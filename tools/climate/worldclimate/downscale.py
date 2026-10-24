"""W13: rain inside a climate cell, per map pixel, from the terrain and the season's wind.

The climate grid (~480 km) gives one rain value per cell; a narrow range inside one cell (southern
Andes, a range the user draws) then gets the same rain on both sides.  The map layers redistribute
each cell's rain over its own land pixels, as the temperature already follows the pixel height:

  * windward / leeward slope: w = V . grad h of the smoothed pixel terrain (the surface wind lifted
    or let down by the slope) -> factor exp(clip(w / w0, -k, k))
  * rain shadow: the highest ground up to `shadow_km` upwind; where it stands more than
    `shadow_free_m` above the pixel the air has descended from it -> factor exp(-excess / shadow_m)
  * where the wind is weak the factors fade out (no direction to tell windward from leeward)
  * each cell keeps its rain: the factors are divided by their (area-weighted) mean over the cell's
    land pixels, interpolated between cells like the rain itself, so the map shows where in the cell
    it rains, not more or less rain.  Sea pixels are unchanged.
Diagnostic only: the climate model and climate.json (cell values) do not change.
"""
from dataclasses import dataclass
import numpy as np
from scipy import ndimage

R_EARTH=6.371e6


@dataclass
class DownscaleParams:
    w0_m_s:float=.05             # upslope wind speed for a factor e (V 5 m/s on a 1 % slope)
    slope_clip:float=1.5         # factor between e^-1.5 and e^1.5 from the slope alone
    shadow_km:float=400.
    shadow_free_m:float=100.     # upwind ground less than this above the pixel casts no shadow
    shadow_m:float=500.          # e-folding of the shadow per metre of upwind barrier above that (Earth pixel points: 0.85 -> 0.77 log error)
    calm_m_s:float=1.            # wind speed at which the factors count half
    smooth_px:float=1.           # terrain smoothing before slopes [pixels]
    clip:tuple=(.15,5.)          # final per-pixel factor limits


def factors(frame,u,v,params=None):
    """Per season the raw rain factor of every pixel (4, N) from the cell winds u, v (east, north) [m/s]."""
    p=params or DownscaleParams();Hh,Ww=frame.H,frame.W
    h=ndimage.gaussian_filter(np.maximum(frame.h,0.).reshape(Hh,Ww),p.smooth_px,mode=('nearest','wrap'))
    lat=np.radians(frame.lat.reshape(Hh,Ww));dy=np.pi*R_EARTH/Hh;dx=2*np.pi*R_EARTH/Ww*np.maximum(np.cos(lat),.05)
    gn=np.zeros_like(h);gn[1:-1]=(h[:-2]-h[2:])/(2*dy)                   # row 0 is the north edge
    ge=(np.roll(h,-1,1)-np.roll(h,1,1))/(2*dx)
    iy,ix=np.mgrid[0:Hh,0:Ww];out=[]
    for us,vs in zip(u,v):
        ue=frame.smooth(us).reshape(Hh,Ww);vn=frame.smooth(vs).reshape(Hh,Ww);V=np.hypot(ue,vn)
        calm=V/(V+p.calm_m_s);w=ue*ge+vn*gn
        slope=np.clip(w/p.w0_m_s,-p.slope_clip,p.slope_clip)
        # highest ground upwind (straight line against the wind, in steps of about one pixel)
        step=max(dy,1.);n=int(p.shadow_km*1e3/step);top=np.zeros_like(h)
        de=-ue/np.maximum(V,1e-6);dn=-vn/np.maximum(V,1e-6)
        for k in range(1,n+1):
            d=k*step;yy=np.clip(np.rint(iy-dn*d/dy),0,Hh-1).astype(int);xx=np.rint(ix+de*d/dx).astype(int)%Ww
            top=np.maximum(top,h[yy,xx])
        shadow=-np.maximum(top-h-p.shadow_free_m,0.)/p.shadow_m
        out.append(np.exp(calm*(slope+shadow)).ravel())
    return np.array(out)


def rain(frame,P,u,v,params=None):
    """Pixel rain (4, N) [same unit as P]: P (4, N) interpolated cell rain, redistributed inside each cell."""
    p=params or DownscaleParams();F=factors(frame,u,v,p);land=frame.land;n=frame.g.n
    wgt=np.cos(np.radians(frame.lat))*land;out=np.array(P,float).copy()
    for s in range(len(F)):
        num=np.bincount(frame.near,F[s]*wgt,n);den=np.bincount(frame.near,wgt,n)
        mean=np.where(den>0,num/np.maximum(den,1e-30),1.)
        f=np.clip(F[s]/frame.smooth(mean),*p.clip)
        out[s]=np.where(land,P[s]*f,P[s])
    return out
