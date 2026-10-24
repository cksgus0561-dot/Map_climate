"""Immutable model definition and reproducible input identities."""
from pathlib import Path
import hashlib
import json
import numpy as np

ROOT=Path(__file__).resolve().parents[3]
PROFILE_PATH=ROOT/'data/climate/earth-mean-v1.json'
PROFILE=json.loads(PROFILE_PATH.read_text(encoding='utf-8'))
PROFILE_SHA256=hashlib.sha256(PROFILE_PATH.read_bytes()).hexdigest()
G=PROFILE['planet']['gravity_m_s2']
RD=PROFILE['thermodynamics']['Rd_j_kg_k']
RV=PROFILE['thermodynamics']['Rv_j_kg_k']
T0=PROFILE['thermodynamics']['T0_k']
P0=PROFILE['thermodynamics']['p0_pa']
CPD=PROFILE['thermodynamics']['cp_dry_j_kg_k']
YEAR=PROFILE['planet']['year_days']*86400

def require_finite(**arrays):
    for name,a in arrays.items():
        if not np.isfinite(a).all():
            raise ValueError(f'Nonfinite {name}')

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()
