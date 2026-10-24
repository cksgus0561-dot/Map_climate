"""W5: snow, glaciers and sea ice from the seasonal climate (diagnostic, no feedback beyond energy.py's ice albedo).

Snow on land (per cell, seasons are equal quarters of the year):
  * snowfall = P * rain/snow split, snow share = logistic((273.65 - T) / 1.2 K)
  * melt = degree-day factor * max(T - 273.15, 0) * season length (smooth max), capped by the pack
  * the pack is iterated through the seasons year after year; a cell whose pack still grows after
    the spin-up keeps snow all year: glacier / ice sheet (annual accumulation > annual melt); so does a
    cell that never thaws (warmest season below -2 C) with any snowfall (dry ice-sheet interiors)
Sea ice: concentration from the ocean temperature with the energy model's ice curve.
"""
from dataclasses import dataclass
import numpy as np

DAY=86400.


@dataclass
class CryoParams:
    degree_day_mm_k_day:float=3.5
    snow_split_k:float=273.65
    snow_width_k:float=1.2
    melt_smooth_k:float=1.
    spinup_years:int=60
    glacier_growth_mm_yr:float=1.    # pack growth per year at the end of spin-up that marks a glacier
    sea_ice_freeze_k:float=269.
    sea_ice_width_k:float=1.5


def snow(T,P,land,params=None,seasons=4):
    """T [K], P [kg/m2/s] arrays (seasons, n). Returns dict of pack [mm w.e.], cover, glacier mask, ..."""
    p=params or CryoParams();T=np.asarray(T);P=np.asarray(P);dt=365.2422/seasons
    share=1/(1+np.exp((T-p.snow_split_k)/p.snow_width_k))
    fall=P*DAY*dt*share                                                  # mm per season
    x=(T-273.15)/p.melt_smooth_k;warm=p.melt_smooth_k*np.where(x>30,x,np.log1p(np.exp(np.minimum(x,30))))
    pot=p.degree_day_mm_k_day*warm*dt                                     # potential melt, mm per season
    S=np.zeros(T.shape[1]);hist=[]
    for year in range(p.spinup_years):
        start=S.copy();packs=[]
        for s in range(seasons):
            S=S+fall[s];m=np.minimum(pot[s],S);S=S-m;packs.append(S.copy())
        hist.append(S-start)
    pack=np.array(packs);growth=hist[-1]
    never_melts=(T.max(0)<271.15)&(fall.sum(0)>5.)                         # cold deserts of ice sheets too
    glacier=land&((growth>p.glacier_growth_mm_yr)|never_melts)
    melt=np.minimum(pot,pack+fall)                                        # approximate per-season melt
    cover=np.clip(pack/50.,0,1)*land                                      # 50 mm w.e. ~ full cover
    return dict(pack_mm=np.where(land,pack,0.),cover=np.where(glacier,1.,cover),glacier=glacier,
                snowfall_mm_season=fall*land,melt_mm_season=melt*land,growth_mm_yr=growth*land,
                snow_seasons=np.where(glacier,seasons,(pack>10).sum(0))*land)


def sea_ice(T,ocean,params=None):
    p=params or CryoParams()
    c=1/(1+np.exp((np.asarray(T)-p.sea_ice_freeze_k)/p.sea_ice_width_k))
    return np.where(ocean,c,0.)
