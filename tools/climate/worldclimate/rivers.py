"""W5: rivers, lakes and closed (endorheic) basins on the user's own terrain.

The climate grid (~480 km) is far too coarse for rivers, so the terrain is re-sampled on a fine
latitude-longitude raster (default 0.5 deg ~ 55 km) straight from the editor source, and the
climate fields (runoff, open-water evaporation, rain) are brought to it from the nearest climate cell.

  1. priority flood from the sea (Barnes et al. 2014, with a tiny epsilon) fills every depression to
     its spill level, so every land cell drains; flow goes to the steepest lower neighbour (D8) on the
     filled surface; discharge accumulates downstream [m3/s]
  2. every depression (filled above the ground) is a candidate lake.  Its inflow Q is compared with
     what a lake covering the whole depression would lose, (E_open - P) * area:
       Q >= loss  -> the lake fills and spills: open (fresh) lake, water continues downstream
       Q <  loss  -> closed basin: lake area = Q / (E_open - P) on the lowest ground, no outflow
                     (salt lake / playa); downstream discharge is recomputed without it
     repeated until no basin changes (closing an upstream basin can close a downstream one)
  3. (2026-10-21) routing uses the *lowest* ground of each raster pixel, not its mean: a valley or gorge
     narrower than a pixel still drains (with the mean the Congo gorge closed and the Congo basin
     became a 0.7 M km2 lake spilling into the Nile).  An overflowing lake cuts its own outlet, the
     deeper the more water leaves (stream-power incision ~ Q^0.5): the level drops by
     cut = breach_m sqrt(Q / 1000 m3/s) (10 m for 1000 m3/s, 70 m for an Amazon-size 50 000 m3/s).
     The lake keeps the ground deeper than the cut; the lowest `wetland_m` of the drained floor is
     wetland.  A shallow lowland basin crossed by a big river thus drains (a user's overlapping
     flatten stamps made 1.4 M km2 lakes 40 m deep), a deep one stays a lake with a marshy edge.
     Closed basins have no outflow to cut and keep their level.
"""
import heapq
import numpy as np
from scipy.spatial import cKDTree
from scipy import ndimage

R_EARTH=6.371e6


class Raster:
    def __init__(self,step_deg=.5):
        self.step=step_deg;self.ny=int(round(180/step_deg));self.nx=int(round(360/step_deg))
        self.lat=90-(np.arange(self.ny)+.5)*step_deg;self.lon=-180+(np.arange(self.nx)+.5)*step_deg
        self.area=(np.radians(step_deg)*R_EARTH)**2*np.cos(np.radians(self.lat))[:,None]*np.ones(self.nx)
        LA,LO=np.meshgrid(np.radians(self.lat),np.radians(self.lon),indexing='ij')
        self.points=np.stack([np.cos(LA)*np.cos(LO),np.cos(LA)*np.sin(LO),np.sin(LA)],-1)

    def bin(self,points,values,stat='mean'):
        """Mean (or 'min') of samples (unit vectors) per raster cell; empty cells take the nearest filled cell."""
        lat=np.degrees(np.arcsin(np.clip(points[:,2],-1,1)));lon=np.degrees(np.arctan2(points[:,1],points[:,0]))
        iy=np.clip(((90-lat)/self.step).astype(int),0,self.ny-1);ix=np.clip(((lon+180)/self.step).astype(int),0,self.nx-1)
        k=iy*self.nx+ix;n=self.ny*self.nx
        c=np.bincount(k,minlength=n);out=np.full(n,np.nan)
        if stat=='min':
            m=np.full(n,np.inf);np.minimum.at(m,k,values);out[c>0]=m[c>0]
        else:
            s=np.bincount(k,values,minlength=n);out[c>0]=s[c>0]/c[c>0]
        if (c==0).any():
            P=self.points.reshape(-1,3);t=cKDTree(P[c>0]);out[c==0]=out[c>0][t.query(P[c==0])[1]]
        return out.reshape(self.ny,self.nx)

    def from_grid(self,grid,values):
        """Climate-cell field -> raster (nearest cell)."""
        idx=cKDTree(grid.points).query(self.points.reshape(-1,3))[1]
        return np.asarray(values)[idx].reshape(self.ny,self.nx)


def terrain_raster(source,raster,sea_level=0.,stat='mean'):
    """Height above sea level [m] on the raster from: a VectorTerrain / .garden path, a testworlds.Canvas,
    or (lat_deg, lon_deg, h) sample arrays.  stat 'mean' for display, 'min' for routing."""
    from .terrain import load_vector,vector_samples
    if hasattr(source,'h') and hasattr(source,'p'):                        # testworlds.Canvas
        return raster.bin(source.p,source.h,stat)-sea_level
    if isinstance(source,tuple):
        lat,lon,h=source;la,lo=np.radians(lat),np.radians(lon)
        return raster.bin(np.stack([np.cos(la)*np.cos(lo),np.cos(la)*np.sin(lo),np.sin(la)],-1),h,stat)-sea_level
    vt=load_vector(source);pts,h,_=vector_samples(vt,raster.area.max()/4)       # ~4 samples per equatorial pixel
    return raster.bin(pts,h,stat)-vt.sea_level_m


_NB=[(-1,-1),(-1,0),(-1,1),(0,-1),(0,1),(1,-1),(1,0),(1,1)]


def priority_flood(h,eps=1e-5):
    """Filled surface and drainage receivers (flat index, -1 for sea / sink) on a lon-periodic raster."""
    ny,nx=h.shape;sea=h<=0;filled=h.astype(float).copy();closed=sea.copy();heap=[]
    if not sea.any():                                                    # no sea: drain to the lowest point
        k=int(np.argmin(h));sea=np.zeros_like(sea);sea.flat[k]=True;closed.flat[k]=True
    border=sea&~ndimage.binary_erosion(sea,structure=np.ones((3,3)),border_value=1)
    for k in np.flatnonzero(border):heapq.heappush(heap,(min(filled.flat[k],0.),int(k)))
    while heap:
        z,k=heapq.heappop(heap);y,x=divmod(k,nx)
        for dy,dx in _NB:
            yy=y+dy
            if yy<0 or yy>=ny:continue
            kk=yy*nx+(x+dx)%nx
            if closed.flat[kk]:continue
            closed.flat[kk]=True
            if filled.flat[kk]<=z+eps:filled.flat[kk]=z+eps
            heapq.heappush(heap,(filled.flat[kk],kk))
    # steepest lower neighbour on the filled surface
    lat=np.radians(90-(np.arange(ny)+.5)*180/ny);best=np.zeros((ny,nx));recv=np.full((ny,nx),-1)
    Y,X=np.mgrid[0:ny,0:nx]
    for dy,dx in _NB:
        yy=np.clip(Y+dy,0,ny-1);xx=(X+dx)%nx;valid=(Y+dy>=0)&(Y+dy<ny)
        dist=np.hypot(dx*np.cos(lat)[:,None],dy)+1e-9
        slope=np.where(valid,(filled-filled[yy,xx])/dist,-np.inf)
        better=slope>best;best=np.where(better,slope,best);recv=np.where(better,yy*nx+xx,recv)
    recv[sea]=-1
    return filled,sea,recv.ravel()


def accumulate(filled,recv,source,sinks=None):
    """Downstream sum of `source` [m3/s per cell]; cells in `sinks` keep their water (no outflow)."""
    order=np.argsort(filled.ravel())[::-1];Q=source.ravel().astype(float).copy()
    stop=np.zeros(Q.size,bool) if sinks is None else sinks.ravel()
    for k in order:
        r=recv[k]
        if r>=0 and not stop[k]:Q[r]+=Q[k]
    return Q.reshape(filled.shape)


def roughness(h,amplitude_m=2.,sigma_cells=1.5,seed=0):
    """Small smooth relief added for routing only: perfectly flat brush plateaus would otherwise drain
    in straight parallel lines; real plains have metres of relief that make dendritic networks."""
    r=ndimage.gaussian_filter(np.random.default_rng(seed).normal(0,1,h.shape),sigma_cells,mode=('nearest','wrap'))
    return amplitude_m*r/max(float(r.std()),1e-12)


def route(h,raster,runoff,lake_loss,*,min_depth_m=5.,breach_m=10.,wetland_m=5.,max_passes=6,roughness_m=2.,seed=0,ice=None):
    """h: height above sea [m] (ny, nx); runoff, lake_loss: [kg/m2/s] rasters (lake_loss = E_open - P).

    Returns dict: discharge [m3/s], filled surface, sea mask, lakes (1 open/fresh, 2 closed/salt, 3 wetland),
    closed-basin catchment mask, basin table.
    """
    if roughness_m>0:h=np.where(h>0,np.maximum(h+roughness(h,roughness_m,seed=seed),.01),h)
    filled,sea,recv=priority_flood(h);area=raster.area
    src=np.where(sea,0.,np.maximum(runoff,0)/1000.*area)                  # m3/s
    depth=filled-h;dep=(depth>min_depth_m)&~sea
    if ice is not None:dep&=~ice;src=np.where(ice,0.,src)                 # no lakes or runoff on glaciers
    lab,nb=ndimage.label(dep,structure=np.ones((3,3)))
    flat_lab=lab.ravel();closed=np.zeros(nb+1,bool);sinks=np.zeros(h.size,bool)
    srt=np.argsort(flat_lab,kind='stable');bounds=np.searchsorted(flat_lab[srt],np.arange(nb+2))
    members=[srt[bounds[b]:bounds[b+1]] for b in range(nb+1)]
    lakes=np.zeros(h.shape,np.int8);basins=[]
    for _ in range(max_passes):
        Q=accumulate(filled,recv,src,sinks).ravel();changed=False;basins=[];lakes[:]=0
        for b in range(1,nb+1):
            cells=members[b];out=cells[(recv[cells]<0)|(flat_lab[np.maximum(recv[cells],0)]!=b)]
            exit_=out[np.argmax(Q[out])] if len(out) else cells[np.argmax(Q[cells])]
            # inflow: water reaching the exit when the basin is open; for a closed basin its sink holds it
            inflow=float(Q[exit_]) if not closed[b] else float(Q[cells][sinks[cells]].sum())
            loss=np.maximum(lake_loss.ravel()[cells],1e-12)/1000.*area.ravel()[cells]   # m3/s if fully flooded
            full=float(loss.sum())
            want_closed=inflow<full
            if want_closed!=closed[b]:
                closed[b]=want_closed;changed=True
                sinks[cells]=False
                if want_closed:sinks[exit_]=True
                # route all basin cells to the exit so the sink collects the whole inflow
            if closed[b]:
                order=cells[np.argsort(h.ravel()[cells])];cum=np.cumsum(loss[np.argsort(h.ravel()[cells])])
                wet=order[:max(1,int(np.searchsorted(cum,inflow)))];lakes.ravel()[wet]=2
            else:
                d=depth.ravel()[cells];cut=breach_m*np.sqrt(max(inflow,0.)/1000.)          # outlet cut by the outflow
                above=min(cut,float(d.max()))-d                                            # height above the new level (or the drained bottom)
                lakes.ravel()[cells]=np.where(d>cut,1,np.where(above<=wetland_m,3,0))
            basins.append(dict(id=int(b),cells=int(len(cells)),closed=bool(closed[b]),inflow_m3_s=round(inflow,1),
                               full_lake_loss_m3_s=round(full,1),lake_cells=int(np.isin(lakes.ravel()[cells],(1,2)).sum()),wetland_cells=int((lakes.ravel()[cells]==3).sum()),
                               lat=round(float(raster.lat[exit_//raster.nx]),1),lon=round(float(raster.lon[exit_%raster.nx]),1)))
        if not changed:break
    # catchments of closed basins: cells whose drainage path ends in a sink
    endo=np.zeros(h.size,bool)
    if sinks.any():
        order=np.argsort(filled.ravel())                               # downstream first
        term=np.full(h.size,-1);term[sinks]=np.flatnonzero(sinks)
        for k in order:
            if term[k]<0 and recv[k]>=0 and not sinks[k]:term[k]=term[recv[k]] if term[recv[k]]>=0 else -1
        endo=(term>=0)&~sea.ravel()
    return dict(discharge=Q.reshape(h.shape),filled=filled,sea=sea,lakes=lakes,endorheic=endo.reshape(h.shape),basins=basins,receiver=recv)
