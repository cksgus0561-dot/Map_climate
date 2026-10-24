"""Fictional test worlds painted the way a user paints in the editor.

The editor (src/TheGarden.Core/TerrainEditing.cs) stamps a brush along a great-circle
stroke every 0.18 radius with falloff (1 - (d/r)^2)^2; Raise adds `strength` metres per
stamp.  Here the same stamping is done on a fine sample set (0.25 deg), starting from a
flat ocean floor, so the climate code sees the kind of terrain users actually make:
blob continents, dragged mountain ridges, chains of islands, any sea level.
These worlds are for judging the model; Earth is only one extra reference.
"""
import numpy as np

FLOOR=-3500.


def _unit(lat,lon):
    lat=np.radians(lat);lon=np.radians(lon)
    return np.stack([np.cos(lat)*np.cos(lon),np.cos(lat)*np.sin(lon),np.sin(lat)],-1)


class Canvas:
    def __init__(self,step_deg=.25,floor=FLOOR,radius_m=6.371e6):
        lat=np.arange(-90+step_deg/2,90,step_deg);lon=np.arange(-180+step_deg/2,180,step_deg)
        LA,LO=np.meshgrid(lat,lon,indexing='ij');self.lat=LA.ravel();self.lon=LO.ravel()
        self.p=_unit(self.lat,self.lon);self.weight=np.cos(np.radians(self.lat))
        self.h=np.full(len(self.lat),float(floor));self.R=radius_m

    def stamp(self,lat,lon,radius_km,strength_m):
        c=_unit(lat,lon);d=np.arccos(np.clip(self.p@c,-1,1))*self.R/(radius_km*1e3)
        m=d<1;self.h[m]+=strength_m*(1-d[m]**2)**2

    def stroke(self,path,radius_km,strength_m):
        """path: list of (lat, lon) way points; stamps every 0.18 radius along great circles."""
        for (a,b),(c,d) in zip(path[:-1],path[1:]):
            p,q=_unit(a,b),_unit(c,d);ang=np.arccos(np.clip(p@q,-1,1))
            k=max(1,int(np.ceil(ang*self.R/(.18*radius_km*1e3))))
            for t in np.linspace(0,1,k,endpoint=(c,d)==tuple(path[-1])):
                x=(np.sin((1-t)*ang)*p+np.sin(t*ang)*q)/np.sin(ang) if ang>1e-9 else p
                la=np.degrees(np.arcsin(np.clip(x[2],-1,1)));lo=np.degrees(np.arctan2(x[1],x[0]))
                self.stamp(la,lo,radius_km,strength_m)

    def flatten(self,lat,lon,radius_km,target_m,amount=.9):
        """Editor Flatten: move height toward target with the brush falloff."""
        c=_unit(lat,lon);d=np.arccos(np.clip(self.p@c,-1,1))*self.R/(radius_km*1e3)
        m=d<1;self.h[m]+=amount*(1-d[m]**2)**2*(target_m-self.h[m])

    def blob(self,lat,lon,radius_km,target_m=400.,layers=7):
        """A continent blocked in the way users do: overlapping Flatten stamps to a lowland
        height (irregular outline), then the coast stays where the blend crosses sea level."""
        rng=np.random.default_rng(int(abs(lat*1000+lon*7))%2**32)
        for _ in range(layers):
            dl=rng.normal(0,.3)*radius_km/111.;dn=rng.normal(0,.3)*radius_km/111./max(np.cos(np.radians(lat)),.2)
            self.flatten(lat+dl,lon+dn,radius_km*rng.uniform(.7,1.1),target_m*rng.uniform(.6,1.4))

    def on(self,grid,sea_level=0.):
        from .terrain import from_samples
        return from_samples(grid,np.radians(self.lat),np.radians(self.lon),self.h,self.weight,sea_level=sea_level)


def supercontinent():
    c=Canvas()
    c.blob(10,0,6000);c.blob(-25,15,4500);c.blob(40,-20,4200);c.blob(35,40,4200)
    c.stroke([(45,-35),(30,-10),(20,25),(30,55)],450,550)        # dragged east-west mountain belt
    c.stroke([(-10,-25),(-40,-5)],300,400)
    return c


def archipelago():
    c=Canvas();rng=np.random.default_rng(3)
    for _ in range(40):
        c.blob(rng.uniform(-50,50),rng.uniform(-180,180),rng.uniform(250,700),rng.uniform(150,600),layers=3)
    c.stroke([(-5,120),(10,150),(25,170)],250,900)               # volcanic arc
    return c


def polar_and_midlatitude():
    c=Canvas()
    c.blob(-85,0,3500,1500);c.blob(-75,90,2000,1200)            # pole-centred continent
    c.blob(45,60,4500);c.blob(30,100,3200)             # big northern mid-latitude continent
    c.stroke([(35,40),(40,80),(32,110)],450,900)                # high plateau / range
    return c


def meridional_range():
    """One tropical-to-midlatitude continent with a long north-south range near its west coast."""
    c=Canvas()
    c.blob(0,-60,3300);c.blob(30,-70,3300);c.blob(-30,-60,3000)
    c.stroke([(50,-85),(20,-82),(-10,-76),(-45,-70)],300,800)
    c.blob(20,60,3800)
    return c


def inland_sea():
    """Two continents almost touching, enclosing a narrow inland sea."""
    c=Canvas()
    c.blob(42,0,4300);c.blob(5,10,4200)
    c.stroke([(28,-30),(25,0),(28,35)],280,-900)                # Lower brush: carve the sea
    return c


WORLDS=dict(supercontinent=supercontinent,archipelago=archipelago,polar_and_midlatitude=polar_and_midlatitude,
            meridional_range=meridional_range,inland_sea=inland_sea)


def basins():
    """One large continent with two hollows carved by the Lower brush: one in the dry subtropical
    interior, one in the wet mid-latitude west; plus a coastal range."""
    c=Canvas()
    c.blob(25,20,4200);c.blob(50,0,3500);c.blob(5,30,3200)
    c.stroke([(60,-25),(45,-20),(32,-14)],300,700)                # west coast range (rain on the windward side)
    c.flatten(25,30,600,120.);c.flatten(25,30,400,40.)            # dry interior hollow (above sea level: below it the
                                                                  # editor and the climate model both show sea)
    c.flatten(52,5,450,500.);c.flatten(52,5,250,30.)             # wet mid-latitude hollow: rim 500 m, floor 30 m
    return c


WORLDS['basins']=basins
