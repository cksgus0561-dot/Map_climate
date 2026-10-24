"""W6: why a place has its climate — reasons per land cell, from the solved fields (Korean text).

Diagnostics per cell (all from quantities the model actually solved):
  * latitude belt and whether the ITCZ (the cell's own thermal equator) reaches it in some season
  * sea-level pressure regime by season (subtropical high, winter continental high, heat low)
  * wind origin: points 300..1200 km upwind (against the season's wind) over sea or land,
    and the highest ground on that path above the cell (rain shadow)
  * distance to the sea; offshore water warmer / colder than its latitude (warm / cold current)
  * share of the rain brought by fronts of the storm track (moisture.Moisture.frontal)
  * height above sea level; seasonal temperature range (continentality)
"""
import numpy as np
from scipy.spatial import cKDTree
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import dijkstra


def _move(p,dirs,dist_m,R=6.371e6):
    """Points dist_m along unit tangent dirs from unit vectors p (great circle)."""
    th=dist_m/R;q=np.cos(th)*p+np.sin(th)*dirs;return q/np.linalg.norm(q,axis=-1)[:,None]


class Explainer:
    def __init__(self,climate,result,surface_T=None):
        self.cl=cl=climate;self.r=r=result;g=self.g=cl.g;self.f=f=np.asarray(cl.f);self.land=f>.5
        self.z=np.asarray(cl.z);self.lat=np.degrees(g.lat);self.tree=cKDTree(g.points);T=r['T']
        self.Ts=(cl.surface_temperature(T) if surface_T is None else surface_T)-273.15
        # distance to the sea along the grid [km]
        i,j=g.left,g.right;A=coo_matrix((np.r_[g.distance,g.distance]/1e3,(np.r_[i,j],np.r_[j,i])),shape=(g.n,g.n)).tocsr()
        sea=np.flatnonzero(~self.land)
        self.coast_km=dijkstra(A,indices=sea,min_only=True) if len(sea) else np.full(g.n,np.inf)
        S=np.maximum(T,271.35).mean(0);self.sst_anom=S-cl.atm.zonal@S
        w=r.get('water');self.frontal=cl.water.frontal(w['W']) if w is not None and getattr(cl.water,'front',None) is not None else np.zeros((4,g.n))

    def season_names(self,i):
        n=self.lat[i]>=0
        return {1:'여름',3:'겨울',0:'봄',2:'가을'} if n else {1:'겨울',3:'여름',0:'가을',2:'봄'}

    def upwind(self,i,s):
        c=self.r['circulation'][s];w=c['wind3'][i];sp=float(np.linalg.norm(w))
        if sp<.5:return None
        d=-w/sp;p=self.g.points[i][None,:];pts=np.concatenate([_move(p,d[None,:],k*3e5) for k in (1,2,3,4)])
        idx=self.tree.query(pts)[1];return dict(speed=sp,sea_share=float((~self.land[idx]).mean()),
                                               barrier=float(self.z[idx].max()-self.z[i]),cells=idx)

    def reasons(self,i):
        cl,r,lat=self.cl,self.r,self.lat[i];alat=abs(lat);sn=self.season_names(i);out=[]
        P=r['water']['P'][:,i]*86400;circ=r['circulation']
        # belt / ITCZ
        itcz=[abs(lat-c['itcz_cell'][i])<9 for c in circ]
        if any(itcz):
            when=[sn[s] for s in range(4) if itcz[s]]
            out.append(('itcz','적도 수렴대(ITCZ)가 '+('일 년 내내' if all(itcz) else '·'.join(when)+'에')+' 머물러 상승 기류와 비가 많다'))
        slp=[c['slp'][i] for c in circ]
        ws,ss=(3,1) if lat>=0 else (1,3)                                  # winter / summer season index
        if 12<=alat<=38 and (np.mean(slp)>2 or slp[ws]>4) and P.mean()<1.:
            out.append(('subtropical_high','아열대 고기압이 덮어 하강 기류로 구름과 비가 적다'))
        if self.land[i] and slp[ws]>6 and alat>35:out.append(('winter_high','겨울에 대륙 고기압이 생겨 차갑고 마른 바람이 분다'))
        if self.land[i] and slp[ss]<-4 and P[ss]>2*max(P[ws],.1) and (alat<40 or self.coast_km[i]<1500):
            out.append(('monsoon','여름에 대륙이 달궈져 열저기압이 생기고 바다에서 습한 계절풍이 불어 든다'))
        # wind origin and rain shadow
        for s in (ss,ws):
            u=self.upwind(i,s)
            if u is None:continue
            if u['barrier']>800 and P[s]<1.:
                out.append(('rain_shadow',f'{sn[s]} 바람이 넘어오는 쪽에 높이 {u["barrier"]:.0f} m 더 높은 산지가 있어 비그늘이 된다'));break
            if u['sea_share']>.7 and P[s]>1.5 and alat>30:
                out.append(('maritime',f'{sn[s]}에 바다에서 바람이 불어와 습하고 기온 변화가 완만하다'));break
            if u['sea_share']<.2 and self.coast_km[i]>800 and P[s]<1.:
                out.append(('continental',f'{sn[s]} 바람이 넓은 대륙을 건너와 건조하다'));break
        # storm-track fronts: a large share of the rain comes from fronts, mostly in one season
        Pf=self.frontal[:,i]*86400
        if alat>=28 and P.mean()>1.2 and Pf.sum()>.5*P.sum():
            s=int(np.argmax(Pf));out.append(('frontal',f'{sn[s]}을 중심으로 편서풍대의 저기압과 전선이 지나며 비를 뿌린다'))
        # sea distance, currents
        if self.land[i] and self.coast_km[i]>1500:out.append(('interior',f'바다에서 약 {self.coast_km[i]:.0f} km 떨어진 내륙이다'))
        if self.land[i] and self.coast_km[i]<700:
            near=self.tree.query_ball_point(self.g.points[i],800/6371.)
            sea=[k for k in near if not self.land[k]]
            if sea:
                a=float(np.mean(self.sst_anom[sea]))
                if a<-1.5:out.append(('cold_current','앞바다에 찬 해류(용승)가 흘러 서늘하고 안개는 있어도 비는 적다'))
                elif a>1.5:out.append(('warm_current','앞바다에 따뜻한 해류가 흘러 같은 위도보다 온화하다'))
        # height, continentality
        if self.z[i]>1500:out.append(('altitude',f'해발 약 {self.z[i]:.0f} m의 높은 땅이라 서늘하다'))
        rng=float(self.Ts[:,i].max()-self.Ts[:,i].min())
        if self.land[i] and rng>25:out.append(('continentality',f'계절 기온 차가 {rng:.0f} K로 큰 대륙성 기후다'))
        if not out:out.append(('zonal',f'위도 {alat:.0f}°의 일반적인 기후대에 속한다'))
        return out

    def explain(self,i,climate_name=''):
        rs=self.reasons(i);text='; '.join(t for _,t in rs)
        return dict(cell=int(i),keys=[k for k,_ in rs],text=(f'{climate_name}: ' if climate_name else '')+text)
