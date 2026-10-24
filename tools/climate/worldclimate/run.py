"""W6/W7: one world in, climate atlas out (command line and the editor app).

    python -m worldclimate.run WORLD [--out DIR] [--name NAME] [--deepwater LAT,LON ...]
                                     [--app WIDTHxHEIGHT] [--status FILE] [--cancel FILE] [--no-states]

WORLD is an editor file (.garden v2 or garden-vector-terrain-v1 JSON), the name of a test world
(worldclimate.testworlds.WORLDS) or `earth` (reference only).  Writes to DIR:
  result.json             small summary for the app: terrain_sha256, states, layer list, suggestions
  state-K/climate.json    per-cell table (lat, lon, land, height, seasonal T and P, Koppen, biome, snow,
                          sea ice, explanation) of climate state K (0 = warmest)
  state-K/layers/*.png    with --app: map layers in the app's equirectangular frame (+ legends/*.png)
  state-0/*.png           atlas sheets (temperature, precipitation, koppen, biome, winds, currents, hydro)
--deepwater names the sea(s) where surface water sinks (thermohaline overturning).  Without it the model finds
them itself: where the coldest-season surface water is densest (ocean.py); result.json lists where it sank.
--status gets {"phase","progress","message"} updates; if the --cancel file appears the run stops (exit 3).
"""
import argparse,json,os,sys,time,zipfile
from datetime import datetime,timezone
from pathlib import Path
import numpy as np
from .grid import Grid
from .coupled import Climate,solutions
from .ocean import OceanParams
from . import terrain,testworlds,cryosphere,rivers,classify,applayers
from .explain import Explainer
from .render import save_maps,save_flow_maps,save_categorical,save_hydro_map,KOPPEN_COLORS,BIOME_COLORS

DAY=86400.
SEASONS_NH=['봄(북반구 3-5월)','여름(6-8월)','가을(9-11월)','겨울(12-2월)']
MODEL='worldclimate 0.1 (창작용 단순 계절 기후 모형, 근사)'


class Cancelled(Exception):pass


class Status:
    def __init__(self,path=None,cancel=None):
        self.path=Path(path) if path else None;self.cancel=Path(cancel) if cancel else None;self.t0=time.time()
    def __call__(self,phase,progress,message=''):
        self.check()
        if self.path is None:return
        tmp=self.path.with_suffix('.tmp')
        tmp.write_text(json.dumps(dict(phase=phase,progress=round(float(progress),3),message=message,seconds=round(time.time()-self.t0,1)),ensure_ascii=False),encoding='utf-8')
        for _ in range(50):                                             # Windows: replace fails while the app reads the old file
            try:os.replace(tmp,self.path);break
            except PermissionError:time.sleep(.02)
    def check(self,*_):
        if self.cancel is not None and self.cancel.exists():raise Cancelled()


def terrain_hash(world):
    """The editor's own TerrainSha256 (manifest of a .garden file), so the app can match results."""
    try:
        # the app's climate input names its own terrain (cube height map) as SourceTerrainSha256;
        # TerrainSha256 is the checksum of the triangle surface sampled from it for this model
        with zipfile.ZipFile(world) as z:
            m=json.loads(z.read('world.json'));return m.get('SourceTerrainSha256') or m.get('TerrainSha256')
    except Exception:return None


def load_world(world,grid):
    """-> (land fraction, height, fine-raster source, name)"""
    if world=='earth':                                                  # reference only (RTopo-2.0.1 on disk)
        from netCDF4 import Dataset
        f,z=terrain.earth_rtopo(grid)
        with Dataset('artifacts/engines/climber-x-input-files/RTopo-2.0.1_0.125deg_DRThydrocorr.nc') as d:
            names=list(d.variables);key='surface_elevation' if 'surface_elevation' in names else [k for k in names if 'elev' in k or 'topo' in k][0]
            # full 0.125 deg: the routing raster takes the lowest sample per pixel, and the hydro-corrected
            # channels (Congo gorge) are only open at the native resolution
            la=np.asarray(d['lat'][:],float);lo=np.asarray(d['lon'][:],float);zz=np.asarray(d[key][:],float)
            if key=='bedrock_topography' and 'ice_thickness' in names:zz=zz+np.asarray(d['ice_thickness'][:],float)   # ice-sheet surface, not the bed below
        LA,LO=np.meshgrid(la,lo,indexing='ij');return f,z,(LA.ravel(),LO.ravel(),zz.ravel()),'earth'
    if world in testworlds.WORLDS:
        c=testworlds.WORLDS[world]();f,z=c.on(grid);return f,z,c,world
    vt=terrain.load_vector(world);f,z,_=terrain.from_vector(grid,vt);return f,z,vt,Path(world).stem


def products(out,g,f,z,source,cl,r,*,name,raster_step=.5,app=None,sheets=True,status=None):
    """Classification, explanations, hydrology, maps and layers of one solved state."""
    out.mkdir(parents=True,exist_ok=True);land=f>.5;ocean=~land;lat=np.degrees(g.lat);lon=np.degrees(g.lon)
    T=r['T'];Ts=cl.surface_temperature(T);Tc=Ts-273.15;wt=r['water'];P=wt['P']*DAY
    code,mon=classify.koppen(Tc,P,lat);sn=cryosphere.snow(T,wt['P'],land);si=cryosphere.sea_ice(T,ocean)
    code=np.where(sn['glacier'],'EF',code);bio=classify.biome(code,mon)
    ex=Explainer(cl,r,Ts);cells=[]
    for i in range(g.n):
        row=dict(lat=round(float(lat[i]),2),lon=round(float(lon[i]),2),land=round(float(f[i]),3),height_m=round(float(z[i])),
                 T_c=[round(float(x),1) for x in Tc[:,i]],P_mm_day=[round(float(x),2) for x in P[:,i]],sea_ice=round(float(si[:,i].max()),2))
        if land[i]:
            row.update(koppen=str(code[i]),koppen_name=classify.KOPPEN_NAMES.get(code[i],code[i]),biome=str(bio[i]),
                       snow_seasons=int(sn['snow_seasons'][i]),glacier=bool(sn['glacier'][i]),soil_wetness_min=round(float(wt['m'][:,i].min()),2),
                       explanation=ex.explain(i,classify.KOPPEN_NAMES.get(code[i],code[i]))['text'])
        cells.append(row)
    if status:status('hydrology',.75,'하천과 호수를 계산하는 중')
    rs=rivers.Raster(360/app[0]) if app else rivers.Raster(raster_step);hr=rivers.terrain_raster(source,rs)
    mo=cl.water;k=1-mo.p.surface_rh_floor;e_open=(mo.ce*k*(1-wt['W']/mo.Ws)).mean(0)
    gl_r=rs.from_grid(g,sn['glacier'].astype(float))>.5
    hm=rivers.terrain_raster(source,rs,stat='min')                         # routing: lowest ground per pixel
    hy=rivers.route(hm,rs,rs.from_grid(g,wt['runoff'].mean(0)),rs.from_grid(g,e_open-wt['P'].mean(0)),ice=gl_r)
    layers=[]
    if app:
        if status:status('layers',.85,'지도 층을 그리는 중')
        fr=applayers.Frame(g,app[0],app[1],hr)
        layers=applayers.build(out,fr,cl,r,surface_T=Ts,water=wt,snow=sn,sea_ice=si,hydro=hy,hydro_raster=rs)
    if sheets:
        save_maps(out/'temperature.png',g,[(f'{name}: {SEASONS_NH[s]} 지표 기온',Tc[s]) for s in range(4)]+[('연평균',Tc.mean(0))],land_fraction=f,vmin=-40,vmax=35,unit='C')
        save_maps(out/'precipitation.png',g,[('연 강수 (mm/일)',P.mean(0))]+[(SEASONS_NH[s],P[s]) for s in (1,3)],land_fraction=f,cmap='YlGnBu',vmin=0,vmax=10,unit='mm/day')
        save_categorical(out/'koppen.png',g,list(code),KOPPEN_COLORS,land_fraction=f,title=f'{name}: 쾨펜-가이거 기후형',names=classify.KOPPEN_NAMES)
        save_categorical(out/'biome.png',g,list(bio),BIOME_COLORS,land_fraction=f,title=f'{name}: 생물군계')
        c=r['circulation']
        save_flow_maps(out/'winds.png',g,[dict(title=f'{SEASONS_NH[s]} 해면 기압 편차와 지표 바람',field=c[s]['slp'],cmap='RdBu_r',vmin=-20,vmax=20,unit='hPa',
                       u=c[s]['u'],v=c[s]['v'],arrow_scale=180) for s in (1,3)],land_fraction=f,ncols=1)
        psi=np.array(r['psi_used']).mean(0);u,v=cl.ocn.transport_vectors(psi);S=np.maximum(T,271.35).mean(0);an=S-cl.atm.zonal@S
        save_flow_maps(out/'currents.png',g,[dict(title='해류(화살표)와 같은 위도 대비 수온 편차 (난류 +, 한류 -)',field=np.where(ocean,an,np.nan),cmap='RdBu_r',vmin=-6,vmax=6,unit='K',
                       u=u,v=v,mask=ocean,arrow_scale=500)],land_fraction=f,ncols=1)
        save_hydro_map(out/'hydro.png',rs,hr,hy,title=name,glacier=gl_r,sea_ice=rs.from_grid(g,si.max(0)))
    w=g.area/g.area.sum();la=g.area*land
    share=lambda cond:round(float(la[cond].sum()/max(la.sum(),1)),3)
    summary=dict(global_mean_c=round(float(T.mean(0)@w-273.15),2),global_precip_mm_day=round(float(P.mean(0)@w),2),
        koppen_land_share={k:share(code==k) for k in classify.KOPPEN_ORDER if share(code==k)>0},
        koppen_group_share={k:share(np.array([c_[0]==k for c_ in code])&land) for k in 'ABCDE'},
        biome_land_share={b:share(bio==b) for b in classify.BIOMES if share(bio==b)>0},
        closed_basins=sum(b['closed'] for b in hy['basins']))
    (out/'climate.json').write_text(json.dumps(dict(summary=summary,cells=cells),ensure_ascii=False,indent=0),encoding='utf-8')
    return summary,layers


def atlas(world,out,*,name=None,find_states=True,raster_step=.5,deepwater=(),app=None,status=None,log=print):
    status=status or Status();t0=time.perf_counter();out=Path(out);out.mkdir(parents=True,exist_ok=True);g=Grid(4)
    status('loading',.02,'지형을 기후 격자로 옮기는 중')
    f,z,source,auto=load_world(world,g);name=name or auto
    op=OceanParams(deepwater_sites=tuple(tuple(x) for x in deepwater));cl=Climate(g,f,z,ocean=op)
    starts=['warm','cool','cold'] if find_states else ['warm'];done=[0]
    def on_start(s):
        status('solving',.05+.45*done[0]/len(starts),f'기후 해를 찾는 중 ({done[0]+1}/{len(starts)}: {dict(warm="따뜻한",cool="중간",cold="차가운")[s]} 출발점)');done[0]+=1
    states=solutions(cl,log=status.check,on_start=on_start) if find_states else None
    if states is None:
        on_start('warm');r=cl.solve(np.broadcast_to(300-40*np.sin(g.lat)**2,(4,g.n)),log=status.check)
        states=[dict(start='warm',converged=r['converged'],result=r,global_mean_c=float(r['T'].mean(0)@(g.area/g.area.sum())-273.15),floquet=[],stable=None)]
    ok=[s for s in states if s.get('converged')]
    if not ok:raise RuntimeError('no climate state converged')
    out_states=[]
    for k,st in enumerate(ok):
        status('state',.5+.45*k/len(ok),f'기후안 {k+1}/{len(ok)}의 지도와 설명을 만드는 중')
        # solve the state again so the Climate object holds its own circulation and moisture
        ck=Climate(g,f,z,ocean=op);rk=ck.solve(st['result']['T'],log=status.check)
        summ,layers=products(out/f'state-{k}',g,f,z,source,ck,rk,name=name,raster_step=raster_step,app=app,sheets=(k==0),status=status)
        out_states.append(dict(folder=f'state-{k}',start=st['start'],stable=st['stable'],floquet=[round(x,3) for x in st['floquet']],
                               label=('따뜻한 기후안' if summ['global_mean_c']>-10 else '얼음 행성 기후안'),layers=layers,
                               deepwater=ck.ocn.sinking_sites() if ck.auto_deepwater else [dict(lat=a,lon=b,share=None) for a,b in deepwater],**summ))
    result=dict(model=MODEL,name=name,terrain_sha256=terrain_hash(world) if str(world).endswith('.garden') else None,
                created_utc=datetime.now(timezone.utc).isoformat(timespec='seconds'),seconds=round(time.perf_counter()-t0,1),
                frame=None if not app else dict(width=app[0],height=app[1]),land_fraction=round(float(f@(g.area/g.area.sum())),3),
                # deep water: found by the model (densest surface water) unless --deepwater names the seas; sites of the warmest state
                deepwater_auto=cl.auto_deepwater,deepwater_sites=[[d['lat'],d['lon']] for d in out_states[0]['deepwater']],
                deepwater_suggestions=[] if cl.auto_deepwater else cl.ocn.suggest_deepwater_sites(),
                states=out_states,not_converged=[s['start'] for s in states if not s.get('converged')])
    (out/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=1),encoding='utf-8')
    status('completed',1.,'완료')
    log(json.dumps({k:v for k,v in result.items() if k!='states'}|{'states':[{q:s[q] for q in ('label','global_mean_c','stable')} for s in out_states]},ensure_ascii=False))
    return result


def main():
    a=argparse.ArgumentParser(description=__doc__,formatter_class=argparse.RawDescriptionHelpFormatter)
    a.add_argument('world');a.add_argument('--out',default='worldclimate-out');a.add_argument('--name');a.add_argument('--no-states',action='store_true')
    a.add_argument('--deepwater',action='append',default=[],help='LAT,LON of a deep-water formation sea (repeatable)')
    a.add_argument('--app',help='WIDTHxHEIGHT: also write map layers in the app frame');a.add_argument('--status');a.add_argument('--cancel')
    x=a.parse_args();sites=[tuple(float(v) for v in d.split(',')) for d in x.deepwater]
    app=tuple(int(v) for v in x.app.lower().split('x')) if x.app else None;st=Status(x.status,x.cancel)
    try:atlas(x.world,x.out,name=x.name,find_states=not x.no_states,deepwater=sites,app=app,status=st)
    except Cancelled:
        if st.path:st.path.write_text(json.dumps(dict(phase='cancelled',progress=0,message='취소됨'),ensure_ascii=False),encoding='utf-8')
        sys.exit(3)
    except Exception as e:
        if st.path:st.path.write_text(json.dumps(dict(phase='failed',progress=0,message=f'{type(e).__name__}: {e}'),ensure_ascii=False),encoding='utf-8')
        raise


if __name__=='__main__':main()
