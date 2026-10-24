"""The planet as seen from space: natural colour of the surface and a cloud deck, per season.

Not a radiative transfer model.  Each land pixel gets a ground cover from its own climate (the same
pixel temperature and rain the Koppen layer uses), and the cover is painted with colours read off
true-colour satellite mosaics:
  * moisture  = annual rain / Koppen dryness threshold (20 T + 0/140/280 mm, by rain season):
    below ~0.5 bare ground (desert), ~0.5-1 sparse grass (steppe), above 1 closed vegetation
  * warmth    = warmest-season temperature: trees need ~10 C (tree line), any plant cover ~0 C
  * dry season = a warm season with rain well below the threshold thins the trees to savanna or
    open woodland (grass shows between them), however wet the rest of the year is
  * phenology = this season's warmth and this season's rain: grass and deciduous trees green up
    in the warm wet season and turn straw/brown in the dry or cold season; rain forest and
    conifers stay evergreen
  * snow      = this season's pixel temperature below freezing with some precipitation (so
    mountains carry their own snow caps); forests hide part of the snow; ice sheets stay white
  * sea       = deep blue, shelves turquoise by depth, sea ice by its seasonal concentration
Bare ground colour varies smoothly between sand, red rock and grey rock by a fixed noise field, so
deserts are not one flat colour; the noise is seeded by the frame only (same terrain, same picture).

Clouds: cover per pixel follows this season's rain (rising air: the ITCZ, storm tracks, monsoons)
plus low cloud over cool water; the shapes come from a warped 3-D noise on the sphere (no seam),
squeezed north-south so systems stretch along the westerlies and trades, and thresholded at the
local cover so the cloud fraction matches the climate.  Cores are opaque, edges feather out.
"""
import numpy as np


def _smooth(a,b,x):
    t=np.clip((np.asarray(x,float)-a)/(b-a),0,1);return t*t*(3-2*t)


def _mix(c0,c1,t):
    t=np.asarray(t,float)[...,None];return np.asarray(c0,float)*(1-t)+np.asarray(c1,float)*t


class Noise3:
    """Value noise on a 3-D lattice (trilinear, smooth fade), evaluated on unit-sphere points."""
    def __init__(self,seed=7,size=64):
        rng=np.random.default_rng(seed);self.n=size;self.v=rng.random((size,size,size)).astype(np.float32)

    def __call__(self,p):
        n=self.n;q=np.asarray(p,np.float64);i=np.floor(q).astype(np.int64);f=q-i;f=f*f*(3-2*f)
        out=np.zeros(len(q),np.float32)
        for dx in (0,1):
            for dy in (0,1):
                for dz in (0,1):
                    w=(f[:,0] if dx else 1-f[:,0])*(f[:,1] if dy else 1-f[:,1])*(f[:,2] if dz else 1-f[:,2])
                    out+=w*self.v[(i[:,0]+dx)%n,(i[:,1]+dy)%n,(i[:,2]+dz)%n]
        return out

    def fbm(self,p,freq,octaves=5,gain=.5):
        s=np.zeros(len(p),np.float32);a=1.;t=0.
        for k in range(octaves):
            s+=a*self((p*freq*2**k)+11.3*k);t+=a;a*=gain
        return s/t


def _uniform(v):
    """Rank-transform to a uniform 0..1 distribution (so thresholds equal area fractions)."""
    r=np.empty(len(v));r[np.argsort(v,kind='stable')]=np.arange(len(v));return (r+.5)/len(v)


# colours (sRGB 0..1) read off true-colour mosaics
SAND=(.86,.74,.55);RED_ROCK=(.70,.46,.31);GREY_ROCK=(.50,.47,.44);TUNDRA=(.46,.44,.36)
DRY_GRASS=(.68,.61,.41);GREEN_GRASS=(.38,.46,.21);LEAF=(.18,.31,.12);BARE_TREES=(.38,.33,.26)
RAIN_FOREST=(.09,.22,.08);CONIFER=(.10,.20,.13);SNOW=(.95,.96,.98);ICE_SHEET=(.92,.95,.99)
DEEP_SEA=(.03,.08,.20);SHELF=(.10,.37,.47);SEA_ICE=(.88,.91,.95);LAKE=(.06,.17,.30);SALT=(.88,.85,.78);MARSH=(.13,.22,.17)

LEGEND=[('눈·빙하·해빙','#f2f4f8'),('상록 숲 (열대 우림·침엽수)','#1a3a1c'),('낙엽 숲·초원 (계절 따라 색이 바뀜)','#556b2f'),
        ('마른 풀밭·스텝','#ad9c69'),('사막 (모래·붉은 바위)','#d8b98a'),('툰드라·고산 바위','#77705e'),
        ('얕은 바다','#1a5e78'),('깊은 바다','#081433')]


def _points(frame):
    lat=np.radians(frame.lat);lon=np.radians(-180+(np.arange(frame.W)+.5)*360/frame.W);lon=np.tile(lon,frame.H)
    return np.stack([np.cos(lat)*np.cos(lon),np.cos(lat)*np.sin(lon),np.sin(lat)],-1)


def surface(frame,Tpx,Ppx,*,glacier,sea_ice,lakes=None,seed=7):
    """RGB (4, N, 3) for the four seasons (0 Mar-May, 1 Jun-Aug, 2 Sep-Nov, 3 Dec-Feb).

    Tpx (4,N) pixel temperature C, Ppx (4,N) pixel rain mm/day, glacier (N,) ice-sheet fraction 0..1
    (interpolated between climate cells; its edge is frayed by noise so it does not follow the cells),
    sea_ice (4,N) concentration 0..1, lakes (N,) 0 none / 1 fresh / 2 salt / 3 wetland."""
    land=frame.land;h=frame.h;p=_points(frame);nz=Noise3(seed)
    Tann=Tpx.mean(0);Pmm=Ppx.mean(0)*365.24;Twarm=Tpx.max(0);Tcold=Tpx.min(0)
    # Koppen dryness threshold by rain season (north/south summer = seasons 1/3)
    summer=np.where(frame.lat>=0,Ppx[1],Ppx[3]);winter=np.where(frame.lat>=0,Ppx[3],Ppx[1])
    share=summer/np.maximum(summer+winter,1e-9);add=np.where(share>.7,280.,np.where(share<.3,0.,140.))
    Pth=np.maximum(20*Tann+add,40.);moist=Pmm/Pth
    herb=_smooth(.35,1.1,moist)*_smooth(-1.,8.,Twarm)
    # a long warm dry season keeps trees sparse (savanna, open woodland) however much rain the wet
    # season brings; a dry season that is frozen anyway (cold-winter taiga) does not count
    drought=np.max([_smooth(5.,15.,Tpx[s])*_smooth(.8,.15,Ppx[s]*365.24/Pth) for s in range(4)],0)
    trees=_smooth(.9,2.,moist)*_smooth(8.,13.,Twarm)*(1-.7*drought)
    evergreen=np.maximum(_smooth(-1.,-6.,Tcold),_smooth(16.,19.,Tcold)*_smooth(1.6,2.6,moist)*(1-drought))
    conifer=_smooth(-1.,-6.,Tcold)
    # bare ground: sand / red rock / grey rock by a fixed noise; rock and tundra where cold or high
    n1=nz.fbm(p,3.,4);n2=nz.fbm(p+5.1,6.,3)
    soil=_mix(SAND,RED_ROCK,_smooth(.42,.62,n1));soil=_mix(soil,GREY_ROCK,_smooth(.55,.75,n2)*.6)
    soil=_mix(soil,TUNDRA,_smooth(14.,4.,Twarm)*_smooth(.3,.8,moist))
    soil=_mix(soil,GREY_ROCK,_smooth(2500.,4500.,h)*.7)
    ice=_smooth(.35,.65,np.asarray(glacier,float)+.35*(nz.fbm(p+2.7,10.,3)-.5))*land
    depth=np.maximum(-h,0.);sea=_mix(DEEP_SEA,SHELF,np.exp(-depth/140.)*.9)
    sea=_mix(sea,(.16,.48,.55),np.exp(-depth/60.)*_smooth(18.,28.,Tann)*.5)
    out=np.empty((4,len(h),3))
    for s in range(4):
        rain_s=Ppx[s]*365.24/Pth
        active=_smooth(-1.,9.,Tpx[s])*_smooth(.25,1.,rain_s)
        grass=_mix(DRY_GRASS,GREEN_GRASS,active)
        canopy=_mix(_mix(BARE_TREES,LEAF,_smooth(.15,.7,active)),_mix(RAIN_FOREST,CONIFER,conifer),evergreen)
        col=_mix(soil,grass,herb);col=_mix(col,canopy,trees)
        snow=_smooth(.5,-5.,Tpx[s])*_smooth(.02,.25,np.maximum(Ppx[s],Ppx.mean(0)))
        col=_mix(col,SNOW,snow*(1-.55*trees*(1-_smooth(-8.,-25.,Tpx[s]))))
        col=_mix(col,ICE_SHEET,ice)
        if lakes is not None:
            frozen=_smooth(-1.,-6.,Tpx[s])
            col=np.where((lakes==1)[:,None],_mix(LAKE,SNOW,frozen),col);col=np.where((lakes==2)[:,None],_mix(SALT,SNOW,frozen*.5),col)
            col=np.where((lakes==3)[:,None],_mix(_mix(col,MARSH,.55),SNOW,frozen*.8),col)   # wetland: dark, wet vegetation
        sc=_mix(sea,SEA_ICE,_smooth(.1,.8,sea_ice[s]))
        out[s]=np.where(land[:,None],col,sc)
    return out


def clouds(frame,Ppx,*,cool_water=None,seed=11):
    """Cloud alpha (4, N): cover from rain (and low cloud over cool water), shapes from warped noise."""
    p=_points(frame)*np.array([1.,1.,1.9]);nz=Noise3(seed,48);out=np.empty((4,len(p)))
    for s in range(4):
        cover=.16+.62*(1-np.exp(-np.maximum(Ppx[s],0)/3.2))
        if cool_water is not None:cover=np.maximum(cover,.25+.45*_smooth(.5,3.,cool_water))
        cover=np.clip(cover,.05,.85)
        w=np.stack([nz.fbm(p+(s*3.7+k*1.9),2.2,3) for k in range(3)],-1)-.5
        q=p+.55*w;n=nz.fbm(q+s*7.1,4.5,6,.55);u=_uniform(n)
        a=_smooth(1-cover-.04,1-cover+.22,u)**.8
        fine=nz.fbm(q*1.0+3.3,14.,3)
        out[s]=np.clip(a*(.78+.22*_smooth(.3,.7,fine))+.12*_smooth(.62,.8,fine)*_smooth(1-cover-.2,1-cover,u),0,1)*.97
    return out
