"""Vector terrain on the sphere, independent of any climate engine's grid.

Vertices carry elevations; oriented great-circle triangles carry a continuous
projectively linear height surface. Coastlines are h=sea_level intersections,
not the sign of a cell-mean height. This is a terrain/geometry kernel, not a
general constrained mesh generator or a validated climate discretization.
"""
from __future__ import annotations

import json
import hashlib
import struct
import zipfile
from pathlib import Path
import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components


def unit(x):
    x = np.asarray(x, float)
    length = np.linalg.norm(x, axis=-1, keepdims=True)
    if not np.isfinite(x).all() or np.any(length == 0):
        raise ValueError('Finite nonzero directions required')
    return x / length


def lon_lat_directions(lon_deg, lat_deg):
    lon, lat = np.broadcast_arrays(np.asarray(lon_deg, float), np.asarray(lat_deg, float))
    if not np.isfinite(lon).all() or not np.isfinite(lat).all() or np.any(abs(lat) > 90):
        raise ValueError('Finite longitude and latitude in degrees required')
    lon, lat = np.deg2rad(lon), np.deg2rad(lat)
    return np.stack((np.cos(lat)*np.cos(lon), np.cos(lat)*np.sin(lon), np.sin(lat)), axis=-1)


def arc_angle(a, b):
    return np.arctan2(np.linalg.norm(np.cross(a, b), axis=-1), np.sum(a*b, axis=-1))


def triangle_solid_angle(a, b, c):
    return 2*np.arctan2(np.sum(a*np.cross(b, c), axis=-1),
                        1+np.sum(a*b, axis=-1)+np.sum(b*c, axis=-1)+np.sum(c*a, axis=-1))


class VectorTerrain:
    def __init__(self, directions, elevation_m, faces, *, radius_m=6371000.,
                 sea_level_m=0., features=(), require_closed=False, height_weights=None):
        p = np.asarray(directions, float)
        h = np.asarray(elevation_m, float)
        f = np.asarray(faces)
        if p.ndim != 2 or p.shape[1] != 3 or h.shape != (len(p),):
            raise ValueError('One elevation and XYZ direction per vertex required')
        if not np.isfinite(h).all() or not np.isfinite(radius_m) or radius_m <= 0 or not np.isfinite(sea_level_m):
            raise ValueError('Finite elevations, datum and positive radius required')
        if f.ndim != 2 or f.shape[1] != 3 or not len(f) or f.dtype.kind not in 'iu':
            raise ValueError('Integer triangular faces required')
        if np.any(f < 0) or np.any(f >= len(p)) or len(np.unique(f)) != len(p):
            raise ValueError('Invalid or unused terrain vertices')
        p = unit(p)
        a, b, c = p[f[:, 0]], p[f[:, 1]], p[f[:, 2]]
        omega = triangle_solid_angle(a, b, c)
        if np.any(omega <= 1e-15) or np.any(omega >= 2*np.pi) or np.any(np.linalg.det(p[f]) <= 1e-15):
            raise ValueError('Nondegenerate counterclockwise minor spherical triangles required')
        if len({tuple(sorted(row)) for row in f}) != len(f):
            raise ValueError('Duplicate terrain face')
        self.points, self.height, self.faces = p.copy(), h.copy(), f.astype(int).copy()
        q = np.ones(len(p)) if height_weights is None else np.asarray(height_weights, float)
        if q.shape != (len(p),) or not np.isfinite(q).all() or np.any(q <= 0):
            raise ValueError('One finite positive interpolation weight per vertex required')
        self.height_weights = q.copy()
        self.radius_m, self.sea_level_m = float(radius_m), float(sea_level_m)
        self.area_m2 = omega * radius_m**2
        self.centers = unit(p[f].sum(axis=1))
        self._inverse = np.linalg.inv(np.transpose(p[f], (0, 2, 1)))
        entries = {}
        for face, row in enumerate(f):
            for i, j in zip(row, np.roll(row, -1)):
                key = tuple(sorted((int(i), int(j))))
                entries.setdefault(key, []).append((face, 1 if i < j else -1))
        for neighbors in entries.values():
            if len(neighbors) > 2 or (len(neighbors) == 2 and neighbors[0][1] == neighbors[1][1]):
                raise ValueError('Nonmanifold or inconsistently oriented edge')
        self.edges = np.array(sorted(entries), int)
        self.neighbors = [entries[tuple(edge)] for edge in self.edges]
        self.boundary = np.array([len(n) == 1 for n in self.neighbors])
        self.closed = not bool(self.boundary.any())
        if require_closed and not self.closed:
            raise ValueError('Open boundary in requested planet surface')
        if require_closed and not np.isclose(omega.sum(), 4*np.pi, rtol=1e-12, atol=1e-12):
            raise ValueError('Closed planet does not cover one sphere')
        rows, cols, signs = [], [], []
        for edge, neighbors in enumerate(self.neighbors):
            for face, sign in neighbors:
                rows.append(face); cols.append(edge); signs.append(sign)
        self.incidence = coo_matrix((signs, (rows, cols)), shape=(len(f), len(self.edges))).tocsr()
        pa, pb = p[self.edges[:, 0]], p[self.edges[:, 1]]
        self.edge_angle = arc_angle(pa, pb)
        if np.any(self.edge_angle >= np.pi-1e-12):
            raise ValueError('Antipodal edge has no unique minor arc')
        self.edge_normal = -unit(np.cross(pa, pb))
        self.edge_length_m = radius_m*self.edge_angle
        self.features = tuple(dict(x) for x in features)
        names = set()
        for feature in self.features:
            if set(feature) != {'name', 'kind', 'edges'} or feature['kind'] not in ('coast', 'ridge', 'valley', 'boundary'):
                raise ValueError('Explicit named feature kind and edge chain required')
            if not isinstance(feature['name'], str) or not feature['name'] or feature['name'] in names:
                raise ValueError('Unique nonempty feature name required')
            names.add(feature['name'])
            if not feature['edges']:
                raise ValueError('Empty feature')
            for edge in feature['edges']:
                key = tuple(sorted(edge))
                if len(edge) != 2 or key not in entries:
                    raise ValueError('Feature must be an existing terrain edge')
                if feature['kind'] == 'coast' and np.any(h[list(key)] != sea_level_m):
                    raise ValueError('A declared coastline must lie at sea level')

    @classmethod
    def load(cls, path):
        if Path(path).suffix.lower() == '.garden':
            return cls._load_garden(path)
        d = json.loads(Path(path).read_text(encoding='utf-8'))
        if d.get('schema') != 'garden-vector-terrain-v1':
            raise ValueError('Unsupported vector terrain schema')
        if d.get('domain') not in ('planet', 'patch'):
            raise ValueError('Explicit planet or patch domain required')
        interpolation = d.get('height_interpolation')
        if interpolation not in ('projective-linear-on-great-circle-triangles',
                                  'weighted-projective-linear-on-great-circle-triangles'):
            raise ValueError('Explicit height interpolation required')
        weights = d['vertex_height_weights'] if interpolation.startswith('weighted-') else None
        v = np.asarray(d['vertices_lon_lat_elevation'], float)
        if v.ndim != 2 or v.shape[1] != 3:
            raise ValueError('Vertices must contain longitude deg, latitude deg, elevation m')
        return cls(lon_lat_directions(v[:, 0], v[:, 1]), v[:, 2], np.asarray(d['faces']),
                   radius_m=d['radius_m'], sea_level_m=d['sea_level_m'],
                   features=d['features'], require_closed=d['domain'] == 'planet', height_weights=weights)

    @classmethod
    def _load_garden(cls, path):
        """Native editor v2 source, with its checksum and no display raster."""
        with zipfile.ZipFile(path) as archive:
            if sorted(archive.namelist()) != ['faces.i32', 'vertices.bin', 'world.json']:
                raise ValueError('Native vector .garden v2 entries required')
            if archive.getinfo('world.json').file_size > 65536:
                raise ValueError('Oversized manifest')
            m = json.loads(archive.read('world.json'))
            if m.get('FormatVersion') != 2 or m.get('Grid') != 'spherical-triangle-surface-v1' or m.get('ElevationUnits') != 'm':
                raise ValueError('Only native vector .garden v2 is supported')
            nv, nf = m['VertexCount'], m['FaceCount']
            if type(nv) is not int or type(nf) is not int or not (4 <= nv <= 2_000_000 and 4 <= nf <= 4_000_000):
                raise ValueError('Invalid terrain size')
            if archive.getinfo('vertices.bin').file_size != 36*nv or archive.getinfo('faces.i32').file_size != 12*nf:
                raise ValueError('Invalid native terrain byte length')
            vb, fb = archive.read('vertices.bin'), archive.read('faces.i32')
        radius, sea = m['Planet']['RadiusMeters'], m['SeaLevelMeters']
        digest = hashlib.sha256(b'garden-vector-terrain-v1\0')
        digest.update(struct.pack('<dfii', radius, sea, nv, nf)); digest.update(vb); digest.update(fb)
        if digest.hexdigest() != m['TerrainSha256']:
            raise ValueError('Native terrain checksum mismatch')
        v = np.frombuffer(vb, dtype=[('xyz','<f8',(3,)), ('height','<f4'), ('weight','<f8')])
        # WPF (east, north, Greenwich) -> scientific Cartesian (Greenwich, east, north).
        return cls(v['xyz'][:, [2, 0, 1]], v['height'], np.frombuffer(fb,dtype='<i4').reshape(nf,3),
                   radius_m=radius, sea_level_m=sea, height_weights=v['weight'], require_closed=True)

    def height_and_gradient(self, face, directions):
        """Height and tangent gradient [m/m], without averaging ridge and plain."""
        x = unit(directions)
        w = np.einsum('ij,...j->...i', self._inverse[face], x)
        q = self.height_weights[self.faces[face]]
        w = w*q
        denominator = w.sum(axis=-1)
        if np.any(denominator <= 0) or np.any(w/denominator[..., None] < -1e-12):
            raise ValueError('Direction outside declared terrain face')
        h = np.sum(w*self.height[self.faces[face]], axis=-1)/denominator
        a = self._inverse[face].T @ (q*self.height[self.faces[face]])
        b = self._inverse[face].T @ q
        grad = (a-h[..., None]*b)/denominator[..., None]
        grad = (grad-np.sum(grad*x, axis=-1)[..., None]*x)/self.radius_m
        return h, grad

    def ocean_intervals(self):
        """Wet arc interval [0,1] per edge from the actual height zero crossing."""
        out = np.zeros((len(self.edges), 2))
        for k, (i, j) in enumerate(self.edges):
            hi, hj = (self.height[[i, j]]-self.sea_level_m)*self.height_weights[[i, j]]
            if hi < 0 and hj < 0:
                out[k] = (0, 1)
            elif hi < 0 or hj < 0:
                t = -hi/(hj-hi)
                cross = unit((1-t)*self.points[i]+t*self.points[j])
                fraction = float(arc_angle(self.points[i], cross)/self.edge_angle[k])
                out[k] = (0, fraction) if hi < 0 else (fraction, 1)
        return out

    def ocean_area_m2(self):
        """Clip each curved face at sea level; retain mixed land/ocean faces."""
        areas = []
        for row in self.faces:
            polygon = []
            for i, j in zip(row, np.roll(row, -1)):
                hi, hj = (self.height[[i, j]]-self.sea_level_m)*self.height_weights[[i, j]]
                if hi < 0:
                    polygon.append(self.points[i])
                if (hi < 0) != (hj < 0):
                    t = -hi/(hj-hi)
                    polygon.append(unit((1-t)*self.points[i]+t*self.points[j]))
            area = 0.
            for j in range(1, len(polygon)-1):
                area += float(triangle_solid_angle(polygon[0], polygon[j], polygon[j+1]))
            areas.append(area*self.radius_m**2)
        return np.array(areas)

    def ocean_components(self):
        wet = self.ocean_area_m2() > 0
        intervals = self.ocean_intervals()
        rows, cols = [], []
        for k, neighbors in enumerate(self.neighbors):
            if len(neighbors) == 2 and intervals[k, 1] > intervals[k, 0]:
                i, j = neighbors[0][0], neighbors[1][0]
                rows.extend((i, j)); cols.extend((j, i))
        graph = coo_matrix((np.ones(len(rows)), (rows, cols)), shape=(len(wet), len(wet))).tocsr()
        count, labels = connected_components(graph[wet][:, wet], directed=False)
        result = np.full(len(wet), -1, int); result[wet] = labels
        return int(count), result

    def edge_quadrature(self, edge, start=0., stop=1., order=8):
        if not (0 <= start <= stop <= 1) or type(order) is not int or order < 1:
            raise ValueError('Valid arc interval and positive quadrature order required')
        xi, weight = np.polynomial.legendre.leggauss(order)
        t = start+(xi+1)*(stop-start)/2
        a, b = self.points[self.edges[edge]]; theta = self.edge_angle[edge]
        x = (np.sin((1-t)*theta)[:, None]*a+np.sin(t*theta)[:, None]*b)/np.sin(theta)
        return x, weight*(stop-start)*self.edge_length_m[edge]/2

