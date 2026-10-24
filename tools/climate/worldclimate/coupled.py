"""W3 driver: temperature <-> winds <-> gyres, solved by an outer fixed-point loop.

Each outer pass: winds and stress from the current temperature (winds.py), Stommel
streamfunction, Ekman fluxes and wind mass fluxes per season (ocean.py, winds.py), heat
operators installed in the energy model, then a full Newton solve of the seasonal energy
balance (energy.py). The circulation is relaxed between passes; the loop stops when the
temperature change is below `tol_k`.
"""
import time
import numpy as np
from .energy import EnergyModel,Params
from .winds import Atmosphere
from .ocean import Ocean,soft_floor
from .moisture import Moisture


class Climate:
    def __init__(self,grid,land_fraction,elevation_m,*,seasons=4,energy=None,wind=None,ocean=None,
                 ocean_on=True,wind_heat_on=True,moisture=None,moisture_on=True,dry_albedo=0.,dry_rh=1.,arid_surface_k=3.,
                 inversion_k=.35,inversion_ref_k=263.):
        self.g=grid;self.f=land_fraction;self.z=elevation_m;self.ns=seasons
        # W6 calibration against CMIP6 t2m (Earth, lat > -60, docs/worldbuilding-climate-progress.md W6):
        # only A, D and the land heat capacity move from the literature values; A 205 -> 204.3 in W10
        # with the weaker wind heat advection (global mean +0.17 K, seasonal RMSE 4.22 -> 3.97 K);
        # W11: sea ice stores heat like air over ice (2e7 J/m2/K) plus the latent heat of a ~2 m pack,
        # centred at -3 C so a melting pack holds the summer air near 0 C; A 204.3 -> 205.8 for the
        # less icy planet (seasonal RMSE 3.97 -> 3.68 K, Arctic summer -13.4 -> -1.5 C, obs -0.9)
        # W16: A 205.8 -> 206.1 with the stronger wind heat advection (winds.advection_mass_fraction .2)
        self.energy=EnergyModel(grid,land_fraction,elevation_m,seasons=seasons,params=energy or
                                Params(A=206.1,D=.95e6,land_heat_capacity=5e6,sea_ice_heat_capacity=2e7,sea_ice_latent_j_m2=6e8,
                                       sea_ice_T=270.15,sea_ice_W=2.))
        self.atm=Atmosphere(grid,land_fraction,elevation_m,params=wind)
        self.ocn=Ocean(grid,land_fraction,params=ocean,geometry=self.atm.geo)
        self.auto_deepwater=self.ocn.p.deepwater_auto and not self.ocn.p.deepwater_sites
        self.outlets=self.ocn.river_outlets(elevation_m) if self.auto_deepwater else None
        self.ocean_on=ocean_on;self.wind_heat_on=wind_heat_on
        self.water=Moisture(grid,land_fraction,elevation_m,seasons=seasons,params=moisture,geometry=self.atm.geo)
        self.water.smooth=self.atm.smoother.solve;self.water.project=self.atm.rotational
        self.moisture_on=moisture_on;self.dry_albedo=dry_albedo;self.dry_rh=dry_rh;self.arid_surface_k=arid_surface_k;self.inversion=(inversion_k,inversion_ref_k);self.wet=None;self.rh0=self.energy.p.RH

    def hydrology(self,T,circ):
        """W4: moisture balance for T and winds.

        Optional feedbacks of land dryness on the energy balance (off by default): lower MSE humidity
        (dry_rh < 1) makes the outer loop oscillate by 5-10 K, because RH contrasts inside the MSE
        diffusion act as a second, much stronger moisture transport; brighter dry land (dry_albedo)
        is largely offset in reality by fewer clouds.  See surface_temperature for the dry lapse effect.
        """
        ab=self.energy.Q*(1-self.energy.albedo(np.asarray(T))[0])
        w=self.water.solve(T,circ,None if self.wet is None else self.wet['x'],absorbed=ab)
        if not w['converged']:w=self.water.solve(T,circ,absorbed=ab)
        self.wet=w;b,_=self.water.beta(w['m']);land=self.water.f
        self.energy.p.RH=self.rh0*(1-land*(1-self.dry_rh)*(1-b))
        self.energy.albedo_extra=self.dry_albedo*land*(1-b)
        return w

    def circulation(self,T):
        dry=None
        if self.atm.p.heat_low_dry_k and self.wet is not None:     # dryness of the last moisture solve (lagged in the outer loop)
            b,_=self.water.beta(self.wet['m']);dry=self.water.f*(1-b)
        out=self.atm.seasons(T,dry)
        for d in out:
            d['psi']=self.ocn.streamfunction(d['stress3']);d['mass_flux']=self.atm.mass_flux(d['wind3'])
        return out

    def install(self,state):
        """state: per season dict(psi, stress3, mass_flux) (already relaxed)."""
        if self.ocean_on:self.energy.set_ocean_heat([self.ocn.season_operator(s['psi'],s['stress3']) for s in state])
        else:self.energy.set_ocean_heat(None)
        if self.wind_heat_on:self.energy.set_atm_heat([self.atm.heat_operator(s['mass_flux']) for s in state])
        else:self.energy.set_atm_heat(None)

    def _map(self,T):
        """Fixed-point map: circulation from T, then the energy balance with that circulation."""
        circ=self.circulation(T);state=[{q:c[q] for q in ('psi','stress3','mass_flux')} for c in circ]
        if self.moisture_on:self.hydrology(T,circ)
        # deep water sinks where this T, these currents and this E - P make the surface densest
        # (salinity uses the previous pass's overturning: the salt-advection feedback is in the outer loop)
        if self.ocean_on and self.auto_deepwater and self.wet is not None:self.ocn.moc=self.ocn.auto_overturning(T,state,self.wet,self.outlets)
        self.install(state);r=self.energy.solve(T);return r,state

    def solve(self,T0,*,outer=30,memory=5,mixing=.7,tol_k=.05,log=None):
        """Outer loop with Anderson acceleration on T (memory `memory`, mixing `mixing`).

        G(T) = energy solution with the circulation diagnosed from T; we seek T = G(T).
        Anderson mixing handles the slow coupled modes (e.g. the equatorial cold tongue ->
        trade wind -> upwelling feedback) that plain relaxation converges only slowly.
        """
        t0=time.perf_counter();r=self.energy.solve(T0);T=r['T'];state=None;hist=[]
        if not r['converged']:return dict(r,outer=0,history=hist,converged=False)
        if self.ocean_on or self.wind_heat_on or self.moisture_on:
            X=[];Rs=[];x=T.ravel()
            for k in range(outer):
                r,state=self._map(x.reshape(T.shape))
                if not r['converged']:hist.append(dict(outer=k,newton=r['iterations'],converged=False));break
                gx=r['T'].ravel();res=gx-x;dT=float(np.max(abs(res)))
                hist.append(dict(outer=k,newton=r['iterations'],max_dT_k=dT,converged=True))
                if log is not None:log(hist[-1])
                if dT<tol_k:x=gx;break
                X.append(x.copy());Rs.append(res.copy());X=X[-memory-1:];Rs=Rs[-memory-1:]
                if len(Rs)>1:
                    dR=np.array([Rs[i+1]-Rs[i] for i in range(len(Rs)-1)]).T;dX=np.array([X[i+1]-X[i] for i in range(len(X)-1)]).T
                    gam=np.linalg.lstsq(dR,res,rcond=None)[0]
                    x=x+mixing*res-(dX+mixing*dR)@gam
                else:x=x+mixing*res
            T=x.reshape(T.shape)
            if hist and hist[-1].get('max_dT_k',1e9)<tol_k:T=r['T']
            else:r,state=self._map(T);T=r['T']
        circ=self.circulation(T)
        if self.moisture_on:self.hydrology(T,circ)
        return dict(T=T,water=self.wet,residual=r['residual'],converged=bool(r['converged'] and (not hist or hist[-1].get('max_dT_k',1e9)<tol_k)),
                    outer=len(hist),history=hist,circulation=circ,state=state,psi_used=None if state is None else [s['psi'] for s in state],
                    seconds=time.perf_counter()-t0)

    def surface_temperature(self,T,water=None):
        """Near-surface air temperature for display / classification.

        T is the column-consistent temperature of the energy balance.  Over dry land the boundary layer
        follows a dry rather than a moist lapse rate, so the surface is warmer for the same column
        energy: + arid_surface_k * land * (1 - beta(soil wetness)).  Diagnostic, no feedback.
        Over cold land a surface inversion forms (long nights, snow, clear skies): the surface is colder
        than the column by inversion_k * softplus(ref - T), e.g. -3 K at -19 C, -7 K at -30 C (k = 0.35).
        """
        T=np.asarray(T);k,ref=self.inversion;x=ref-T;sp=np.where(x>30,x,np.log1p(np.exp(np.minimum(x,30))))
        out=T-k*sp*self.water.f
        water=water or self.wet
        if water is None:return out
        b,_=self.water.beta(water['m']);return out+self.arid_surface_k*self.water.f*(1-b)

    def heat_transport_pw(self,T,state,lat_edges=np.arange(-80,81,10)):
        """Annual-mean northward heat transport [PW]: ocean gyres, ocean Ekman cells, wind advection."""
        gy=np.zeros(len(lat_edges));ek=np.zeros(len(lat_edges));at=np.zeros(len(lat_edges))
        for s,st in enumerate(state):
            S,_=soft_floor(T[s]);H=self.ocn.heat_operator(self.ocn.edge_flux(st['psi']))
            gy+=self.ocn.meridional_heat_transport(H,S,lat_edges)
            He,be,_=self.ocn.ekman_operator(st['stress3']);ek+=self.ocn.meridional_heat_transport(He,S,lat_edges,be)
            h,_=self.energy.mse(T[s]);A=self.atm.heat_operator(st['mass_flux'])
            at+=self.ocn.meridional_heat_transport(A,h,lat_edges)
        n=len(state);return dict(gyre=list(np.round(gy/n,3)),ekman=list(np.round(ek/n,3)),wind_advection=list(np.round(at/n,3)))

    def diffusive_transport_pw(self,T,lat_edges=np.arange(-80,81,10)):
        """Annual-mean northward transport of the MSE diffusion [PW]."""
        acc=np.zeros(len(lat_edges));lat=np.degrees(self.g.lat)
        for s in range(self.ns):
            h,_=self.energy.mse(T[s]);conv=(self.energy.L@h)*self.g.area
            acc+=[conv[lat>=L].sum()/1e15 for L in lat_edges]
        return list(np.round(acc/self.ns,3))


def solutions(climate,*,starts=None,distinct_k=1.,log=None,on_start=None):
    """W6: search for several climate states (e.g. warm and snowball) from different first guesses.

    Returns a list of dict(start, result, global_mean_c, floquet, stable), duplicates (global mean
    within distinct_k) removed, warmest first.
    """
    g=climate.g;s2=np.sin(g.lat)**2;ns=climate.ns
    starts=starts or dict(warm=300-40*s2,cool=280-45*s2,cold=np.full(g.n,230.))
    w=g.area/g.area.sum();found=[]
    for name,T0 in starts.items():
        # forget the previous state's circulation and moisture so each start is independent
        climate.energy.set_ocean_heat(None);climate.energy.set_atm_heat(None);climate.wet=None
        if climate.auto_deepwater:climate.ocn.moc=None
        if on_start is not None:on_start(name)
        r=climate.solve(np.broadcast_to(T0,(ns,g.n)),log=log)
        if not r['converged']:found.append(dict(start=name,converged=False));continue
        gm=float(r['T'].mean(0)@w-273.15)
        if any(abs(gm-x['global_mean_c'])<distinct_k for x in found if x.get('converged',True) and 'global_mean_c' in x):continue
        try:fl=[float(v) for v in climate.energy.stability(r['T'],count=2)]
        except Exception:fl=[float('nan')]
        found.append(dict(start=name,converged=True,result=r,global_mean_c=gm,floquet=fl,stable=bool(max(fl)<1)))
    ok=[x for x in found if x.get('converged')]
    return sorted(ok,key=lambda x:-x['global_mean_c'])+[x for x in found if not x.get('converged')]
