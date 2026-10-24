"""W7: map layers for the editor app — RGBA images in the app's own equirectangular frame.

Every layer is width x height pixels, north row first, longitude -180..180 (the same frame as the
editor's display raster, World.DisplayCoordinate), transparent where the layer has nothing to show,
so the app can lay it over its own terrain shading.  A small legend image goes with each layer.

Fine detail comes from the terrain itself, not from a finer climate:
  * temperature on land follows the pixel's own height (lapse 6.5 K/km from the cell's mean height)
  * rain inside a cell is redistributed by the pixel terrain and the season's wind (windward slopes
    wetter, rain shadows drier, the cell keeping its rain: downscale.py)
  * Koppen type and biome are classified per pixel from that temperature and rain, so mountain
    ranges show their colder, wetter climates and the dry side of a range its rain shadow
Climate fields are interpolated between the three nearest climate cells (inverse distance).
"""
import json
import numpy as np
from scipy.spatial import cKDTree
from . import classify,naturalcolor,downscale
from .render import KOPPEN_COLORS,BIOME_COLORS,korean_font

LAPSE=6.5e-3;DAY=86400.


class Frame:
    def __init__(self,grid,width,height,height_m):
        """height_m: terrain height above sea level on this frame (height, width), negative = sea."""
        self.g=grid;self.W=width;self.H=height
        lat=np.radians(90-(np.arange(height)+.5)*180/height);lon=np.radians(-180+(np.arange(width)+.5)*360/width)
        LA,LO=np.meshgrid(lat,lon,indexing='ij');p=np.stack([np.cos(LA)*np.cos(LO),np.cos(LA)*np.sin(LO),np.sin(LA)],-1).reshape(-1,3)
        d,idx=cKDTree(grid.points).query(p,k=3);w=1/np.maximum(d,1e-9)**2;self.w=w/w.sum(1,keepdims=True);self.idx=idx
        self.near=idx[:,0];self.h=np.asarray(height_m,float).reshape(-1);self.land=self.h>0
        self.lat=np.degrees(LA).reshape(-1)

    def smooth(self,v):return (np.asarray(v)[...,self.idx]*self.w).sum(-1)
    def nearest(self,v):return np.asarray(v)[...,self.near]


def _rgba(values,cmap,vmin,vmax,alpha=1.,mask=None):
    import matplotlib
    c=matplotlib.colormaps[cmap]((np.clip(values,vmin,vmax)-vmin)/(vmax-vmin));c[...,3]=alpha
    if mask is not None:c[~mask,3]=0
    return c


def _save(path,rgba,frame):
    import matplotlib.image as mpimg
    mpimg.imsave(path,np.clip(rgba.reshape(frame.H,frame.W,4),0,1))


def _hex(c):return np.array([int(c[k:k+2],16)/255 for k in (1,3,5)]+[1.])


def _categorical(labels,colors,mask,alpha=.9):
    out=np.zeros((len(labels),4));lut={k:_hex(v) for k,v in colors.items()}
    for k,col in lut.items():
        m=labels==k;out[m]=col
    out[:,3]=np.where(mask&(out[:,3]>0),alpha,0.);return out


def _speed_ref(*fields):
    """The strength drawn as the largest head: the 95th percentile of the speeds (shared by the maps it is given)."""
    s=np.concatenate([np.hypot(np.asarray(u),np.asarray(v)).ravel() for u,v in fields]);s=s[s>0]
    return float(np.percentile(s,95)) if len(s) else 1.


def _arrows(path,frame,base,u,v,step_deg=10.,ref=None,color='k',mask=None,sea_only=False):
    """Draw arrows over an RGBA base image at exact pixel geometry (full-bleed axes).
    Every arrow has the same length, centred on its grid point and shorter than the spacing, so neighbours never cross;
    the size of the head tells the rough strength (largest at `ref` and above, with a smallest head that still shows
    the direction).  sea_only: arrows only where the map itself is sea, cut off at the coast (ocean currents)."""
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.collections import LineCollection,PolyCollection
    g=frame.g;W,H=frame.W,frame.H
    fig=plt.figure(figsize=(W/100,H/100),dpi=100);fig.patch.set_alpha(0);ax=fig.add_axes([0,0,1,1]);ax.axis('off')
    ax.imshow(base.reshape(H,W,4),extent=(-180,180,-90,90),interpolation='nearest',aspect='auto')
    lat=np.arange(-90+step_deg/2,90,step_deg);LA,LO=np.meshgrid(lat,np.arange(-180+step_deg/2,180,step_deg),indexing='ij')
    p=np.stack([np.cos(np.radians(LA))*np.cos(np.radians(LO)),np.cos(np.radians(LA))*np.sin(np.radians(LO)),np.sin(np.radians(LA))],-1).reshape(-1,3)
    k=cKDTree(g.points).query(p)[1];m=np.ones(len(k),bool) if mask is None else np.asarray(mask)[k].astype(bool)
    lo=LO.ravel();la=LA.ravel()
    if sea_only:
        px=np.clip(((lo+180)/360*W).astype(int),0,W-1);py=np.clip(((90-la)/180*H).astype(int),0,H-1)
        m&=~frame.land[py*W+px]
    U=np.asarray(u,float)[k];V=np.asarray(v,float)[k];s=np.hypot(U,V);m&=s>0
    lo,la,U,V,s=lo[m],la[m],U[m],V[m],s[m]
    if ref is None:ref=_speed_ref((U,V))
    d=np.stack([U,V],-1)/s[:,None]                                  # direction on the map (degrees are square)
    c=np.stack([lo,la],-1);half=.31*step_deg
    head=step_deg*(.12+.22*np.clip(s/max(ref,1e-12),0,1))           # head length: 12% (weak) .. 34% (strong) of the spacing
    tip=c+d*half;tail=c-d*half;neck=tip-d*head[:,None];side=np.stack([-d[:,1],d[:,0]],-1)*(.55*head)[:,None]
    lw=W/1000*72/100                                                # about W/1000 pixels wide
    ax.add_collection(LineCollection(np.stack([tail,neck],1),colors=color,linewidths=lw,capstyle='butt'))
    ax.add_collection(PolyCollection(np.stack([tip,neck+side,neck-side],1),facecolors=color,edgecolors=color,linewidths=lw*.3))
    ax.set_xlim(-180,180);ax.set_ylim(-90,90)
    if not sea_only:
        fig.savefig(path,dpi=100,transparent=True);plt.close(fig);return
    fig.canvas.draw();img=np.asarray(fig.canvas.buffer_rgba(),float)[:H,:W]/255;plt.close(fig)
    land=frame.land.reshape(H,W);img[land]=base.reshape(H,W,4)[land]           # nothing of an arrow on land
    import matplotlib.image as mpimg
    mpimg.imsave(path,np.clip(img,0,1))


def _legend_bar(path,title,cmap,vmin,vmax,unit):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fn=korean_font()
    if fn:plt.rcParams['font.family']=fn;plt.rcParams['axes.unicode_minus']=False
    fig=plt.figure(figsize=(4.6,.78),dpi=130);ax=fig.add_axes([.03,.42,.94,.24])
    import matplotlib.colorbar as cb
    from matplotlib.colors import Normalize
    cb.ColorbarBase(ax,cmap=matplotlib.colormaps[cmap],norm=Normalize(vmin,vmax),orientation='horizontal')
    ax.tick_params(labelsize=8,colors='#c9d6df');ax.set_title(f'{title} ({unit})',fontsize=9,color='#c9d6df',pad=3)
    for s in ax.spines.values():s.set_edgecolor('#55697a')
    fig.savefig(path,transparent=True);plt.close(fig)


def _legend_categories(path,title,items):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.patches import Patch
    fn=korean_font()
    if fn:plt.rcParams['font.family']=fn
    ncol=min(5,max(2,int(np.ceil(len(items)/4))));rows=int(np.ceil(max(len(items),1)/ncol))
    fig=plt.figure(figsize=(2.15*ncol,.34+.21*rows),dpi=130);ax=fig.add_axes([0,0,1,1]);ax.axis('off')
    ax.legend(handles=[Patch(color=c,label=l) for l,c in items],loc='center',ncol=ncol,fontsize=7.5,frameon=False,labelcolor='#c9d6df',
              title=title,title_fontsize=8.5,handlelength=1.2,columnspacing=1.,labelspacing=.25,borderaxespad=0.)
    leg=ax.get_legend();leg.get_title().set_color('#c9d6df');fig.savefig(path,transparent=True);plt.close(fig)


def _ef_px(code):return (code=='EF').astype(float)*.6   # pixel ice climate without a cell ice sheet: snow-white, not ice-blue


def build(out,frame,climate,result,*,surface_T,water,snow,sea_ice,hydro=None,hydro_raster=None,log=None):
    """Write layers/*.png and legends/*.png into `out`; returns the layer list for layers.json."""
    out.mkdir(parents=True,exist_ok=True);(out/'layers').mkdir(exist_ok=True);(out/'legends').mkdir(exist_ok=True)
    g=frame.g;cl=climate;r=result;layers=[];lakes=None
    zc=np.asarray(cl.z);Tc=np.asarray(surface_T)-273.15;P=water['P']*DAY
    # temperature with the pixel's own height on land
    zc_px=frame.smooth(zc);dz=np.where(frame.land,np.maximum(frame.h,0)-zc_px,0.)
    Tpx=frame.smooth(Tc)-LAPSE*dz[None,:];Ppx=frame.smooth(P)
    # rain inside each cell follows windward slopes and rain shadows (W13, downscale.py)
    c=r['circulation'];Ppx=downscale.rain(frame,Ppx,[x['u'] for x in c],[x['v'] for x in c])
    def add(lid,title,img,legend,kind='field'):
        _save(out/'layers'/f'{lid}.png',img,frame) if img is not None else None
        layers.append(dict(id=lid,title=title,file=f'layers/{lid}.png',legend=f'legends/{lid}.png',kind=kind))
        if log:log(lid)
    for lid,title,v in (('temperature_annual','연평균 기온',Tpx.mean(0)),('temperature_summer','6-8월 기온',Tpx[1]),('temperature_winter','12-2월 기온',Tpx[3])):
        add(lid,title,_rgba(v,'RdYlBu_r',-40,35,.92),None);_legend_bar(out/'legends'/f'{lid}.png',title,'RdYlBu_r',-40,35,'°C')
    for lid,title,v in (('precipitation_annual','연 강수',Ppx.mean(0)),('precipitation_summer','6-8월 강수',Ppx[1]),('precipitation_winter','12-2월 강수',Ppx[3])):
        add(lid,title,_rgba(v,'YlGnBu',0,10,.9),None);_legend_bar(out/'legends'/f'{lid}.png',title,'YlGnBu',0,10,'mm/일')
    # Koppen and biome per land pixel
    L=np.flatnonzero(frame.land);code=np.full(frame.W*frame.H,'',dtype=object);bio=np.full(frame.W*frame.H,'',dtype=object)
    if len(L):
        gl=frame.nearest(snow['glacier'].astype(float))[L]>.5
        c,mon=classify.koppen(Tpx[:,L],Ppx[:,L],frame.lat[L],glacier=gl);code[L]=c;bio[L]=classify.biome(c,mon)
    add('koppen','쾨펜-가이거 기후형',_categorical(code,KOPPEN_COLORS,frame.land),None,'category')
    present=[k for k in KOPPEN_COLORS if (code==k).any()]
    items=[(f'{k} {classify.KOPPEN_NAMES[k]}',KOPPEN_COLORS[k]) for k in present]
    _legend_categories(out/'legends'/'koppen.png','쾨펜-가이거 기후형',items);layers[-1]['legend_items']=items
    add('biome','생물군계',_categorical(bio,BIOME_COLORS,frame.land),None,'category')
    items=[(b,BIOME_COLORS[b]) for b in BIOME_COLORS if (bio==b).any()]
    _legend_categories(out/'legends'/'biome.png','생물군계',items);layers[-1]['legend_items']=items
    # pressure and wind
    c=r['circulation'];wind_ref=_speed_ref(*[(c[s]['u'],c[s]['v']) for s in (1,3)])   # the same head sizes in both seasons
    for s,lid,title in ((1,'wind_summer','6-8월 해면 기압·바람'),(3,'wind_winter','12-2월 해면 기압·바람')):
        base=_rgba(frame.smooth(c[s]['slp']),'RdBu_r',-20,20,.75);path=out/'layers'/f'{lid}.png'
        _arrows(path,frame,base,c[s]['u'],c[s]['v'],ref=wind_ref);layers.append(dict(id=lid,title=title,file=f'layers/{lid}.png',legend=f'legends/{lid}.png',kind='field'))
        _legend_bar(out/'legends'/f'{lid}.png',title.replace('·바람','')+' 편차','RdBu_r',-20,20,'hPa')
    # ocean currents: SST minus latitude mean, arrows of the gyre transport
    S=np.maximum(np.asarray(r['T']),271.35).mean(0);an=S-cl.atm.zonal@S;ocean=~frame.land
    psi=np.array(r['psi_used']).mean(0);u,v=cl.ocn.transport_vectors(psi)
    base=_rgba(frame.smooth(an),'RdBu_r',-6,6,.85,mask=ocean)
    _arrows(out/'layers'/'currents.png',frame,base,u,v,step_deg=8.,mask=cl.ocn.ocean,sea_only=True)
    layers.append(dict(id='currents',title='해류 (난류·한류)',file='layers/currents.png',legend='legends/currents.png',kind='field'))
    _legend_bar(out/'legends'/'currents.png','같은 위도 대비 수온 (+ 난류, − 한류)','RdBu_r',-6,6,'K')
    # snow and sea ice
    snow_px=frame.nearest(snow['snow_seasons'])/4.;ice_px=frame.smooth(np.asarray(sea_ice).max(0))
    img=np.zeros((frame.W*frame.H,4));img[:,:3]=np.where(frame.land[:,None],[.96,.97,1.],[.82,.92,1.])
    img[:,3]=np.where(frame.land,.85*snow_px,np.where(ice_px>.15,.85*ice_px,0.))
    add('snow_ice','눈 덮이는 계절 · 해빙',img,None);_legend_bar(out/'legends'/'snow_ice.png','눈 덮인 계절 비율 / 해빙 농도','Blues_r',0,1,'0–1')
    # rivers, lakes, glaciers (hydrology raster resampled onto the frame by nearest pixel)
    if hydro is not None and hydro_raster is not None:
        rs=hydro_raster;ny,nx=hydro['discharge'].shape
        iy=np.clip(((90-frame.lat)/rs.step).astype(int),0,ny-1)
        lon=-180+(np.arange(frame.W)+.5)*360/frame.W;ix=np.tile(np.clip(((lon+180)/rs.step).astype(int),0,nx-1),frame.H)
        Q=hydro['discharge'][iy,ix];lk=hydro['lakes'][iy,ix];gl=frame.nearest(snow['glacier'].astype(float))>.5
        img=np.zeros((frame.W*frame.H,4))
        img[(lk==3)&frame.land]=[.36,.54,.45,.85]                            # wetland (rivers drawn over it)
        riv=(Q>1000)&frame.land&((lk==0)|(lk==3));t=np.clip((np.log10(np.maximum(Q,1))-3)/2.3,0,1)
        img[riv]=np.c_[.15+0*t[riv],.45+.2*t[riv],.95-.1*t[riv],.55+.45*t[riv]]
        img[(lk==1)&frame.land]=[.12,.31,.62,1.];img[(lk==2)&frame.land]=[.90,.57,.22,1.]
        img[gl&frame.land]=[.97,.98,1.,.95]
        add('hydro','하천·호수·빙하',img,None);lakes=np.where(frame.land,lk,0)
        items=[('강 (1000 m³/s 이상, 진할수록 큼)','#3c78e6'),('민물 호수','#1f4e9e'),('염호 (닫힌 분지)','#e69138'),('습지 (얕게 물이 고이는 땅)','#5b8a72'),('빙하·빙상','#f7faff')]
        _legend_categories(out/'legends'/'hydro.png','하천·호수·빙하',items);layers[-1]['legend_items']=items
    # the planet from space: natural colour per season, and a cloud deck the app can lay over it
    sat=naturalcolor.surface(frame,Tpx,Ppx,glacier=np.maximum(frame.smooth(snow['glacier'].astype(float)),_ef_px(code)),sea_ice=frame.smooth(np.asarray(sea_ice,float)),lakes=lakes)
    cool=np.where(frame.land,0.,-frame.smooth(an))
    cloud=naturalcolor.clouds(frame,Ppx,cool_water=cool)
    for s,(suffix,title) in enumerate((('spring','3-5월'),('summer','6-8월'),('autumn','9-11월'),('winter','12-2월'))):
        img=np.ones((frame.W*frame.H,4));img[:,:3]=sat[s]
        add(f'satellite_{suffix}',f'우주에서 본 모습 · {title}',img,None,'category');layers[-1]['legend_items']=naturalcolor.LEGEND
        img=np.ones((frame.W*frame.H,4));img[:,:3]=.97;img[:,3]=cloud[s]
        add(f'clouds_{suffix}',f'구름 · {title}',img,None,'overlay')
    _point_raster(out,frame,Tpx,Ppx,code,bio)
    return layers


def _point_raster(out,frame,Tpx,Ppx,code,bio):
    """point.bin + point.json: the pixel values the layers were drawn from, so the app's point card shows
    what the map shows (not the ~480 km cell mean).  Little-endian, north row first, per pixel:
    float16 T_c[season][pixel], float16 P_mm_day[season][pixel], int16 height_m[pixel],
    uint8 koppen[pixel], uint8 biome[pixel] (indices into the lists in point.json, 255 = sea / none)."""
    kop=list(classify.KOPPEN_ORDER);bl=list(classify.BIOMES)
    def index(labels,names):
        lut={k:i for i,k in enumerate(names)};return np.array([lut.get(x,255) for x in labels],np.uint8)
    T=np.asarray(Tpx,'<f2');P=np.asarray(Ppx,'<f2');h=np.clip(np.round(frame.h),-32768,32767).astype('<i2')
    with open(out/'point.bin','wb') as fh:
        for a in (T,P,h,index(code,kop),index(bio,bl)):fh.write(np.ascontiguousarray(a).tobytes())
    (out/'point.json').write_text(json.dumps(dict(width=frame.W,height=frame.H,seasons=int(T.shape[0]),file='point.bin',
        layout=['T_c f2 [season][pixel]','P_mm_day f2 [season][pixel]','height_m i2','koppen u1','biome u1'],
        koppen=kop,koppen_names=[classify.KOPPEN_NAMES.get(k,k) for k in kop],biomes=bl),ensure_ascii=False),encoding='utf-8')
