"""Equirectangular maps of cell fields (nearest cell per pixel), PNG via matplotlib."""
import numpy as np
from scipy.spatial import cKDTree


def raster(grid,values,width=720,height=360):
    lat=np.radians(90-(np.arange(height)+.5)*180/height);lon=np.radians(-180+(np.arange(width)+.5)*360/width)
    LAT,LON=np.meshgrid(lat,lon,indexing='ij')
    p=np.stack([np.cos(LAT)*np.cos(LON),np.cos(LAT)*np.sin(LON),np.sin(LAT)],axis=-1).reshape(-1,3)
    idx=cKDTree(grid.points).query(p)[1]
    return np.asarray(values)[idx].reshape(height,width)


def save_maps(path,grid,panels,*,land_fraction=None,ncols=2,cmap='RdYlBu_r',vmin=None,vmax=None,unit=''):
    """panels: list of (title, values). Coastline = land fraction 0.5 contour."""
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fn=korean_font()
    if fn:plt.rcParams['font.family']=fn;plt.rcParams['axes.unicode_minus']=False
    nrows=int(np.ceil(len(panels)/ncols))
    fig,axes=plt.subplots(nrows,ncols,figsize=(6.2*ncols,3.3*nrows),squeeze=False)
    lo=vmin if vmin is not None else min(float(np.min(v)) for _,v in panels)
    hi=vmax if vmax is not None else max(float(np.max(v)) for _,v in panels)
    coast=raster(grid,land_fraction) if land_fraction is not None else None
    for ax,(title,v) in zip(axes.ravel(),panels):
        im=ax.imshow(raster(grid,v),extent=(-180,180,-90,90),cmap=cmap,vmin=lo,vmax=hi,interpolation='nearest')
        if coast is not None and coast.max()>0:ax.contour(np.linspace(-180,180,coast.shape[1]),np.linspace(90,-90,coast.shape[0]),coast,[.5],colors='k',linewidths=.6)
        ax.set_title(title,fontsize=10);ax.set_xticks([-180,-90,0,90,180]);ax.set_yticks([-60,-30,0,30,60])
    for ax in axes.ravel()[len(panels):]:ax.axis('off')
    fig.colorbar(im,ax=axes.ravel().tolist(),shrink=.8,label=unit)
    fig.savefig(path,dpi=110,bbox_inches='tight');plt.close(fig)


def save_flow_maps(path,grid,panels,*,land_fraction=None,ncols=2,step_deg=8.,mask=None):
    """panels: list of dict(title, field, cmap, vmin, vmax, unit, u, v, contour, levels, arrow_scale, mask).

    field: colour shading; (u, v): arrows sampled every step_deg; contour: line contours of another field.
    """
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fn=korean_font()
    if fn:plt.rcParams['font.family']=fn;plt.rcParams['axes.unicode_minus']=False
    nrows=int(np.ceil(len(panels)/ncols))
    fig,axes=plt.subplots(nrows,ncols,figsize=(6.6*ncols,3.5*nrows),squeeze=False)
    coast=raster(grid,land_fraction) if land_fraction is not None else None
    X=np.linspace(-180,180,720);Y=np.linspace(90,-90,360)
    lat=np.arange(-90+step_deg/2,90,step_deg);lon=np.arange(-180+step_deg/2,180,step_deg)
    LA,LO=np.meshgrid(np.radians(lat),np.radians(lon),indexing='ij')
    pts=np.stack([np.cos(LA)*np.cos(LO),np.cos(LA)*np.sin(LO),np.sin(LA)],-1).reshape(-1,3)
    near=cKDTree(grid.points).query(pts)[1]
    for ax,pn in zip(axes.ravel(),panels):
        if pn.get('field') is not None:
            im=ax.imshow(raster(grid,pn['field']),extent=(-180,180,-90,90),cmap=pn.get('cmap','RdYlBu_r'),
                         vmin=pn.get('vmin'),vmax=pn.get('vmax'),interpolation='nearest')
            fig.colorbar(im,ax=ax,shrink=.75,label=pn.get('unit',''))
        if pn.get('contour') is not None:
            cs=ax.contour(X,Y,raster(grid,pn['contour']),levels=pn.get('levels',12),colors='k',linewidths=.5)
            ax.clabel(cs,fontsize=6,fmt='%g')
        if coast is not None and coast.max()>0:ax.contour(X,Y,coast,[.5],colors='0.25',linewidths=.8)
        if pn.get('u') is not None:
            u=np.asarray(pn['u'])[near];v=np.asarray(pn['v'])[near]
            m=np.ones_like(u,bool) if pn.get('mask') is None else np.asarray(pn['mask'])[near]
            ax.quiver(np.degrees(LO).ravel()[m],np.degrees(LA).ravel()[m],u[m],v[m],angles='uv',
                      scale=pn.get('arrow_scale'),width=.0022,color=pn.get('arrow_color','k'))
        ax.set_xlim(-180,180);ax.set_ylim(-90,90)
        ax.set_title(pn['title'],fontsize=10);ax.set_xticks([-180,-90,0,90,180]);ax.set_yticks([-60,-30,0,30,60])
    for ax in axes.ravel()[len(panels):]:ax.axis('off')
    fig.savefig(path,dpi=110,bbox_inches='tight');plt.close(fig)


def save_hydro_map(path,raster,h,hydro,*,title='',glacier=None,sea_ice=None,river_min_m3_s=1000.):
    """Terrain shading + rivers (log discharge) + fresh / salt lakes, wetlands + glaciers + seasonal sea ice (raster fields)."""
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.colors import ListedColormap
    fn=korean_font()
    if fn:plt.rcParams['font.family']=fn;plt.rcParams['axes.unicode_minus']=False
    fig,ax=plt.subplots(figsize=(12,6.2));ext=(-180,180,-90,90)
    land=~hydro['sea'];shade=np.where(land,np.clip(h,0,5000),np.nan)
    ax.imshow(np.where(land,np.nan,1.),extent=ext,cmap=ListedColormap(['#9fc5e8']),interpolation='nearest')
    if sea_ice is not None:ax.imshow(np.where(~land&(sea_ice>.15),sea_ice,np.nan),extent=ext,cmap='Blues_r',vmin=-.5,vmax=1.2,interpolation='nearest')
    ax.imshow(shade,extent=ext,cmap='gist_earth',vmin=-1500,vmax=5000,interpolation='nearest')
    if glacier is not None:ax.imshow(np.where(glacier&land,1.,np.nan),extent=ext,cmap=ListedColormap(['white']),interpolation='nearest')
    ax.imshow(np.where(hydro['lakes']==3,1.,np.nan),extent=ext,cmap=ListedColormap(['#5b8a72']),interpolation='nearest')
    Q=hydro['discharge'];riv=land&(Q>river_min_m3_s)&np.isin(hydro['lakes'],(0,3))
    im=ax.imshow(np.where(riv,np.log10(np.maximum(Q,1)),np.nan),extent=ext,cmap='winter_r',vmin=np.log10(river_min_m3_s),vmax=5.3,interpolation='nearest')
    ax.imshow(np.where(hydro['lakes']==1,1.,np.nan),extent=ext,cmap=ListedColormap(['#1f4e9e']),interpolation='nearest')
    ax.imshow(np.where(hydro['lakes']==2,1.,np.nan),extent=ext,cmap=ListedColormap(['#e69138']),interpolation='nearest')
    if hydro['endorheic'].any():ax.contour(np.linspace(-180,180,h.shape[1]),np.linspace(90,-90,h.shape[0]),hydro['endorheic'].astype(float),[.5],colors='#b45f06',linewidths=.6)
    fig.colorbar(im,ax=ax,shrink=.7,label='river discharge, log10 m3/s')
    ax.set_title(title+'  (blue lakes: fresh, orange: closed/salt, green: wetland, brown line: closed-basin catchment, white: glacier)',fontsize=9)
    ax.set_xticks([-180,-90,0,90,180]);ax.set_yticks([-60,-30,0,30,60])
    fig.savefig(path,dpi=110,bbox_inches='tight');plt.close(fig)


KOPPEN_COLORS={'Af':'#0000ff','Am':'#0078ff','Aw':'#46aafa','As':'#46aafa','BWh':'#ff0000','BWk':'#ff9696','BSh':'#f5a500','BSk':'#ffdc64',
    'Csa':'#ffff00','Csb':'#c8c800','Csc':'#969600','Cwa':'#96ff96','Cwb':'#64c864','Cwc':'#329632','Cfa':'#c8ff50','Cfb':'#64ff50','Cfc':'#32c800',
    'Dsa':'#ff00ff','Dsb':'#c800c8','Dsc':'#963296','Dsd':'#966496','Dwa':'#aaafff','Dwb':'#5a78dc','Dwc':'#4b50b4','Dwd':'#320087',
    'Dfa':'#00ffff','Dfb':'#37c8ff','Dfc':'#007d7d','Dfd':'#00465f','ET':'#b2b2b2','EF':'#686868'}
BIOME_COLORS={'빙원':'#f0f0f0','툰드라':'#b8c4b0','타이가(냉대림)':'#2f6b4f','온대 혼합림':'#4f8f4f','온대 낙엽수림':'#7fbf5f','온대 우림':'#1f7f5f',
    '난온대 상록림':'#3f9f3f','지중해성 관목림':'#bfbf5f','온대 초원(스텝)':'#dfcf7f','건조 관목·사바나':'#d9a54a','사막':'#f2dc9b','열대 사바나':'#c9c94a',
    '열대 계절림':'#5faf2f','열대 우림':'#0f6f1f'}


def korean_font():
    """A font that has Hangul, if one is installed (Windows: Malgun Gothic; Linux: Noto/Nanum)."""
    import os
    from matplotlib import font_manager
    for path in ('C:/Windows/Fonts/malgun.ttf','/mnt/c/Windows/Fonts/malgun.ttf'):   # Windows font, also seen from WSL
        if os.path.exists(path):
            try:font_manager.fontManager.addfont(path)
            except Exception:pass
    for name in ('Malgun Gothic','NanumGothic','Noto Sans CJK KR','Noto Sans KR','AppleGothic'):
        if any(name==f.name for f in font_manager.fontManager.ttflist):return name
    return None


def save_categorical(path,grid,labels,colors,*,land_fraction=None,title='',names=None,ocean_label=None):
    """Category map (one label per cell) with a legend of the categories present."""
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.colors import ListedColormap
    from matplotlib.patches import Patch
    fn=korean_font()
    if fn:plt.rcParams['font.family']=fn;plt.rcParams['axes.unicode_minus']=False
    cats=[c for c in colors if c in set(labels)];code={c:k for k,c in enumerate(cats)}
    vals=np.array([code.get(l,np.nan) for l in labels],float)
    if land_fraction is not None:vals=np.where(np.asarray(land_fraction)>.5,vals,np.nan)
    fig,ax=plt.subplots(figsize=(12,6.2));ax.set_facecolor('#c6dbef')
    ax.imshow(raster(grid,vals),extent=(-180,180,-90,90),cmap=ListedColormap([colors[c] for c in cats]),vmin=-.5,vmax=len(cats)-.5,interpolation='nearest')
    if land_fraction is not None:
        coast=raster(grid,land_fraction);ax.contour(np.linspace(-180,180,coast.shape[1]),np.linspace(90,-90,coast.shape[0]),coast,[.5],colors='k',linewidths=.5)
    lab=lambda c:f'{c} {names[c]}' if names and c in names and names[c]!=c else c
    ax.legend(handles=[Patch(color=colors[c],label=lab(c)) for c in cats],loc='center left',bbox_to_anchor=(1.01,.5),fontsize=7,frameon=False)
    ax.set_title(title,fontsize=10);ax.set_xticks([-180,-90,0,90,180]);ax.set_yticks([-60,-30,0,30,60])
    fig.savefig(path,dpi=110,bbox_inches='tight');plt.close(fig)
