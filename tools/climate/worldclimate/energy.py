"""W2: seasonal energy-balance climate on the icosahedral grid.

Unknown: near-surface air temperature T[s, i] (K) for every season s and cell i.
Each season and cell satisfies, in W/m2,

    (n_s / year) (H_i(T[s]) - H_i(T[s-1])) = Q[s] (1 - albedo(T)) - OLR(T) + div(K grad h)

  * Q: seasonal-mean daily insolation (insolation.py)
  * albedo: ice-free planetary albedo 0.303 + 0.0779 P2(sin lat) (North & Coakley 1979),
    blended smoothly to 0.62 (ice/snow, North 1975) below T_ICE (logistic width W_ICE)
  * OLR = A + B (T - 273.15), A = 203.3 W/m2, B = 2.09 W/m2/K (North & Coakley 1979)
  * transport: diffusion of moist static energy h = cp T + g z + L RH q_sat(T, p)
    with the column mass p_s/g and D = 1.06e6 m2/s (Hwang & Frierson 2010).  D stands for the
    meridional eddy flux; east-west exchange (between a continent and the ocean at the same
    latitude) is carried by the wind advection of winds.py, so across east-west edges only the
    share `zonal_diffusion` of D acts (tensor D (n n^T + zonal e e^T); isotropic near the poles)
  * H: heat content with heat capacity C = dH/dT: ocean mixed layer (60 m) or land + atmosphere
    column, by land fraction.  With `sea_ice_heat_capacity`, sea ice (a logistic in T with its own
    centre `sea_ice_T` near the melting point, not the albedo's seasonal-mean T_ICE)
    cuts the air off from the water below, so the ice-covered share of a sea cell stores heat like
    air over ice (C ~ land), not like 60 m of water: polar seas cool fast in winter and warm to the
    melting point in summer, and the ice retreats.  `sea_ice_latent_j_m2` adds the latent heat of the
    ice pack (L_f rho_i thickness), spread over the ice curve: a melting pack holds the air near the
    melting point until the ice is gone, a freezing sea gives that heat back.  H is the integral of C
    (no spurious energy over the cycle)
  * seasons are cyclic: season 0 follows season n_s - 1 (no initial year)

Every term is smooth, so the periodic system is solved directly with Newton and
an analytic sparse Jacobian. No time stepping.
"""
from dataclasses import dataclass,field
import numpy as np
from scipy.sparse import diags,bmat,csr_matrix,identity
from scipy.sparse.linalg import splu
from .insolation import seasonal_insolation

YEAR=365.2422*86400
CP=1004.;LV=2.501e6;G=9.80665;RHO_W=1025.;CP_W=3990.


@dataclass
class Params:
    A:float=203.3            # OLR at 0 C [W/m2]
    B:float=2.09             # OLR slope [W/m2/K]
    albedo_ice:float=.62
    T_ICE:float=268.15       # seasonal-mean temperature of half ice/snow cover [K]
    W_ICE:float=3.           # logistic width [K]
    D:float=1.06e6           # moist static energy diffusivity [m2/s]
    RH:float=.8
    mixed_layer_m:float=60.
    land_heat_capacity:float=1.0e7   # land + atmosphere column [J/m2/K]
    sea_ice_heat_capacity:float|None=None   # air + ice over ice-covered sea [J/m2/K]; None: open water (as up to W10)
    sea_ice_latent_j_m2:float=0.     # latent heat of the seasonal ice pack (with sea_ice_heat_capacity)
    sea_ice_T:float|None=None        # centre / width [K] of the sea-ice curve for heat storage (None: T_ICE / W_ICE)
    sea_ice_W:float|None=None
    scale_height_m:float=8000.
    zonal_diffusion:float=1.         # share of D across east-west gradients (1 = isotropic, as up to W8)
    obliquity_deg:float|None=None    # None: profile value


class EnergyModel:
    def __init__(self,grid,land_fraction,elevation_m,*,seasons=4,params=None):
        self.g=grid;self.ns=seasons;self.p=p=params or Params()
        self.f=np.clip(np.asarray(land_fraction,float),0,1);self.z=np.maximum(np.asarray(elevation_m,float),0.)*(self.f>0)
        self.Q=seasonal_insolation(grid.lat,seasons,obliquity_deg=p.obliquity_deg)
        x=np.sin(grid.lat);self.albedo_free=.303+.0779*(3*x*x-1)/2
        self.C=self.f*p.land_heat_capacity+(1-self.f)*RHO_W*CP_W*p.mixed_layer_m   # ice-free
        self.dC_ice=None if p.sea_ice_heat_capacity is None else (1-self.f)*(p.sea_ice_heat_capacity-RHO_W*CP_W*p.mixed_layer_m)
        self.ps=1e5*np.exp(-self.z/p.scale_height_m)
        mass=self.ps/G;k=.5*(mass[grid.left]+mass[grid.right])*p.D*self.edge_weight(grid,p.zonal_diffusion)
        self.L=grid.laplacian(k)      # (L h)_i in W/m2 for h in J/kg
        self.rate=self.ns/YEAR
        self.ocean_heat=None          # W3: per season (H, b): ocean convergence = H @ S(T) + b (ocean.py)
        self.albedo_extra=0.          # W4: per (season, cell) albedo added to the ice-free value
        self.atm_heat=None            # W3: per season A: wind-advection convergence = A @ h(T) (winds.py)

    @staticmethod
    def edge_weight(grid,zonal):
        """(d.N)^2 + zonal (d.E)^2 for the unit edge direction d at the edge midpoint (north N, east E);
        blended to 1 poleward of 70-85 deg where east and north lose their meaning on a 480 km grid."""
        if zonal==1:return 1.
        a=grid.points[grid.left];b=grid.points[grid.right];m=a+b;m/=np.linalg.norm(m,axis=1,keepdims=True)
        d=b-a;d/=np.linalg.norm(d,axis=1,keepdims=True)
        lat=np.arcsin(np.clip(m[:,2],-1,1));lon=np.arctan2(m[:,1],m[:,0])
        E=np.stack([-np.sin(lon),np.cos(lon),0*lon],-1);N=np.stack([-np.sin(lat)*np.cos(lon),-np.sin(lat)*np.sin(lon),np.cos(lat)],-1)
        w=np.sum(d*N,1)**2+zonal*np.sum(d*E,1)**2
        t=np.clip((np.degrees(abs(lat))-70)/15,0,1);t=t*t*(3-2*t)
        return w*(1-t)+t

    def set_ocean_heat(self,operators,floor=271.35,width=1.):
        """Install ocean heat transport operators (one per season: H or (H, b); None removes)."""
        self.ocean_heat=None if operators is None else [o if isinstance(o,tuple) else (o,0.) for o in operators]
        self._floor=(floor,width)

    def set_atm_heat(self,operators):
        self.atm_heat=operators

    def _ocean(self,T,s):
        from .ocean import soft_floor
        S,dS=soft_floor(T[s],*self._floor);H,b=self.ocean_heat[s];return H@S+b,H@diags(dS)

    # ----- constitutive pieces (value, derivative wrt T) --------------------------
    def qsat(self,T):
        Tc=T-273.15;es=610.94*np.exp(17.625*Tc/(Tc+243.04))
        des=es*17.625*243.04/(Tc+243.04)**2
        return .622*es/self.ps,.622*des/self.ps

    def mse(self,T):
        q,dq=self.qsat(T);p=self.p
        return CP*T+G*self.z+LV*p.RH*q,CP+LV*p.RH*dq

    def heat(self,T):
        """Heat content H(T) [J/m2, up to a constant] and heat capacity C(T) = dH/dT."""
        T=np.asarray(T)
        if self.dC_ice is None:return self.C*T,np.broadcast_to(self.C,T.shape)
        p=self.p;T0=p.T_ICE if p.sea_ice_T is None else p.sea_ice_T;w=p.W_ICE if p.sea_ice_W is None else p.sea_ice_W
        x=(T-T0)/w;sp=np.where(x>30,x,np.log1p(np.exp(np.minimum(x,30))))
        s=1/(1+np.exp(np.clip(x,-60,60)));ds=-s*(1-s)/w;Lf=(1-self.f)*p.sea_ice_latent_j_m2   # d/dT (T - w softplus) = ice share
        return self.C*T+self.dC_ice*(T-w*sp)+Lf*(1-s),self.C+self.dC_ice*s-Lf*ds

    def ice(self,T):
        s=1/(1+np.exp((T-self.p.T_ICE)/self.p.W_ICE));return s,-s*(1-s)/self.p.W_ICE

    def albedo(self,T):
        free=self.albedo_free+self.albedo_extra        # W4: brighter dry land (set by coupled.py)
        s,ds=self.ice(T);a=free+(self.p.albedo_ice-free)*s
        return a,(self.p.albedo_ice-free)*ds

    # ----- residual and Jacobian -------------------------------------------------
    def residual(self,T):
        T=np.asarray(T).reshape(self.ns,self.g.n);p=self.p
        a,_=self.albedo(T);h,_=self.mse(T);H,_=self.heat(T)
        storage=self.rate*(H-np.roll(H,1,axis=0))
        transport=np.array([self.L@h[s] for s in range(self.ns)])
        if self.ocean_heat is not None:transport=transport+np.array([self._ocean(T,s)[0] for s in range(self.ns)])
        if self.atm_heat is not None:transport=transport+np.array([self.atm_heat[s]@h[s] for s in range(self.ns)])
        return storage-self.Q*(1-a)+p.A+p.B*(T-273.15)-transport

    def jacobian(self,T):
        T=np.asarray(T).reshape(self.ns,self.g.n);p=self.p;n=self.g.n
        _,da=self.albedo(T);_,dh=self.mse(T);c=self.storage_rate(T)
        blocks=[[None]*self.ns for _ in range(self.ns)]
        for s in range(self.ns):
            blocks[s][s]=self._local(T,s,c[s],da,dh)
            prev=(s-1)%self.ns
            blocks[s][prev]=diags(-c[prev]) if prev!=s else blocks[s][s]
        return bmat(blocks,format='csc')

    def storage_rate(self,T):
        """c[s] = C(T[s]) n_s / year [W/m2/K] (zero for a single season)."""
        _,C=self.heat(T);return np.array(C)*self.rate if self.ns>1 else np.zeros_like(np.asarray(T,float))

    def season_blocks(self,T):
        """Diagonal blocks D_s of the Jacobian (the block to the previous season is -c[s-1] I)."""
        T=np.asarray(T).reshape(self.ns,self.g.n);_,da=self.albedo(T);_,dh=self.mse(T)
        c=self.storage_rate(T)
        return dict(D=[self._local(T,s,c[s],da,dh) for s in range(self.ns)],c=c)

    def linear_solve(self,blocks,rhs,sigma=0.):
        """Solve J x = rhs for the cyclic seasonal Jacobian (J + sigma I with pseudo-transient sigma).

        J has blocks D_s on the diagonal and -c[s-1] I to the previous season (cyclic).  Each season is
        factorized alone (2562 unknowns, little fill), the seasons are swept in order,
        x_s = (D_s + sigma)^-1 (b_s + c[s-1] x_{s-1}), and only the closing condition x_{-1} = x_{ns-1} is
        solved by GMRES on one season's unknowns: (I - M) y = q with M the linearized year map.
        Exact (to the GMRES tolerance); falls back to one full sparse LU if GMRES stalls.
        """
        from scipy.sparse.linalg import gmres,LinearOperator
        ns,n=self.ns,self.g.n;b=np.asarray(rhs).reshape(ns,n);c=blocks['c']
        I=identity(n,format='csc')
        lus=[splu((D+sigma*I).tocsc() if sigma else D.tocsc()) for D in blocks['D']]
        if ns==1:return lus[0].solve(b[0])[None,:]
        def sweep(y,rhs_on):
            xs=[];x=y
            for s in range(ns):x=lus[s].solve((b[s] if rhs_on else 0.)+c[s-1]*x);xs.append(x)
            return xs
        q=sweep(np.zeros(n),True)[-1]
        A=LinearOperator((n,n),matvec=lambda y:y-sweep(y,False)[-1],dtype=float)
        try:y,info=gmres(A,q,rtol=1e-12,atol=0.,restart=60,maxiter=20)
        except TypeError:y,info=gmres(A,q,tol=1e-12,atol=0.,restart=60,maxiter=20)
        x=np.array(sweep(y,True))
        if info!=0 or not np.all(np.isfinite(x)):                      # fall back to the full system
            J=bmat([[blocks['D'][s] if r==s else (diags(-c[r]) if r==(s-1)%ns else None) for r in range(ns)] for s in range(ns)],format='csc')
            if sigma:J=(J+sigma*identity(ns*n,format='csc')).tocsc()
            x=splu(J).solve(b.ravel()).reshape(ns,n)
        return x

    def _local(self,T,s,c,da,dh):
        J=diags(c+self.p.B+self.Q[s]*da[s])-self.L@diags(dh[s])
        if self.ocean_heat is not None:J=J-self._ocean(T,s)[1]
        if self.atm_heat is not None:J=J-self.atm_heat[s]@diags(dh[s])
        return J.tocsr()

    # ----- solve -----------------------------------------------------------------
    def solve(self,T0,*,tolerance=1e-6,max_iterations=300,log=None,sigma0=30.):
        """Newton with backtracking; when a Newton step makes too little progress (near the
        ice-albedo fold, where the unstable branch attracts Newton) it switches to pseudo-transient
        continuation (J + sigma I) dT = -F.  sigma0 ~ heat capacity / one season [W/m2/K], so the
        iteration follows the physical relaxation towards a stable state; sigma shrinks with the
        residual (switched evolution relaxation, sigma = sigma0 |F| / |F_switch|) back to pure Newton."""
        T=np.array(T0,float).reshape(self.ns,self.g.n).copy();f=self.residual(T);sigma=0.;N=T.size;fref=None
        for it in range(max_iterations):
            err=float(np.max(abs(f)))
            if log is not None:log.append(dict(iteration=it,max_residual_w_m2=err,sigma=sigma))
            if err<=tolerance:return dict(T=T,residual=f,iterations=it,converged=True)
            blocks=self.season_blocks(T);merit=float(np.sum(f*f))
            while True:
                step=self.linear_solve(blocks,-f,sigma);alpha=1.
                if sigma==0:
                    for _ in range(12):
                        trial=T+alpha*step;ft=self.residual(trial)
                        if np.all(np.isfinite(ft)) and float(np.sum(ft*ft))<=(1-1e-4*alpha)*merit:break
                        alpha*=.5
                    good=bool(np.all(np.isfinite(ft)) and float(np.sum(ft*ft))<merit) and alpha>=1/64
                else:
                    # a pseudo time step: physical relaxation may first raise |F| (leaving an unstable state)
                    trial=T+step;ft=self.residual(trial)
                    good=bool(np.all(np.isfinite(ft)) and float(np.sum(ft*ft))<100*merit)
                if good or sigma>1e6:break
                if sigma==0:sigma=sigma0;fref=np.sqrt(merit)
                else:sigma*=4
            if good:T,f=trial,ft
            if sigma>0:
                sigma=min(max(sigma0*np.sqrt(float(np.sum(f*f)))/fref,0.),sigma) if good else sigma
                if sigma<1e-3*sigma0:sigma=0.
        return dict(T=T,residual=f,iterations=max_iterations,converged=bool(np.max(abs(f))<=tolerance))

    # ----- diagnostics -----------------------------------------------------------
    def surface_temperature(self,T):return np.asarray(T)   # T is already at the surface height (g z inside h)

    def energy_budget(self,T):
        """Global, annual means [W/m2]: absorbed, OLR, imbalance."""
        T=np.asarray(T).reshape(self.ns,self.g.n);w=self.g.area/self.g.area.sum()
        a,_=self.albedo(T);asr=float(np.mean((self.Q*(1-a))@w));olr=float(np.mean((self.p.A+self.p.B*(T-273.15))@w))
        return dict(absorbed=asr,olr=olr,imbalance=asr-olr)

    def stability(self,T,count=6):
        """Floquet multipliers of the linearized seasonal cycle (|m| < 1: stable).

        Over one season the linearized implicit step is (c[s] + J_local) dT_s = c[s-1] dT_{s-1};
        the year map is the product over seasons. Largest |m| by power-type iteration (eigs).
        """
        from scipy.sparse.linalg import LinearOperator,eigs
        T=np.asarray(T).reshape(self.ns,self.g.n);p=self.p;c=self.storage_rate(T);n=self.g.n
        _,da=self.albedo(T);_,dh=self.mse(T)
        lus=[splu(self._local(T,s,c[s],da,dh).tocsc()) for s in range(self.ns)]
        def year(v):
            for s,lu in enumerate(lus):v=lu.solve(c[s-1]*v)
            return v
        vals=eigs(LinearOperator((n,n),matvec=year,dtype=float),k=count,which='LM',return_eigenvectors=False)
        return sorted(np.abs(vals),reverse=True)
