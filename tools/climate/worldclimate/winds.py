"""W3: diagnosed sea-level pressure and surface winds for each season.

Sea-level pressure anomaly (hPa) = zonal belt pattern + thermal term:

  * belts: equatorial trough, subtropical highs, subpolar lows, polar highs, as smooth
    Gaussians in a latitude coordinate that follows the season's thermal equator
    (the ITCZ belt moves into the summer hemisphere)
  * thermal term: -beta (theta - zonal mean theta), theta = T + lapse * z
    (warm land in summer -> heat low / monsoon trough; cold land in winter -> high;
    warm winter ocean -> Icelandic / Aleutian lows)
  * one implicit smoothing pass (length ~ 1 cell) removes coastline steps

Surface wind from the friction-geostrophic balance  f k x u + r u = -grad(p) / rho,
solved per cell in complex form u + i v = -(grad p / rho) / (r + i f): geostrophic in
mid-latitudes, down-gradient near the equator, cross-isobar angle atan(r/f) with
larger friction over land.  Everything is a smooth function of T.
"""
from dataclasses import dataclass
import numpy as np
from scipy.sparse import coo_matrix,identity,diags
from scipy.sparse.linalg import splu

OMEGA=7.2921e-5;RHO_AIR=1.2


@dataclass
class WindParams:
    trough_hpa:float=8.      # equatorial trough depth
    subtropical_hpa:float=13.# subtropical high
    subpolar_hpa:float=16.   # subpolar low
    storm_land_share:float=.4# subpolar-low depth over wide land relative to open ocean
    subtropical_summer_land:float=.25  # subtropical-high strength over wide land in the summer hemisphere
                                       # (replaced by heat lows: highs sit over the oceans, moist flow up east coasts)
    polar_hpa:float=6.       # polar high
    belt_width_deg:float=11.
    itcz_follow:float=1.     # thermal-equator shift applied to the belt coordinate
    itcz_max_deg:float=22.
    itcz_lon_sigma_deg:float=30.   # the thermal equator is found per longitude sector (monsoons)
    itcz_lat_limit_deg:float=35.
    itcz_softness_k:float=2.
    itcz_land_weight:float=.3
    beta_land_hpa_k:float=1.3# thermal pressure response
    beta_ocean_hpa_k:float=.6
    lapse_k_m:float=6.5e-3
    thermal_plateau_share:float=1.   # share of the height term lapse * z in the heat-low theta (thermal pressure term only);
                                     # 1 = a plateau heats like lowland as warm as its sea-level-equivalent temperature
    heat_low_dry_k:float=0.          # dry land heats its boundary layer (sensible heat): theta + this * land * (1 - soil beta) in the
                                     # thermal term (coupled.circulation passes the dryness of the last moisture solve); 0 = off
    zonal_sigma_deg:float=6.
    smooth_m:float=4.0e5
    r_ocean:float=4.e-5      # friction [1/s]
    r_land:float=1.2e-4
    drag:float=1.3e-3
    transient_m_s:float=5.   # synoptic wind variability in the bulk stress  <|u| u> ~ sqrt(|U|^2 + u'^2) U
    advection_mass_fraction:float=.2   # share of the column moved by the rotational surface wind (heat transport);
                                       # .3 up to W8 damped the land seasonal cycle (Earth land range 11.1 K, CMIP6 16.1; .1: 13.3)
                                       # W16 .1 -> .2: with the later sea-ice / inversion changes .1 left continental interiors
                                       # 8.5 K too cold in winter (CMIP6, 50-65 deg, >1500 km inland) and west coasts 3.4 K;
                                       # .2: -2.6 / -2.2 K, land seasonal range 16.5 -> 15.0 K (CMIP6 16.1)


def tangent_basis(points):
    lat=np.arcsin(np.clip(points[:,2],-1,1));lon=np.arctan2(points[:,1],points[:,0])
    east=np.stack([-np.sin(lon),np.cos(lon),0*lon],-1)
    north=np.stack([-np.sin(lat)*np.cos(lon),-np.sin(lat)*np.sin(lon),np.cos(lat)],-1)
    return east,north


class EdgeGeometry:
    """Per-edge tangent normals at each end and at the midpoint, plus the Voronoi vertices of every edge."""
    def __init__(self,grid):
        P=grid.points;i,j=grid.left,grid.right
        def tangent(at,to):
            d=to-np.sum(to*at,-1)[:,None]*at;return d/np.linalg.norm(d,axis=-1)[:,None]
        self.n_i=tangent(P[i],P[j]);self.n_j=tangent(P[j],P[i])           # at i towards j, at j towards i
        m=P[i]+P[j];self.mid=m/np.linalg.norm(m,axis=-1)[:,None];self.n_mid=tangent(self.mid,P[j])
        sv=grid.voronoi;regions=[set(r) for r in sv.regions];va=[];vb=[]
        for a,b in zip(i,j):
            s=sorted(regions[a]&regions[b]);va.append(s[0]);vb.append(s[1])
        va=np.array(va);vb=np.array(vb)
        # orient so that the volume flux i -> j equals psi(va) - psi(vb): (vb - va) . (r x n) > 0
        t=np.cross(self.mid,self.n_mid);flip=np.sum((sv.vertices[vb]-sv.vertices[va])*t,-1)<0
        self.va=np.where(flip,vb,va);self.vb=np.where(flip,va,vb)
        nv=len(sv.vertices);cells=[[] for _ in range(nv)]
        for c,r in enumerate(sv.regions):
            for v in r:cells[v].append(c)
        self.vertex_cells=cells                                            # cells sharing each Voronoi vertex


def gradient_operators(grid,geo):
    """Green-Gauss gradient: three sparse n x n matrices (x, y, z components of a tangent 3-vector)."""
    i,j=grid.left,grid.right;n=grid.n;w=grid.edge_length/2
    ops=[]
    for c in range(3):
        a=w*geo.n_i[:,c];b=w*geo.n_j[:,c]
        rows=np.r_[i,i,j,j];cols=np.r_[j,i,i,j];vals=np.r_[a,-a,b,-b]
        ops.append((coo_matrix((vals,(rows,cols)),shape=(n,n)).tocsr()).multiply(1/grid.area[:,None]).tocsr())
    return ops


class CoordinateKernel:
    """y_i = sum_j k(x_i - x_j) v_j for a kernel of one coordinate (latitude or longitude) only.

    Cells sharing a coordinate value (to 1e-9 deg) give the same row and their columns add up, so the
    kernel is built on the distinct values only (level 5: 2,589 latitudes / 4,800 longitudes instead
    of 10,242 cells; same numbers as the dense n x n matrix).  With `weight`, `@` is the normalized
    weighted mean  sum_j k w_j v_j / sum_j k w_j  (the old row-normalized dense matrix)."""
    def __init__(self,x,kernel,weight=None):
        u,inv=np.unique(np.round(np.asarray(x,float),9),return_inverse=True);n=len(x)
        self.inv=inv;self.agg=coo_matrix((np.ones(n),(inv,np.arange(n))),shape=(len(u),n)).tocsr()
        self.K=kernel(u[:,None]-u[None,:]);self.weight=weight
        if weight is not None:self.norm=self.apply(weight)

    def apply(self,v):
        return (self.K@(self.agg@v))[self.inv]

    def __matmul__(self,v):
        return self.apply(self.weight*v)/self.norm


class Atmosphere:
    def __init__(self,grid,land_fraction,elevation_m,params=None):
        self.g=grid;self.p=p=params or WindParams();self.f=np.clip(np.asarray(land_fraction,float),0,1)
        self.z=np.maximum(np.asarray(elevation_m,float),0)*(self.f>0)
        self.geo=EdgeGeometry(grid);self.grad=gradient_operators(grid,self.geo)
        self.east,self.north=tangent_basis(grid.points)
        self.cor=2*OMEGA*np.sin(grid.lat)
        self.r=p.r_ocean+(p.r_land-p.r_ocean)*self.f
        lat=np.degrees(grid.lat);self.latd=lat
        self.zonal=CoordinateKernel(lat,lambda d:np.exp(-.5*(d/p.zonal_sigma_deg)**2),grid.area)   # latitude-band mean
        self.smoother=splu((identity(grid.n)-p.smooth_m**2*grid.laplacian()).tocsc())
        self.beta=p.beta_ocean_hpa_k+(p.beta_land_hpa_k-p.beta_ocean_hpa_k)*self.f
        # subpolar lows are oceanic storm-track lows: weaker over large land (smoothed ocean share)
        o=1-self.f
        for _ in range(3):o=self.smoother.solve(o)
        self.storm=p.storm_land_share+(1-p.storm_land_share)*np.clip(o,0,1);self.ocean_share=np.clip(o,0,1)
        # the ITCZ follows the warmest *sea* surface; land counts with weight itcz_land_weight
        self.sector=CoordinateKernel(np.degrees(grid.lon),lambda d:np.exp(-.5*(((d+180)%360-180)/p.itcz_lon_sigma_deg)**2))
        self.sector_weight=grid.area*np.exp(-(lat/p.itcz_lat_limit_deg)**8)*(1-(1-p.itcz_land_weight)*self.f)
        # rotational-wind heat transport: projection onto divergence-free edge fluxes (all cells)
        i,j=grid.left,grid.right;n=grid.n
        Lg=coo_matrix((np.r_[np.ones(grid.ne),np.ones(grid.ne),-np.ones(grid.ne),-np.ones(grid.ne)],
                       (np.r_[i,j,i,j],np.r_[j,i,i,j])),shape=(n,n)).tocsr()
        self.Lg_lu=splu((Lg-1e-9*identity(n)).tocsc())
        ps=1e5*np.exp(-self.z/8000.);self.edge_mass=p.advection_mass_fraction*.5*(ps[i]+ps[j])/9.80665

    def thermal_equator(self,T):
        """Per cell: smooth latitude of the warmest place in its longitude sector (deg), soft-clipped.

        theta = T + lapse z, so heated plateaus count as warm; the soft arg-max (softness ~1.5 K)
        keeps the result smooth in T.
        """
        p=self.p;th=T+p.lapse_k_m*self.z;e=np.exp((th-th.max())/p.itcz_softness_k)
        w=self.sector_weight*e;phi=self.sector.apply(w*self.latd)/self.sector.apply(w);m=p.itcz_max_deg
        return m*np.tanh(phi/m)*p.itcz_follow

    def belt_latitude(self,phi_e):
        """Map latitude so the thermal equator sits at 0 and the poles stay at +-90."""
        lat=self.latd
        return np.where(lat>=phi_e,(lat-phi_e)/(90-phi_e)*90,(lat-phi_e)/(90+phi_e)*90)

    def pressure(self,T,thermal_term=True,dry=None):
        """Sea-level pressure anomaly [hPa] for one season's temperature field (n,); thermal_term=False: belts only.
        dry (n,): land share without soil water (land * (1 - beta)), deepening heat lows over dry ground."""
        p=self.p;phi_e=self.thermal_equator(T);psi=self.belt_latitude(phi_e)
        G=lambda c:np.exp(-.5*((psi-c)/p.belt_width_deg)**2)
        summer=.5*(1+np.tanh(np.sign(self.latd)*phi_e/3.))          # 1 in the summer hemisphere
        sub=p.subtropical_hpa*(1-(1-p.subtropical_summer_land)*(1-self.ocean_share)*summer)
        belts=(-p.trough_hpa*G(0)+sub*(G(30)+G(-30))-p.subpolar_hpa*self.storm*(G(60)+G(-60))
               +p.polar_hpa*(G(90)+G(-90)))
        theta=T+p.thermal_plateau_share*p.lapse_k_m*self.z
        if dry is not None and p.heat_low_dry_k:theta=theta+p.heat_low_dry_k*dry
        thermal=-self.beta*(theta-self.zonal@theta)
        raw=belts+thermal*thermal_term
        return self.smoother.solve(raw)

    def wind(self,slp_hpa):
        """Surface wind (3-vector per cell, m/s) and (east, north) components."""
        gp=np.stack([op@(slp_hpa*100.) for op in self.grad],-1)/RHO_AIR      # grad p / rho  (tangent 3-vector)
        ge=np.sum(gp*self.east,-1);gn=np.sum(gp*self.north,-1)
        U=-(ge+1j*gn)/(self.r+1j*self.cor)
        u,v=U.real,U.imag
        return u[:,None]*self.east+v[:,None]*self.north,u,v

    def stress(self,wind3):
        """Surface wind stress [N/m2] (3-vector), bulk formula with synoptic variability."""
        s=np.sqrt(np.sum(wind3**2,-1)+self.p.transient_m_s**2)
        return RHO_AIR*self.p.drag*s[:,None]*wind3

    def rotational(self,F):
        """Divergence-free part of edge fluxes F (left -> right)."""
        g=self.g;i,j=g.left,g.right
        d=np.bincount(i,F,minlength=g.n)-np.bincount(j,F,minlength=g.n);chi=self.Lg_lu.solve(-d);return F+chi[j]-chi[i]

    def mass_flux(self,wind3):
        """Divergence-free part of the low-level mass flux across every edge [kg/s] (left -> right)."""
        g=self.g;i,j=g.left,g.right;ue=.5*(wind3[i]+wind3[j])
        F=self.edge_mass*np.sum(ue*self.geo.n_mid,-1)*g.edge_length
        d=np.bincount(i,F,minlength=g.n)-np.bincount(j,F,minlength=g.n)
        chi=self.Lg_lu.solve(-d);return F+chi[j]-chi[i]

    def heat_operator(self,F):
        """Sparse A with  convergence [W/m2] = A @ h  for moist static energy h [J/kg].

        Centred edge values (no numerical diffusion; the MSE diffusion already keeps the cell
        Peclet number below 2) and conservative (antisymmetric edge fluxes).
        """
        g=self.g;i,j=g.left,g.right;n=g.n;F2=F/2
        A=coo_matrix((np.r_[-F2,-F2,F2,F2],(np.r_[i,i,j,j],np.r_[i,j,i,j])),shape=(n,n)).tocsr()
        return A.multiply(1/g.area[:,None]).tocsr()

    def seasons(self,T,dry=None):
        """Pressure, winds and stress for every season of T (ns, n); dry (ns, n): see pressure."""
        out=[]
        for s,Ts in enumerate(np.asarray(T)):
            slp=self.pressure(Ts,dry=None if dry is None else dry[s]);w3,u,v=self.wind(slp);phi=self.thermal_equator(Ts)
            out.append(dict(slp=slp,wind3=w3,u=u,v=v,stress3=self.stress(w3),itcz=float(np.average(phi,weights=self.g.area)),itcz_cell=phi,
                            wind3_heat=self.heat_wind(Ts,w3)))
        return out

    def heat_wind(self,T,wind3):
        """The part of the surface wind driven by the thermal (heat-low / cold-high) pressure term."""
        return wind3-self.wind(self.pressure(T,thermal_term=False))[0]
