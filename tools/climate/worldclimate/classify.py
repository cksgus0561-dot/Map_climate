"""W6: Koppen-Geiger climate types and biomes from the four-season climate (display only, no feedback).

Monthly values are estimated from the seasonal means:
  * warmest / coldest month = annual mean + 1.11 x (warmest / coldest season - annual mean)
    (a sinusoid's quarter means reach 0.90 of its amplitude)
  * months above 10 C from that sinusoid
  * summer half-year = the two warmest consecutive seasons; driest / wettest month of a half =
    driest / wettest season / 3 scaled by 0.7 / 1.25 (months inside a season differ)
Rules follow Peel, Finlayson & McMahon (2007): E first, then B, then A, C, D.
"""
import numpy as np

DAY=86400.
KOPPEN_NAMES={'Af':'열대 우림','Am':'열대 몬순','Aw':'열대 사바나','As':'열대 사바나(여름 건기)',
    'BWh':'더운 사막','BWk':'추운 사막','BSh':'더운 스텝','BSk':'추운 스텝',
    'Csa':'지중해성(더운 여름)','Csb':'지중해성(따뜻한 여름)','Csc':'지중해성(서늘한 여름)',
    'Cwa':'온대 겨울 건조(더운 여름)','Cwb':'온대 겨울 건조(따뜻한 여름)','Cwc':'온대 겨울 건조(서늘한 여름)',
    'Cfa':'온난 습윤','Cfb':'서안 해양성','Cfc':'아한대 해양성',
    'Dsa':'냉대 여름 건조(더운 여름)','Dsb':'냉대 여름 건조(따뜻한 여름)','Dsc':'냉대 여름 건조(서늘한 여름)','Dsd':'냉대 여름 건조(극한 겨울)',
    'Dwa':'냉대 겨울 건조(더운 여름)','Dwb':'냉대 겨울 건조(따뜻한 여름)','Dwc':'냉대 겨울 건조(서늘한 여름)','Dwd':'냉대 겨울 건조(극한 겨울)',
    'Dfa':'냉대 습윤(더운 여름)','Dfb':'냉대 습윤(따뜻한 여름)','Dfc':'아한대(타이가)','Dfd':'아한대(극한 겨울)',
    'ET':'툰드라','EF':'빙설'}
KOPPEN_ORDER=list(KOPPEN_NAMES)
BIOMES=['빙원','툰드라','타이가(냉대림)','온대 혼합림','온대 낙엽수림','온대 우림','난온대 상록림','지중해성 관목림',
        '온대 초원(스텝)','건조 관목·사바나','사막','열대 사바나','열대 계절림','열대 우림']


def monthly_estimates(T_c,P_mm_day,lat_deg):
    """T_c [C] and P [mm/day] as (4, n).  Returns dict of the Koppen inputs per cell."""
    T=np.asarray(T_c);P=np.asarray(P_mm_day)*365.2422/4            # mm per season
    ann=T.mean(0);thot=ann+1.11*(T.max(0)-ann);tcold=ann+1.11*(T.min(0)-ann)
    amp=np.maximum((thot-tcold)/2,1e-6);x=np.clip((10-ann)/amp,-1,1);months10=12*np.arccos(x)/np.pi
    ns=T.shape[0];pair=np.array([T[s]+T[(s+1)%ns] for s in range(ns)]);s0=np.argmax(pair,0)
    idx=np.arange(T.shape[1]);summer=[s0,(s0+1)%ns];winter=[(s0+2)%ns,(s0+3)%ns]
    Ps=np.stack([P[summer[0],idx],P[summer[1],idx]]);Pw=np.stack([P[winter[0],idx],P[winter[1],idx]])
    month=lambda v:v/3.
    return dict(mat=ann,thot=thot,tcold=tcold,months10=months10,map=P.sum(0),
                p_summer=Ps.sum(0),p_winter=Pw.sum(0),
                psdry=.7*month(Ps.min(0)),pswet=1.25*month(Ps.max(0)),pwdry=.7*month(Pw.min(0)),pwwet=1.25*month(Pw.max(0)),
                pdry=.7*month(P.min(0)))


def koppen(T_c,P_mm_day,lat_deg,glacier=None):
    m=monthly_estimates(T_c,P_mm_day,lat_deg);n=len(m['mat']);out=np.empty(n,dtype=object)
    for i in range(n):
        mat,th,tc,MAP=m['mat'][i],m['thot'][i],m['tcold'][i],m['map'][i]
        if (glacier is not None and glacier[i]) or th<0:out[i]='EF';continue
        if th<10:out[i]='ET';continue
        ps,pw=m['p_summer'][i],m['p_winter'][i]
        pth=2*mat+(28 if ps>=.7*MAP else 0 if pw>=.7*MAP else 14)
        if MAP<10*pth:
            out[i]=('BW' if MAP<5*pth else 'BS')+('h' if mat>=18 else 'k');continue
        if tc>=18:
            pd=m['pdry'][i]
            if pd>=60:out[i]='Af'
            elif pd>=100-MAP/25:out[i]='Am'
            else:out[i]='As' if m['psdry'][i]<m['pwdry'][i] else 'Aw'
            continue
        if m['psdry'][i]<40 and m['psdry'][i]<m['pwwet'][i]/3:second='s'
        elif m['pwdry'][i]<m['pswet'][i]/10:second='w'
        else:second='f'
        if th>=22:third='a'
        elif m['months10'][i]>=4:third='b'
        elif tc<-38 and tc<=0:third='d'
        else:third='c'
        out[i]=('C' if tc>0 else 'D')+second+third
        if out[i][0]=='C' and third=='d':out[i]=out[i][:2]+'c'
    return out,m


def biome(code,m):
    """Simple biome from the Koppen type plus annual rain [mm]."""
    n=len(code);out=np.empty(n,dtype=object)
    for i,c in enumerate(code):
        MAP=m['map'][i]
        if c=='EF':b='빙원'
        elif c=='ET':b='툰드라'
        elif c.startswith('BW'):b='사막'
        elif c=='BSh':b='건조 관목·사바나'
        elif c=='BSk':b='온대 초원(스텝)'
        elif c in('Af','Am'):b='열대 우림'
        elif c in('Aw','As'):b='열대 계절림' if MAP>1300 else '열대 사바나'
        elif c[:2]=='Cs':b='지중해성 관목림'
        elif c[0]=='C':
            if c[2] in 'bc' and MAP>2000 and c[1]=='f':b='온대 우림'
            elif c[2]=='a':b='난온대 상록림'
            else:b='온대 낙엽수림'
        elif c[0]=='D':b='타이가(냉대림)' if c[2] in 'cd' else '온대 혼합림'
        else:b='툰드라'
        out[i]=b
    return out
