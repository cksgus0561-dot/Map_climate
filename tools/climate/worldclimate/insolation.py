"""Seasonal-mean daily insolation at the top of the atmosphere.

Seasons are equal quarters of the orbital time starting at the northern vernal
equinox (season 0 = northern spring). Orbit and solar constant from the
project profile (engine.astronomy, engine.profile). Each season averages the
daily-mean insolation over `samples` equally spaced times.
"""
import numpy as np
from engine.astronomy import season_geometry
from engine.profile import PROFILE


def daily_mean(lat,declination,distance_factor,solar_constant):
    lat=np.asarray(lat)[...,None];dec=np.asarray(declination)
    h0=np.arccos(np.clip(-np.tan(lat)*np.tan(dec),-1,1))
    return solar_constant/np.pi*distance_factor*(h0*np.sin(lat)*np.sin(dec)+np.cos(lat)*np.cos(dec)*np.sin(h0))


def seasonal_insolation(lat,seasons=4,samples=32,obliquity_deg=None):
    """(seasons, cells) array [W/m2]."""
    saved=PROFILE['planet']['obliquity_deg']
    if obliquity_deg is not None:PROFILE['planet']['obliquity_deg']=float(obliquity_deg)
    try:
        out=[]
        for s in range(seasons):
            phases=(s+(np.arange(samples)+.5)/samples)/seasons
            g=season_geometry(phases)
            out.append(daily_mean(lat,g['declination'],g['distance_factor'],PROFILE['planet']['solar_constant_w_m2']).mean(axis=-1))
        return np.array(out)
    finally:PROFILE['planet']['obliquity_deg']=saved
