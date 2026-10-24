"""Terrain on the climate grid: land fraction and mean land elevation per cell.

Sources: idealized worlds (aquaplanet, one continent) and the Earth surface
elevation of RTopo-2.0.1 (0.125 deg, already in artifacts/engines/climber-x-input-files).
Fine samples are assigned to the nearest grid cell (cell = Voronoi region) with
area weights cos(lat).
"""
import numpy as np
from scipy.spatial import cKDTree


def _unit(lat,lon):
    return np.stack([np.cos(lat)*np.cos(lon),np.cos(lat)*np.sin(lon),np.sin(lat)],axis=-1)


def from_samples(grid,lat,lon,elevation,weight,sea_level=0.,points=None):
    """Land fraction and mean land height above sea level per cell (samples -> nearest cell)."""
    idx=cKDTree(grid.points).query(_unit(lat,lon) if points is None else points)[1]
    h=np.asarray(elevation,float)-sea_level;land=h>0;n=grid.n
    w=np.bincount(idx,weights=weight,minlength=n)
    wl=np.bincount(idx,weights=weight*land,minlength=n)
    zl=np.bincount(idx,weights=weight*land*np.maximum(h,0),minlength=n)
    f=wl/np.maximum(w,1e-300);z=np.where(wl>0,zl/np.maximum(wl,1e-300),0.)
    return f,z


# barycentric sample pattern inside every editor triangle (7 points, equal weights)
_BARY=np.array([[1/3,1/3,1/3],[.6,.2,.2],[.2,.6,.2],[.2,.2,.6],[.45,.45,.1],[.1,.45,.45],[.45,.1,.45]])


def load_vector(terrain):
    from vector_terrain import VectorTerrain   # tools/climate on the path
    return terrain if isinstance(terrain,VectorTerrain) else VectorTerrain.load(terrain)


def vector_samples(terrain,spacing_area_m2):
    """Points inside every editor triangle with the editor's own height interpolation.

    Each face gets an m x m barycentric sub-triangle lattice (m^2 equal-area centroids) with m chosen so
    one sample covers at most spacing_area_m2.  Returns (unit vectors, heights [m], area weights [m2]).
    """
    terrain=load_vector(terrain)
    m_face=np.maximum(1,np.ceil(np.sqrt(terrain.area_m2/spacing_area_m2))).astype(int)
    pts=[];hs=[];ws=[]
    for m in np.unique(m_face):
        sel=np.flatnonzero(m_face==m);P=terrain.points[terrain.faces[sel]]
        bary=[((i+1/3)/m,(j+1/3)/m,(m-i-j-2/3)/m) for i in range(m) for j in range(m-i)]+             [((i+2/3)/m,(j+2/3)/m,(m-i-j-4/3)/m) for i in range(m-1) for j in range(m-1-i)]
        for b in bary:
            x=np.einsum('k,fkc->fc',np.array(b),P);x/=np.linalg.norm(x,axis=-1)[:,None]
            # editor interpolation h = sum(l q h) / sum(l q), l = A^-1 x  (vector_terrain.height_and_gradient, all faces at once)
            l=np.einsum('fij,fj->fi',terrain._inverse[sel],x)*terrain.height_weights[terrain.faces[sel]]
            h=np.sum(l*terrain.height[terrain.faces[sel]],-1)/l.sum(-1)
            pts.append(x);hs.append(h);ws.append(terrain.area_m2[sel]/len(bary))
    return np.concatenate(pts),np.concatenate(hs),np.concatenate(ws)


def from_vector(grid,terrain):
    """Editor terrain (tools/climate/vector_terrain.VectorTerrain, e.g. a .garden file) on the climate grid.

    Samples (>= ~16 per climate cell, editor interpolation) go to the nearest climate cell with their
    area weight.  Sea level is the world's own SeaLevelMeters.  Returns (land fraction, mean land height
    above sea level [m], highest point above sea level per cell [m]).
    """
    terrain=load_vector(terrain)
    pts,hs,ws=vector_samples(terrain,4*np.pi*terrain.radius_m**2/grid.n/16)
    f,z=from_samples(grid,None,None,hs,ws,sea_level=terrain.sea_level_m,points=pts)
    idx=cKDTree(grid.points).query(pts)[1];zmax=np.full(grid.n,-np.inf)
    np.maximum.at(zmax,idx,hs-terrain.sea_level_m)
    return f,z,np.maximum(zmax,0.)


def aquaplanet(grid):return np.zeros(grid.n),np.zeros(grid.n)


def box_continent(grid,lat_range=(-40,60),lon_range=(-30,30),elevation=300.):
    lat=np.degrees(grid.lat);lon=np.degrees(grid.lon)
    land=(lat>=lat_range[0])&(lat<=lat_range[1])&(lon>=lon_range[0])&(lon<=lon_range[1])
    return land.astype(float),np.where(land,elevation,0.)


def earth_rtopo(grid,path='artifacts/engines/climber-x-input-files/RTopo-2.0.1_0.125deg_DRThydrocorr.nc',stride=2):
    from netCDF4 import Dataset
    with Dataset(path) as d:
        names=list(d.variables)
        lat=np.asarray(d.variables['lat'][::stride],float) if 'lat' in names else np.asarray(d.variables['latitude'][::stride],float)
        lon=np.asarray(d.variables['lon'][::stride],float) if 'lon' in names else np.asarray(d.variables['longitude'][::stride],float)
        key='surface_elevation' if 'surface_elevation' in names else [k for k in names if 'elev' in k or 'topo' in k][0]
        z=np.asarray(d.variables[key][::stride,::stride],float)
    LAT,LON=np.meshgrid(np.radians(lat),np.radians(lon),indexing='ij')
    return from_samples(grid,LAT.ravel(),LON.ravel(),z.ravel(),np.cos(LAT).ravel())
