"""W4: water vapour, precipitation, evaporation and soil moisture for every season.

Unknowns per season s and cell i:
  * W  column water vapour [kg/m2]
  * m  soil wetness of the land bucket [0..1] (capacity `bucket_kg_m2`); ocean cells carry m = 1

Atmosphere (vapour adjusts in ~10 days, so each season is in balance):
    0 = E - P + conv(W)
  * conv: transport of W by the low-level wind of winds.py *including its convergent part*
    (upwind, flux form; low-level convergence is what makes the ITCZ and monsoon rain) plus
    eddy diffusion K_q
  * orographic blocking (2026-10-21): W of a cell is the vapour above its own ground, so air moving
    up onto higher ground carries only the share above the higher ground, b = exp(-(dz - 150 m) / H_v)
    (the moist boundary layer follows a gentle rise); the rest flows round the barrier: it leaves
    through the cell's other outflow edges (or stays, if every way out climbs) and rains only where
    the air is moist enough by the usual threshold, so dry air is not wrung out (optional forced
    slope rain: `orographic_rain_share`).  Eddy exchange between cells of different height mixes
    only the vapour above the higher ground.  Without it a plateau imported the whole lowland
    column and rained it out inland (Great Basin, Tibet, Ethiopia 3-12x too wet)
  * shallow heat lows (W15): the divergent part of the heat-low wind (winds: thermal pressure term) is balanced
    by a return flow just above the boundary layer that carries return_share of the column back out of the
    converging land cell, so a heat low fed by continental air stirs vapour without piling it up (Tarim, Gobi,
    continental interiors were rained on 3-30x by their own heat lows).  A heat low fed by marine air turns deep
    (monsoon) and keeps its vapour: the share is scaled by 1 - marine share of the low-level air, a tracer
    carried by the surface wind from the sea and fading on land over return_marine_km
  * P = (Ws / tau) * delta * softplus((W/Ws - r_c) / delta): rain once the column relative
    humidity W/Ws passes r_c, smoothly.  Ws = saturation column water ~ rho_vs(T) H_v, so
    where air is lifted onto high (cold) ground Ws drops, the vapour rains out on the windward
    side and the lee stays dry (rain shadow) with no extra rule
  * sea: E = rho C_E V q_s(T) (1 - RH_s), near-surface RH_s = a + (1 - a) W/Ws
  * land: E = beta(m) * Priestley-Taylor potential evaporation from the absorbed sunlight (energy limit), V = sqrt(|u|^2 + gust^2); beta = 1 over sea,
    beta(m) = smooth min(1, m / 0.75) over land (bucket model, Manabe 1969)
Land bucket, cyclic over seasons like the heat storage:
    Wmax (ns/year)(m_s - m_{s-1}) = P - E - runoff,   runoff = P m^runoff_power
Everything is smooth; Newton on all seasons at once with an analytic sparse Jacobian.
"""
from dataclasses import dataclass
import numpy as np
from scipy.sparse import coo_matrix,diags,bmat,identity
from scipy.sparse.linalg import splu
from .energy import YEAR

RV=461.5;RHO_AIR=1.2;DAY=86400.


@dataclass
class MoistureParams:
    vapour_scale_height_m:float=2000.
    critical_rh:float=.62
    rh_width:float=.04
    rain_time_s:float=1.*DAY
    exchange:float=1.3e-3       # C_E
    gust_m_s:float=5.
    wind_factor:float=1.3       # rotational part: moisture-weighted (lower-tropospheric) wind / surface wind
    divergent_factor:float=.35  # divergent (friction-layer in/outflow) part: only the boundary-layer share of W moves (W4 .5; .35 with frontal rain, 2026-10-21)
    return_share:float=.35      # W15 shallow heat lows: the mass-balancing return flow just above the boundary layer carries this
                                # share of the column back out of a converging land cell (= divergent_factor: a dry heat low only
                                # stirs vapour, it does not pile it up); 0 = off (as up to W14)
    return_heat_only:bool=True  # the return flow balances only the heat-low (thermal) part of the divergent wind
    return_marine_km:float=1000.  # heat lows fed by marine air (high moist static energy) turn deep like monsoons and keep their
                                # vapour: return share * (1 - marine share); marine character fades over this distance on land
    eddy_diffusivity:float=1.0e6  # K_q [m2/s]
    bucket_kg_m2:float=150.
    beta_wet:float=.75
    surface_rh_floor:float=.6   # near-surface RH = floor + (1 - floor) W/Ws over sea (buffered by the boundary layer)
    surface_rh_floor_land:float=.6  # same over land, used only without radiation (see setup)
    priestley_taylor:float=1.26  # land: E = beta(m) * alpha Delta/(Delta+gamma) Rn / L  (energy-limited)
    net_radiation_share:float=.55   # surface net radiation ~ share * absorbed solar - offset
    net_radiation_offset_w_m2:float=40.
    runoff_power:float=3.
    free_air:bool=False         # (tried: breaks convergence, no gain) saturation from the smoothed free-air temperature (see setup)
    rain_free_air:float=0.      # (tried 2026-10-21: < 0.1 mm/day change, coasts are dry because the air arriving is) convective rain: rain threshold measured against log-blend of surface (0) and free-air (1) saturation
    frontal_rate_per_day:float=.9   # frontal / storm-track rain: W * rate * (baroclinicity / ref) when the column is moist (.8 up to W15)
    frontal_gradient_ref:float=8.   # free-air temperature gradient [K / 1000 km] at which the rate applies
    frontal_power:float=1.      # rate ~ (gradient / ref)^power: > 1 sharpens the winter storm-track maximum
    frontal_subsidence:float=6e-6   # low-level divergence [1/s] where sinking air (east flank of subtropical highs) halves frontal rain; 0 = off
    frontal_subsidence_width:float=1.5e-6
    frontal_tropics_deg:float=25. # W16: baroclinic storms need rotation: frontal rate * s^4 / (s^4 + sin^4 this latitude), s = sin(lat);
                                # in the tropics surface land-sea contrasts do not reach the free troposphere (weak temperature
                                # gradient) and make no storms (they rained Namib, Peru, Bay of Bengal); 0 = off (as up to W15)
    frontal_rh:float=.52        # column RH (against the rain saturation) at which fronts start to rain
    frontal_width:float=.08
    lapse_k_m:float=6.5e-3
    smoothing_passes:int=3
    orographic_blocking:float=1.   # scales dz in b = exp(-dz / H_v) for vapour moving onto higher ground; 0 = off (as up to W11)
    orographic_rain_share:float=0.  # share of the blocked vapour forced to rain on the slope; the rest is deflected
                                    # (1 rained dry desert air too: Arabia 0.3 -> 3 mm/day)
    orographic_deflect:bool=True    # deflected vapour leaves through the cell's other (not climbing) outflow edges, else it stays
    orographic_free_rise_m:float=150.  # rise between neighbours the moist boundary layer simply follows (no blocking)
    orographic_eddy:bool=True       # eddy exchange between different heights mixes only the vapour above the higher ground
    orographic_rain_split:float=.5  # share of that forced rain on the higher cell (rest on the lower, windward)


def softplus(x):
    return np.where(x>30,x,np.log1p(np.exp(np.minimum(x,30)))),1/(1+np.exp(-x))


class Moisture:
    def __init__(self,grid,land_fraction,elevation_m,*,seasons=4,params=None,geometry=None):
        from .winds import EdgeGeometry
        self.g=g=grid;self.p=params or MoistureParams();self.ns=seasons;self.rate=seasons/YEAR
        self.f=np.clip(np.asarray(land_fraction,float),0,1);self.land=self.f>.5
        self.z=np.maximum(np.asarray(elevation_m,float),0)*(self.f>0);self.ps=1e5*np.exp(-self.z/8000.)
        self.geo=geometry or EdgeGeometry(grid)
        self.eddy_operator()

    def blocking(self):
        """Per edge: share of the lower cell's vapour that lies above the higher cell's ground, and which end is lower."""
        g=self.g;dz=self.z[g.right]-self.z[g.left];p=self.p
        return np.exp(-p.orographic_blocking*np.maximum(abs(dz)-p.orographic_free_rise_m,0.)/p.vapour_scale_height_m),dz

    def eddy_operator(self):
        g=self.g;kq=self.p.eddy_diffusivity*g.edge_length/g.distance;i,j=g.left,g.right;n=g.n
        self.kq_built=(self.p.eddy_diffusivity,self.p.orographic_blocking,self.p.orographic_free_rise_m,self.p.orographic_eddy)
        b,dz=self.blocking();b=b if self.p.orographic_eddy else np.ones_like(b)
        ci=np.where(dz>0,b,1.);cj=np.where(dz<0,b,1.)                      # only vapour above the higher ground mixes
        # flux i -> j = kq (ci W_i - cj W_j)
        self.Kq=coo_matrix((np.r_[kq*cj,kq*ci,-kq*ci,-kq*cj],(np.r_[i,j,i,j],np.r_[j,i,i,j])),shape=(n,n)).tocsr().multiply(1/g.area[:,None]).tocsr()

    # ---- thermodynamics --------------------------------------------------------------------
    def saturation_column(self,T):
        Tc=T-273.15;es=610.94*np.exp(17.625*Tc/(Tc+243.04));Ws=es/(RV*T)*self.p.vapour_scale_height_m
        return Ws

    def qsat(self,T):
        Tc=T-273.15;es=610.94*np.exp(17.625*Tc/(Tc+243.04));return .622*es/self.ps

    # ---- operators for given winds ----------------------------------------------------------
    def edge_flux(self,wind3):
        g=self.g;ue=.5*(wind3[g.left]+wind3[g.right]);return np.sum(ue*self.geo.n_mid,-1)*g.edge_length

    def transport(self,wind3,ret=None,heat3=None):
        """Sparse (A, O): conv(W) = A @ W [kg/m2/s] by upwind advection with the full wind + eddy diffusion,
        and the orographic rain O @ W of the vapour blocked where the flow climbs onto higher ground.
        ret (n,): share of the column the return flow above the boundary layer carries out of each cell;
        it balances the divergent part of the heat-low wind heat3 (or of the whole wind without heat3)."""
        g=self.g;i,j=g.left,g.right;n=g.n
        F=self.edge_flux(wind3);Fd=None
        if getattr(self,'project',None) is not None:
            Fr=self.project(F);Fd=F-Fr;F=self.p.wind_factor*Fr+self.p.divergent_factor*Fd
            if heat3 is not None:Fh=self.edge_flux(heat3);Fd=Fh-self.project(Fh)
        else:F=self.p.wind_factor*F
        up=np.where(F>=0,i,j);down=np.where(F>=0,j,i);a=abs(F)
        b,dz=self.blocking();climbs=np.where(F>=0,dz>0,dz<0);b=np.where(climbs,b,1.)
        r=self.p.orographic_rain_share;lost=a*(1-b)*r;out=a*b
        if self.p.orographic_deflect:
            # the deflected share (1 - b)(1 - r) flows round: added to the cell's free outflow edges in proportion
            free=b>=1.;D=np.bincount(up,a*(1-b)*(1-r),n);C=np.bincount(up,a*free,n)
            out=np.where(free&(C[up]>0),a*(1+D[up]/np.maximum(C[up],1e-30)),out)   # no free edge: it stays
        A=coo_matrix((np.r_[-(out+lost),out],(np.r_[up,down],np.r_[up,up])),shape=(n,n)).tocsr().multiply(1/g.area[:,None])
        if Fd is not None and ret is not None and np.any(ret>0):
            # the divergent low-level flux up -> dn is balanced aloft by dn -> up, carrying ret[dn] W[dn]
            ud=np.where(Fd>=0,i,j);dd=np.where(Fd>=0,j,i);r=abs(Fd)*ret[dd]*(self.f[dd] if heat3 is not None else 1.)
            A=A+coo_matrix((np.r_[-r,r],(np.r_[dd,ud],np.r_[dd,dd])),shape=(n,n)).tocsr().multiply(1/g.area[:,None])
        k=self.p.orographic_rain_split
        O=coo_matrix((np.r_[lost*k,lost*(1-k)],(np.r_[down,up],np.r_[up,up])),shape=(n,n)).tocsr().multiply(1/g.area[:,None]).tocsr()
        return (A+self.Kq).tocsr(),O

    def setup(self,T,circulation,smooth=None,absorbed=None):
        """Freeze temperature-dependent pieces and winds for one solve.

        With free_air the saturation column uses the free-tropospheric temperature, which is
        horizontally smooth: theta = T + lapse z is smoothed (, ~700 km) and brought back to
        the local height.  A hot land surface then does not raise the rain threshold (convection
        over summer land), while high ground still has a colder, lower threshold (orographic rain).
        """
        p=self.p;self.T=np.asarray(T);self.qs=self.qsat(self.T)
        smooth=smooth or getattr(self,'smooth',None)
        if p.free_air and smooth is not None:
            th=self.T+p.lapse_k_m*self.z
            for _ in range(p.smoothing_passes):th=np.array([smooth(x) for x in th])
            self.Tfree=th-p.lapse_k_m*self.z
        else:self.Tfree=self.T
        self.Ws=self.saturation_column(self.Tfree)
        if self.kq_built!=(p.eddy_diffusivity,p.orographic_blocking,p.orographic_free_rise_m,p.orographic_eddy):self.eddy_operator()
        # rain saturation (convective rain) and baroclinicity (frontal rain) from the free troposphere
        self.Wp=self.Ws;self.front=None
        if (p.rain_free_air>0 or p.frontal_rate_per_day>0) and smooth is not None:
            th=self.T+p.lapse_k_m*self.z
            for _ in range(p.smoothing_passes):th=np.array([smooth(x) for x in th])
            if p.rain_free_air>0:
                b=p.rain_free_air;self.Wp=self.Ws**(1-b)*self.saturation_column(th-p.lapse_k_m*self.z)**b
            if p.frontal_rate_per_day>0:
                self.front=p.frontal_rate_per_day/DAY*(self.baroclinicity(th)/p.frontal_gradient_ref)**p.frontal_power
                if p.frontal_tropics_deg>0:
                    s4=np.sin(self.g.lat)**4;self.front=self.front*s4/(s4+np.sin(np.radians(p.frontal_tropics_deg))**4)
                if p.frontal_subsidence>0:
                    div=np.array([self.divergence(c['wind3']) for c in circulation])
                    self.front=self.front/(1+np.exp(np.clip((div-p.frontal_subsidence)/p.frontal_subsidence_width,-60,60)))
        self.circ=circulation;self.ret=np.full((self.ns,self.g.n),p.return_share) if p.return_share>0 else None
        if self.ret is not None and p.return_marine_km>0:
            self.marine=np.array([self.marine_air(c['wind3']) for c in circulation]);self.ret=p.return_share*(1-self.marine)
        self.build_transport()
        V=np.array([np.sqrt(np.sum(c['wind3']**2,-1)+p.gust_m_s**2) for c in circulation])
        self.ce=RHO_AIR*p.exchange*V*self.qs                         # potential evaporation scale [kg/m2/s]
        # land: energy-limited (Priestley-Taylor) potential evaporation from the absorbed sunlight
        absorbed=absorbed if absorbed is not None else getattr(self,'absorbed',None);self.ept=None
        if absorbed is not None:
            Tc=self.T-273.15;es=610.94*np.exp(17.625*Tc/(Tc+243.04));des=es*17.625*243.04/(Tc+243.04)**2
            gamma=1004.*self.ps/(.622*2.501e6);rn=p.net_radiation_share*np.asarray(absorbed)-p.net_radiation_offset_w_m2
            x=rn/10.;rn=10.*np.where(x>30,x,np.log1p(np.exp(np.minimum(x,30))))   # smooth max(rn, 0)
            self.ept=p.priestley_taylor*des/(des+gamma)*rn/2.501e6

    def marine_air(self,wind3):
        """Share of marine air in the low-level flow of every cell (1 over sea), carried upwind by the surface wind and
        fading on land as exp(-distance / return_marine_km): c_i (inflow_i + area_i |u_i| / L) = sum_in inflow c_up
        (inflow-weighted upwind mean; a cell with no inflow holds subsided, non-marine air)."""
        g=self.g;i,j=g.left,g.right;n=g.n;F=self.edge_flux(wind3);up=np.where(F>=0,i,j);dn=np.where(F>=0,j,i);a=abs(F)
        sp=np.maximum(np.linalg.norm(wind3,axis=-1),1.);loss=g.area*sp/(self.p.return_marine_km*1e3)
        sea=~self.land;M=coo_matrix((np.r_[a,-a],(np.r_[dn,dn],np.r_[dn,up])),shape=(n,n)).tocsr()+diags(loss)
        M=M.tolil();rhs=np.zeros(n)
        for k in np.flatnonzero(sea):M.rows[k]=[k];M.data[k]=[1.];rhs[k]=1.
        return np.clip(splu(M.tocsc()).solve(rhs),0,1)

    def build_transport(self):
        heat=self.p.return_heat_only and self.ret is not None
        if heat and any('wind3_heat' not in c for c in self.circ):
            raise KeyError("circulation lacks 'wind3_heat' (winds.Atmosphere.seasons adds it; old frozen climates: Atmosphere.heat_wind)")
        AO=[self.transport(c['wind3'],None if self.ret is None else self.ret[s],c['wind3_heat'] if heat else None) for s,c in enumerate(self.circ)]
        self.A=[a for a,_ in AO];self.O=[o for _,o in AO]

    def divergence(self,wind3):
        """Low-level divergence of the surface wind [1/s]: > 0 where air sinks and spreads out."""
        g=self.g;i,j=g.left,g.right;ue=.5*(wind3[i]+wind3[j]);F=np.sum(ue*self.geo.n_mid,-1)*g.edge_length
        return (np.bincount(i,F,g.n)-np.bincount(j,F,g.n))/g.area

    def baroclinicity(self,th):
        """|grad theta| of the smoothed free-air temperature per season [K / 1000 km] (edge differences)."""
        g=self.g;i,j=g.left,g.right;n=g.n;cnt=np.bincount(i,minlength=n)+np.bincount(j,minlength=n)
        out=[]
        for t in th:
            e2=((t[j]-t[i])/g.distance)**2
            out.append(np.sqrt(2*(np.bincount(i,e2,n)+np.bincount(j,e2,n))/np.maximum(cnt,1))*1e6)
        return np.array(out)

    # ---- pieces with derivatives ------------------------------------------------------------
    def precip(self,W):
        """Large-scale/convective rain above the critical column RH, plus frontal rain (storm tracks).

        Convective: the threshold is measured against Wp, which (rain_free_air > 0) leans toward the
        saturation of the smoothed free troposphere: a hot land surface does not lift the threshold
        (summer convection, monsoon coasts) and high ground keeps its colder, lower threshold.
        Frontal: baroclinic eddies lift moist air wherever the free-air temperature gradient is large,
        also below the critical RH: P_f = W * rate * |grad theta| / ref * sigmoid((W/Wp - rh_f) / width),
        fading equatorward of frontal_tropics_deg (W16: no baroclinic storms without rotation)."""
        p=self.p;Wp=self.Wp;r=W/Wp;sp,sg=softplus((r-p.critical_rh)/p.rh_width)
        P=Wp/p.rain_time_s*p.rh_width*sp;dP=sg/p.rain_time_s
        if self.front is not None:
            x=np.clip((r-p.frontal_rh)/p.frontal_width,-60,60);sf=1/(1+np.exp(-x))
            P=P+self.front*W*sf;dP=dP+self.front*(sf+W*sf*(1-sf)/(p.frontal_width*Wp))
        return P,dP

    def orographic(self,W):
        """Rain from the vapour blocked on slopes [kg/m2/s] per season (linear in W)."""
        return np.array([self.O[s]@W[s] for s in range(self.ns)])

    def frontal(self,W):
        """The frontal (storm-track) part of the rain for column vapour W [kg/m2/s]; zeros when off."""
        if self.front is None:return np.zeros_like(np.asarray(W,float))
        p=self.p;x=np.clip((W/self.Wp-p.frontal_rh)/p.frontal_width,-60,60);return self.front*W/(1+np.exp(-x))

    def beta(self,m):
        p=self.p;x=m/p.beta_wet;k=20.
        # smooth min(1, x) for x >= 0
        b=x-np.log1p(np.exp(k*(x-1)))/k;db=(1-1/(1+np.exp(-k*(x-1))))/p.beta_wet
        b=np.where(self.land,b,1.);db=np.where(self.land,db,0.);return b,db

    def evap(self,W,m):
        b,db=self.beta(m);k=np.where(self.land,1-self.p.surface_rh_floor_land,1-self.p.surface_rh_floor);s=k*(1-W/self.Ws)
        E,dW,dm=self.ce*s*b,-k*self.ce/self.Ws*b,self.ce*s*db
        if self.ept is not None:
            E=np.where(self.land,self.ept*b,E);dW=np.where(self.land,0.,dW);dm=np.where(self.land,self.ept*db,dm)
        return E,dW,dm

    def residual(self,x):
        ns,n=self.ns,self.g.n;W=x[:ns*n].reshape(ns,n);m=x[ns*n:].reshape(ns,n);p=self.p
        P,_=self.precip(W);E,_,_=self.evap(W,m)
        rw=np.array([E[s]-P[s]+self.A[s]@W[s] for s in range(ns)])
        P=P+self.orographic(W)                                         # blocked vapour reaches the ground too
        mp=np.clip(m,0,None)**p.runoff_power;R=P*mp
        rm=p.bucket_kg_m2*self.rate*(m-np.roll(m,1,0))-(P-E-R)
        rm=np.where(self.land,rm,(m-1)*1e-4)
        return np.r_[rw.ravel(),rm.ravel()]*DAY                      # mm/day

    def jacobian(self,x):
        ns,n=self.ns,self.g.n;W=x[:ns*n].reshape(ns,n);m=x[ns*n:].reshape(ns,n);p=self.p
        P,dP=self.precip(W);E,dEw,dEm=self.evap(W,m);mc=np.clip(m,0,None);P=P+self.orographic(W)
        mp=mc**p.runoff_power;dmp=p.runoff_power*mc**(p.runoff_power-1)
        c=p.bucket_kg_m2*self.rate;L=self.land
        WW=[[None]*ns for _ in range(ns)];WM=[[None]*ns for _ in range(ns)];MW=[[None]*ns for _ in range(ns)];MM=[[None]*ns for _ in range(ns)]
        for s in range(ns):
            WW[s][s]=(diags(dEw[s]-dP[s])+self.A[s]).tocsr();WM[s][s]=diags(dEm[s])
            # rm = c (m - m_prev) - (P + O W) (1 - m^k) + E
            MW[s][s]=(diags(np.where(L,-(1-mp[s]),0.))@(diags(dP[s])+self.O[s])+diags(np.where(L,dEw[s],0.))).tocsr()
            MM[s][s]=diags(np.where(L,c+dEm[s]+P[s]*dmp[s],1e-4))
            prev=(s-1)%ns
            if prev!=s:MM[s][prev]=diags(np.where(L,-c,0.))
        J=bmat([[bmat(WW),bmat(WM)],[bmat(MW),bmat(MM)]],format='csc')
        return J*DAY

    def solve(self,T,circulation,x0=None,*,tolerance=1e-6,max_iterations=80,smooth=None,absorbed=None):
        """Returns dict(W, m, P, E, runoff [kg/m2/s], converged, iterations).  tolerance in mm/day."""
        self.setup(T,circulation,smooth,absorbed);ns,n=self.ns,self.g.n
        x=np.r_[(.7*self.Ws).ravel(),np.where(self.land,.5,1.)[None,:].repeat(ns,0).ravel()] if x0 is None else x0.copy()
        f=self.residual(x)
        for it in range(max_iterations):
            err=float(np.max(abs(f)))
            if err<=tolerance:break
            step=splu(self.jacobian(x)).solve(-f);merit=float(f@f);a=1.
            for _ in range(40):
                t=x+a*step;t[:ns*n]=np.maximum(t[:ns*n],1e-6);t[ns*n:]=np.clip(t[ns*n:],0,1.2)
                ft=self.residual(t)
                if float(ft@ft)<=(1-1e-4*a)*merit:break
                a*=.5
            x,f=t,ft
        W=x[:ns*n].reshape(ns,n);m=x[ns*n:].reshape(ns,n)
        P,_=self.precip(W);P=P+self.orographic(W);E,_,_=self.evap(W,m);R=np.where(self.land,P*np.clip(m,0,None)**self.p.runoff_power,0.)
        return dict(x=x,W=W,m=m,P=P,E=E,runoff=R,rh=W/self.Ws,converged=bool(np.max(abs(f))<=tolerance),
                    iterations=it,max_residual_mm_day=float(np.max(abs(f))))
