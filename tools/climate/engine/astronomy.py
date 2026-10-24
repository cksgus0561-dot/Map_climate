"""Keplerian phase and stationary diurnal solar geometry, no weather clock."""
import numpy as np
from .profile import PROFILE,YEAR

def season_geometry(phases):
    phi=np.asarray(phases,float)
    e=PROFILE['planet']['eccentricity']
    peri=np.deg2rad(PROFILE['planet']['perihelion_solar_longitude_deg'])
    # Phase zero = northern vernal equinox; mean anomaly accounts for dwell time.
    nu0=-peri
    E0=2*np.arctan2(np.sqrt(1-e)*np.sin(nu0/2),np.sqrt(1+e)*np.cos(nu0/2))
    M0=E0-e*np.sin(E0)
    M=2*np.pi*phi+M0;E=M.copy()
    for _ in range(8):E-=(E-e*np.sin(E)-M)/(1-e*np.cos(E))
    nu=2*np.arctan2(np.sqrt(1+e)*np.sin(E/2),np.sqrt(1-e)*np.cos(E/2))
    solar_longitude=nu+peri
    dec=np.arcsin(np.sin(np.deg2rad(PROFILE['planet']['obliquity_deg']))*np.sin(solar_longitude))
    return dict(declination=dec,distance_factor=1/(1-e*np.cos(E))**2,
                solar_longitude=np.mod(solar_longitude,2*np.pi))

def periodic_derivative(n,year=YEAR):
    """Conservative cyclic FV derivative on equal intervals, states at ends.

    This matrix is applied to all states simultaneously. Its left null vector
    is the annual sum; no initial-year state or history is provided.
    """
    if n<2:raise ValueError('At least two seasonal phases required')
    return (np.eye(n)-np.roll(np.eye(n),-1,axis=1))*n/year

def storage_rate(storage):
    return (storage-np.roll(storage,1,axis=0))*len(storage)/YEAR
