"""W3: wind-driven ocean gyres (Stommel) and their heat transport.

Depth-integrated volume transport U = k x grad(psi) (psi in m3/s) obeys the linear
Stommel vorticity balance, written in flux (area-integrated) form on each cell:

    R lap(psi) + J(psi, f) = curl(tau) / rho0,     J(psi, f) = div(psi * 2 Omega cos(lat)/a * east)

  * R sets the western boundary layer width R/beta; the beta term is upwinded (edge value from
    the eastern cell) so a sub-cell boundary layer stays monotone
  * coasts: psi is constant on every landmass; the largest landmass has psi = 0 and every
    other landmass (island, Antarctica, ...) gets its own unknown constant whose equation is
    the sum of the balance over its cells = Kelvin circulation around it (friction around
    the island balances the wind stress along it).  This lets a circumpolar current exist.
  * edge volume fluxes come from psi at the Voronoi vertices (exactly divergence-free
    between ocean cells), then a small projection removes the divergence left by closing
    coastal edges.

Heat: the gyres carry heat with efficiency eta (only the upper ocean has the surface
temperature contrast):  H_i = -(eta rho c_p / A_i) sum_j F_ij S_upwind,  S = smooth max(T, T_freeze),
a linear operator in S that conserves energy exactly.

Ekman cells: the surface Ekman transport M (f k x M + r_E M = tau/rho0) is closed by a return
flow / upwelling of water at T_d = kappa S + (1 - kappa) T_deep, so it carries (1 - kappa)(S - T_deep).
This is the shallow wind-driven overturning that carries most tropical ocean heat, and its
divergence gives the cold equatorial tongue and the eastern-boundary coastal upwelling.

Eddies: down-gradient mixing rho c_p K_e h_e grad S between ocean cells. Linear gyres never
exchange water across the streamline between subtropical and subpolar gyres; eddies (and in
reality the overturning) do.

Deep-water formation (2026-10-22): the overturning sinks where the surface water is densest, found from
the model's own fields, not chosen by hand.  Surface salinity: the annual-mean balance of the same
upper-ocean transport (gyres, Ekman cells, eddies, overturning) with the freshwater flux E - P - river
inflow (rivers follow the terrain down to the sea) and exchange with the deep ocean,
    (H / rho c_p) s - w_x s = -S_0 (E - P - R) / rho_w,     s = S - S_0;
density from the coldest-season S and that salinity (Roquet et al. 2015 simplified equation of state,
where cold water's small expansion lets salinity decide, e.g. the North Atlantic over the North Pacific).
Sinking is spread over the densest water of the world ocean with weights exp((rho - rho_max) / delta_rho).
"""
from dataclasses import dataclass
import heapq
import numpy as np
from scipy.sparse import coo_matrix,csr_matrix,diags,identity
from scipy.sparse.csgraph import connected_components
from scipy.sparse.linalg import splu
from .winds import EdgeGeometry,gradient_operators,tangent_basis,OMEGA

RHO0=1025.;CP_W=3990.;T_FREEZE=271.35;RHO_FRESH=1000.


def surface_density(T_c,S):
    """Sea-water density anomaly at the surface [kg/m3] (Roquet et al. 2015, simplified equation of state):
    thermal expansion grows with temperature, so near freezing salinity dominates the density."""
    Ta=T_c-10.;Sa=S-35.
    return -.16550*(1+.5*.059520*Ta)*Ta+.76554*(1-.5*.00054914*Sa)*Sa-.0024341*Ta*Sa


@dataclass
class OceanParams:
    friction:float=2.e-6         # R [1/s]; Stommel layer R/beta ~ 100 km (sub-cell, carried by upwinding)
    heat_efficiency:float=.5     # eta
    freeze_width_k:float=1.
    ekman:bool=True
    ekman_friction:float=1.e-5   # r_E [1/s] (= f at ~4 deg): regularizes 1/f near the equator
    upwelling_memory:float=.7    # kappa: upwelled / return water T_d = kappa S + (1 - kappa) T_deep
    deep_temperature_k:float=278.
    eddy_diffusivity:float=1000. # K_e [m2/s] mesoscale eddies
    eddy_depth_m:float=400.      # depth over which they mix the surface temperature contrast
    # deep-water formation (overturning): where the surface water is densest (deepwater_auto), or at
    # deepwater_sites if given
    deepwater_sites:tuple=()     # ((lat, lon), ...) in degrees
    deepwater_auto:bool=False    # off (10-22): on Earth it cooled 50-70N by 1-1.6 K and warmed 50-70S by ~2 K (wrong direction)
    density_width_kg_m3:float=.25 # delta_rho: spread of the sinking over the densest water (seasonal/sub-seasonal and
                                  # year-to-year density scatter); at .1 near-equal seas took turns and the outer loop cycled
    sink_memory:float=.5         # share of the previous outer pass's sinking pattern kept (damps the sinking-site feedback)
    deep_exchange_m_s:float=2.5e-7   # w_x: a 400 m upper ocean renewed from below in ~50 years
    salinity_ref:float=35.
    overturning_sv:float=15.
    deepwater_radius_km:float=800.
    deepwater_return_k:float=276.
    upwelling_min_lat:float=35.  # return flow surfaces in the other hemisphere's ocean poleward of this


def soft_floor(T,floor=T_FREEZE,width=1.):
    x=(T-floor)/width;sp=np.where(x>30,x,np.log1p(np.exp(np.minimum(x,30))))
    return floor+width*sp,1/(1+np.exp(-x))


class Ocean:
    def __init__(self,grid,land_fraction,params=None,geometry=None):
        self.g=g=grid;self.p=p=params or OceanParams();self.f=np.asarray(land_fraction,float)
        self.geo=geo=geometry or EdgeGeometry(grid);self.ocean=self.f<.5;n=g.n;i,j=g.left,g.right
        # landmasses
        land=~self.ocean;both=land[i]&land[j]
        A=coo_matrix((np.ones(both.sum()),(i[both],j[both])),shape=(n,n))
        nc,lab=connected_components(A,directed=False);lab=np.where(land,lab,-1)
        comps=[c for c in np.unique(lab[land])] if land.any() else []
        sizes={c:float(g.area[lab==c].sum()) for c in comps}
        self.gauge=max(sizes,key=sizes.get) if comps else None
        self.islands=[c for c in comps if c!=self.gauge];self.landmass=lab
        # unknown index for every cell (-1: fixed psi = 0)
        idx=np.full(n,-1);oc=np.flatnonzero(self.ocean)
        if self.gauge is None:oc=oc[1:]                            # aquaplanet: pin one cell
        idx[oc]=np.arange(len(oc));k=len(oc)
        for c in self.islands:idx[lab==c]=k;k+=1
        self.index=idx;self.nu=k
        keep=idx>=0
        self.P=csr_matrix((np.ones(keep.sum()),(idx[keep],np.flatnonzero(keep))),shape=(k,n))   # aggregation
        # area-integrated Stommel operator on all cells
        kij=p.friction*g.edge_length/g.distance
        mid=geo.mid;east=np.stack([-mid[:,1],mid[:,0],0*mid[:,0]],-1);east/=np.linalg.norm(east,axis=-1)[:,None]
        coslat=np.sqrt(np.maximum(1-mid[:,2]**2,0))
        cn=2*OMEGA*coslat/g.radius*np.sum(east*geo.n_mid,-1)*g.edge_length
        # beta term upwinded along the westward Rossby characteristic (edge value from the eastern
        # cell), so the boundary layer may be thinner than a cell without oscillations
        up=np.where(cn<0,i,j)
        rows=np.r_[i,i,j,j,i,j];cols=np.r_[j,i,i,j,up,up]
        vals=np.r_[kij,-kij,kij,-kij,cn,-cn]
        M=coo_matrix((vals,(rows,cols)),shape=(n,n)).tocsr()
        self.M=(self.P@M@self.P.T).tocsc();self.lu=splu(self.M)
        self.t_edge=np.cross(geo.mid,geo.n_mid)                     # counter-clockwise around the left cell
        # ocean-only edges, projection Laplacian (graph), gradient for display
        self.oe=self.ocean[i]&self.ocean[j];e=self.oe
        Lg=coo_matrix((np.r_[np.ones(e.sum()),np.ones(e.sum()),-np.ones(e.sum()),-np.ones(e.sum())],
                       (np.r_[i[e],j[e],i[e],j[e]],np.r_[j[e],i[e],i[e],j[e]])),shape=(n,n)).tocsr()
        self.Lg_lu=splu((Lg-1e-9*identity(n)).tocsc())
        # mesoscale eddy mixing of upper-ocean heat across ocean edges (also mixes between gyres)
        ke=RHO0*CP_W*p.eddy_diffusivity*p.eddy_depth_m*g.edge_length[e]/g.distance[e]
        Ld=coo_matrix((np.r_[ke,ke,-ke,-ke],(np.r_[i[e],j[e],i[e],j[e]],np.r_[j[e],i[e],i[e],j[e]])),shape=(n,n)).tocsr()
        self.eddy=Ld.multiply(1/g.area[:,None]).tocsr()
        self.moc=self.overturning(p.deepwater_sites) if p.deepwater_sites else None
        self.vertex_cells=geo.vertex_cells
        self.vc=np.array([c[:3] for c in geo.vertex_cells])           # three cells per vertex
        self.grad=gradient_operators(g,geo);self.east,self.north=tangent_basis(g.points)

    def streamfunction(self,stress3):
        """Solve the Stommel balance for one season's wind stress (n, 3) -> psi on cells [m3/s]."""
        g=self.g;i,j=g.left,g.right
        te=.5*(stress3[i]+stress3[j]);circ=np.sum(te*self.t_edge,-1)*g.edge_length/RHO0
        b=np.bincount(i,circ,minlength=g.n)-np.bincount(j,circ,minlength=g.n)
        psi_u=self.lu.solve(self.P@b)
        return self.P.T@psi_u

    def edge_flux(self,psi):
        """Volume flux left -> right on every edge [m3/s]; zero across coasts; divergence-free in the ocean."""
        pv=psi[self.vc].mean(1);F=pv[self.geo.va]-pv[self.geo.vb];F=np.where(self.oe,F,0.)
        g=self.g;n=g.n
        d=np.bincount(g.left,F,minlength=n)-np.bincount(g.right,F,minlength=n)
        chi=self.Lg_lu.solve(-d)
        F=F+np.where(self.oe,chi[g.right]-chi[g.left],0.)
        return F

    def heat_operator(self,F,efficiency=None):
        """Sparse H with  heat convergence [W/m2] = H @ S(T)  (upwind, conservative)."""
        g=self.g;i,j=g.left,g.right;n=g.n;c=(self.p.heat_efficiency if efficiency is None else efficiency)*RHO0*CP_W
        up=np.where(F>=0,i,j)
        rows=np.r_[i,j];cols=np.r_[up,up];vals=np.r_[-c*F,c*F]
        return (coo_matrix((vals,(rows,cols)),shape=(n,n)).tocsr()).multiply(1/g.area[:,None]).tocsr()

    def ekman_flux(self,stress3):
        """Surface Ekman volume flux across ocean edges [m3/s]:  f k x M + r_E M = tau / rho0.

        Divergent on purpose: divergence = upwelling (equator, eastern-boundary coasts),
        convergence = downwelling (subtropical gyres).
        """
        g=self.g;i,j=g.left,g.right;geo=self.geo
        te=.5*(stress3[i]+stress3[j]);mid=geo.mid
        east=np.stack([-mid[:,1],mid[:,0],0*mid[:,0]],-1);east/=np.maximum(np.linalg.norm(east,axis=-1),1e-12)[:,None]
        north=np.cross(mid,east);f=2*OMEGA*mid[:,2]
        tz=(np.sum(te*east,-1)+1j*np.sum(te*north,-1))/RHO0
        # only the part to the right (NH) of the stress: tz/(r + i f) = tz (r - i f)/(r^2 + f^2); the down-wind
        # part near the equator is returned in the same layer, so it carries no heat
        M=tz*(-1j*f)/(self.p.ekman_friction**2+f*f)
        Mv=M.real[:,None]*east+M.imag[:,None]*north
        F=np.sum(Mv*geo.n_mid,-1)*g.edge_length
        return np.where(self.oe,F,0.)

    def ekman_operator(self,stress3):
        """(H, b): Ekman-cell heat convergence = H @ S + b  with  flux of (1 - kappa)(S - T_deep)."""
        p=self.p;F=self.ekman_flux(stress3);H=self.heat_operator(F,efficiency=1-p.upwelling_memory)
        return H,-p.deep_temperature_k*(H@np.ones(self.g.n)),F

    def season_operator(self,psi,stress3):
        """Total ocean heat convergence for one season: (H, b) with conv = H @ S + b."""
        H=self.heat_operator(self.edge_flux(psi))+self.eddy;b=np.zeros(self.g.n)
        if self.moc is not None:H=H+self.moc['H'];b=b+self.moc['b']
        if not self.p.ekman:return H.tocsr(),b
        He,be,_=self.ekman_operator(stress3);return (H+He).tocsr(),b+be

    # ---- deep-water formation / overturning --------------------------------------------------------
    def ocean_components(self):
        g=self.g;e=self.oe
        A=coo_matrix((np.ones(e.sum()),(g.left[e],g.right[e])),shape=(g.n,g.n))
        _,lab=connected_components(A,directed=False);return np.where(self.ocean,lab,-1)

    def overturning(self,sites):
        """Overturning sinking within deepwater_radius_km of each named site (area weighted)."""
        g=self.g;p=self.p;comp=self.ocean_components()
        sink=np.zeros(g.n)
        for la,lo in sites:
            c=np.radians([la,lo]);x=np.array([np.cos(c[0])*np.cos(c[1]),np.cos(c[0])*np.sin(c[1]),np.sin(c[0])])
            d=np.arccos(np.clip(g.points@x,-1,1))*g.radius/1e3;near=self.ocean&(d<p.deepwater_radius_km)
            if not near.any():near=self.ocean&(d<=d[self.ocean].min()+1)
            sink+=near*g.area
        if not sink.any():return None
        c0=np.bincount(comp[sink>0]).argmax();return self.overturning_from(sink*(comp==c0),comp,c0)

    def overturning_from(self,sink,comp,c0):
        """Upper branch of a thermohaline overturning as a surface potential flow.

        sink: sinking weights on the ocean cells of connected ocean c0, total overturning_sv.
        Upwelling: for the water sinking in each hemisphere, the ocean of the other hemisphere poleward of
        upwelling_min_lat in the same connected ocean (else that ocean's cells away from the sinks).  Surface
        water flows from upwelling to sinking along the potential flow F = w grad(phi) on ocean edges
        (w = edge length / distance); it carries S - T_return, so the sinking region receives heat and the
        upwelling region loses it.
        """
        g=self.g;p=self.p;lat=np.degrees(g.lat);i,j=g.left,g.right;e=self.oe
        ws=sink/sink.sum();d=-ws
        for north in (True,False):
            share=ws[(lat>=0)==north].sum()
            if share<=0:continue
            src=self.ocean&(comp==c0)&((lat<-p.upwelling_min_lat) if north else (lat>p.upwelling_min_lat))
            if not src.any():src=self.ocean&(comp==c0)&(ws<1e-3*ws.max())
            d=d+share*src*g.area/(src*g.area).sum()
        q=p.overturning_sv*1e6;d=q*d                                       # outflow per cell [m3/s]
        w=g.edge_length[e]/g.distance[e];n=g.n
        Lw=coo_matrix((np.r_[w,w,-w,-w],(np.r_[i[e],j[e],i[e],j[e]],np.r_[j[e],i[e],i[e],j[e]])),shape=(n,n)).tocsr()
        phi=splu((Lw-1e-9*identity(n)).tocsc()).solve(-d)
        F=np.zeros(g.ne);F[e]=w*(phi[i[e]]-phi[j[e]])
        H=self.heat_operator(F,efficiency=1.)
        return dict(H=H,b=-p.deepwater_return_k*(H@np.ones(n)),F=F,sink=ws>.01*ws.max(),weights=ws)

    def river_outlets(self,elevation_m):
        """Ocean cell each land cell's runoff reaches: priority flood from the coast over the cell graph,
        so water runs down the terrain (and out of any closed hollow over its lowest rim) to the sea."""
        g=self.g;n=g.n;z=np.asarray(elevation_m,float);out=np.where(self.ocean,np.arange(n),-1)
        nb=[[] for _ in range(n)]
        for a,b in zip(g.left,g.right):nb[a].append(b);nb[b].append(a)
        heap=[(0.,int(c)) for c in np.flatnonzero(self.ocean)];heapq.heapify(heap);seen=self.ocean.copy()
        while heap:
            h,c=heapq.heappop(heap)
            for k in nb[c]:
                if seen[k]:continue
                seen[k]=True;out[k]=out[c];heapq.heappush(heap,(max(h,z[k]),k))
        return out

    def salinity(self,state,water,outlets):
        """Annual-mean surface salinity anomaly s = S - S_0 [g/kg] on ocean cells (module docstring)."""
        g=self.g;p=self.p;n=g.n;oc=self.ocean;land=~oc&(outlets>=0)
        H=sum(self.season_operator(s['psi'],s['stress3'])[0] for s in state)/len(state)/(RHO0*CP_W)
        R=water['runoff'].mean(0)*g.area/RHO_FRESH                    # m3/s from each land cell
        F=(water['E']-water['P']).mean(0)/RHO_FRESH-np.bincount(outlets[land],R[land],minlength=n)/g.area
        F=np.where(oc,F,0.);F=F-oc*(F@g.area)/g.area[oc].sum()        # the global freshwater imbalance makes no salinity contrast
        k=np.flatnonzero(oc);A=(H-p.deep_exchange_m_s*identity(n)).tocsr()[k][:,k]
        s=np.zeros(n);s[k]=splu(A.tocsc()).solve(-p.salinity_ref*F[k]);return s

    def auto_overturning(self,T,state,water,outlets):
        """Overturning sinking where the coldest-season surface water of the world ocean is densest."""
        g=self.g;p=self.p;s=self.salinity(state,water,outlets)
        Tc=np.array([soft_floor(t)[0] for t in np.asarray(T)])-273.15
        rho=surface_density(Tc,p.salinity_ref+s).max(0)
        comp=self.ocean_components();c0=np.bincount(comp[self.ocean],weights=g.area[self.ocean]).argmax();m=comp==c0
        sink=np.where(m,g.area*np.exp((rho-rho[m].max())/p.density_width_kg_m3),0.);sink/=sink.sum()
        # the sinking moves the heat and salt that decide where it sinks: blend with the last pass's pattern
        prev=self.moc.get('weights') if self.moc is not None else None
        if prev is not None and len(prev)==len(sink):sink=p.sink_memory*prev+(1-p.sink_memory)*sink
        out=self.overturning_from(sink,comp,c0);out.update(salinity=p.salinity_ref+s,density=rho);return out

    def sinking_sites(self):
        """Where the overturning sinks: one area-weighted centre per hemisphere with its share."""
        if self.moc is None or 'weights' not in self.moc:return []
        g=self.g;ws=self.moc['weights'];lat=np.degrees(g.lat);lon=g.lon;out=[]
        for north in (True,False):
            w=ws*((lat>=0)==north)
            if w.sum()<.05:continue
            out.append(dict(lat=round(float(np.average(lat,weights=w)),1),
                            lon=round(float(np.degrees(np.angle(np.average(np.exp(1j*lon),weights=w)))),1),share=round(float(w.sum()),2)))
        return out

    def suggest_deepwater_sites(self,min_lat=50.,count=3):
        """Candidate seas for deep-water formation (for the user to choose from; nothing is chosen here):
        connected high-latitude open-ocean regions, largest first, excluding circumpolar belts."""
        g=self.g;lat=np.degrees(g.lat);lon=np.degrees(g.lon);hi=self.ocean&(abs(lat)>=min_lat)&(abs(lat)<=78)
        i,j=g.left,g.right;e=hi[i]&hi[j]
        A=coo_matrix((np.ones(e.sum()),(i[e],j[e])),shape=(g.n,g.n));_,lab=connected_components(A,directed=False)
        out=[]
        for c in np.unique(lab[hi]):
            m=hi&(lab==c);ring=len(np.unique(np.round(lon[m]/10)))>=34                 # circumpolar ring
            if ring:                                            # point at the widest 60-degree sector of the ring
                sec=np.round(lon[m]/60).astype(int)%6;k=np.bincount(sec,weights=g.area[m],minlength=6).argmax();mm=m.copy();mm[m]=sec==k;m_pos=mm
            else:m_pos=m
            out.append(dict(lat=round(float(np.average(lat[m_pos],weights=g.area[m_pos])),1),
                            lon=round(float(np.degrees(np.angle(np.average(np.exp(1j*np.radians(lon[m_pos])),weights=g.area[m_pos])))),1),
                            area_mkm2=round(float(g.area[m].sum()/1e12),2),circumpolar=bool(ring)))
        # open-ended seas first (deep water forms more readily where currents can converge), then by area
        return sorted(out,key=lambda x:(x['circumpolar'],-x['area_mkm2']))[:count]

    def transport_vectors(self,psi):
        """Depth-integrated transport per cell [m2/s] (east, north) = k x grad psi."""
        gp=np.stack([op@psi for op in self.grad],-1);ge=np.sum(gp*self.east,-1);gn=np.sum(gp*self.north,-1)
        u,v=-gn,ge
        return np.where(self.ocean,u,0.),np.where(self.ocean,v,0.)

    def meridional_heat_transport(self,H,S,lat_edges_deg=np.arange(-80,81,5),b=None):
        """Northward ocean heat transport [PW] across latitude circles, from the convergence field."""
        conv=(H@S+(0 if b is None else b))*self.g.area;lat=np.degrees(self.g.lat)
        return [float(conv[lat>=L].sum()/1e15) for L in lat_edges_deg]   # heat entering the cap north of L
