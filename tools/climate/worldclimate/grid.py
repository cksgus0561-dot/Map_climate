"""Icosahedral (geodesic) climate grid: cells are the Voronoi regions of the
vertices of a subdivided icosahedron (level 4 -> 2,562 cells, ~450 km apart).
"""
import numpy as np
from scipy.sparse import coo_matrix
from scipy.spatial import SphericalVoronoi

RADIUS_M=6371000.


def _icosahedron():
    t=(1+np.sqrt(5))/2
    v=np.array([[-1,t,0],[1,t,0],[-1,-t,0],[1,-t,0],[0,-1,t],[0,1,t],[0,-1,-t],[0,1,-t],
                [t,0,-1],[t,0,1],[-t,0,-1],[-t,0,1]],float)
    f=[[0,11,5],[0,5,1],[0,1,7],[0,7,10],[0,10,11],[1,5,9],[5,11,4],[11,10,2],[10,7,6],[7,1,8],
       [3,9,4],[3,4,2],[3,2,6],[3,6,8],[3,8,9],[4,9,5],[2,4,11],[6,2,10],[8,6,7],[9,8,1]]
    return v/np.linalg.norm(v,axis=1)[:,None],np.array(f)


def subdivide(level):
    v,f=_icosahedron();v=list(v)
    for _ in range(level):
        cache={};new=[]
        def mid(a,b):
            k=(min(a,b),max(a,b))
            if k not in cache:
                p=v[a]+v[b];v.append(p/np.linalg.norm(p));cache[k]=len(v)-1
            return cache[k]
        for a,b,c in f:
            ab,bc,ca=mid(a,b),mid(b,c),mid(c,a)
            new+=[[a,ab,ca],[b,bc,ab],[c,ca,bc],[ab,bc,ca]]
        f=np.array(new)
    return np.array(v),np.asarray(f)


class Grid:
    def __init__(self,level=4,radius=RADIUS_M):
        self.level=level;self.radius=radius
        self.points,self.triangles=subdivide(level);n=self.n=len(self.points)
        self.lat=np.arcsin(np.clip(self.points[:,2],-1,1));self.lon=np.arctan2(self.points[:,1],self.points[:,0])
        sv=SphericalVoronoi(self.points,radius=1.);sv.sort_vertices_of_regions()
        self.area=sv.calculate_areas()*radius**2
        regions=[set(r) for r in sv.regions];self.voronoi=sv
        edges=set()
        for a,b,c in self.triangles:
            for i,j in ((a,b),(b,c),(c,a)):edges.add((min(i,j),max(i,j)))
        left=[];right=[];length=[];dist=[]
        for i,j in sorted(edges):
            shared=sorted(regions[i]&regions[j])
            if len(shared)!=2:raise ValueError('Voronoi neighbours must share one edge')
            p,q=sv.vertices[shared[0]],sv.vertices[shared[1]]
            length.append(np.arccos(np.clip(p@q,-1,1))*radius)
            dist.append(np.arccos(np.clip(self.points[i]@self.points[j],-1,1))*radius)
            left.append(i);right.append(j)
        self.left=np.array(left);self.right=np.array(right)
        self.edge_length=np.array(length);self.distance=np.array(dist);self.ne=len(left)

    def laplacian(self,conductance=None):
        """Sparse operator L with (L x)_i = (1/A_i) sum_j k_ij l_ij/d_ij (x_j - x_i)."""
        k=self.edge_length/self.distance*(1. if conductance is None else conductance)
        i,j=self.left,self.right;n=self.n
        rows=np.r_[i,j,i,j];cols=np.r_[j,i,i,j];vals=np.r_[k,k,-k,-k]
        L=coo_matrix((vals,(rows,cols)),shape=(n,n)).tocsr()
        return (L.multiply(1/self.area[:,None])).tocsr()
