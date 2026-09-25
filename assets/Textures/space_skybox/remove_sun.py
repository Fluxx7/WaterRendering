from PIL import Image
import numpy as np
from scipy import ndimage
import sys
SP=sys.argv[1]
a=np.asarray(Image.open('right.png').convert('RGB'),dtype=np.float64)
L=a.mean(2)
# The sun is the one large saturated component; stars are a few pixels each.
lab,n=ndimage.label(L>=250); sizes=ndimage.sum(np.ones_like(L),lab,range(1,n+1))
sun=lab==(np.argmax(sizes)+1); cy,cx=ndimage.center_of_mass(sun)
yy,xx=np.mgrid[0:1024,0:1024]; r=np.hypot(xx-cx,yy-cy)

# Fill region: every pixel with a clipped channel connected to the core, plus a margin.
lab2,_=ndimage.label(a.max(2)>=254); ids=np.unique(lab2[sun]); ids=ids[ids>0]
R_FILL=int(np.ceil(r[np.isin(lab2,ids)].max()))+14

# 1. Additive halo, per channel: 5th percentile per ring above a far baseline ring. Fine bins,
#    and smoothing only well clear of the core, where the profile is steep enough that
#    smoothing would smear the disc's brightness outward and over-subtract.
edges=np.arange(0,441,2); rc=(edges[:-1]+edges[1:])/2; prof=np.zeros((len(rc),3))
for i in range(len(rc)):
    m=(r>=edges[i])&(r<edges[i+1]); prof[i]=np.percentile(a[m],5,axis=0)
base=prof[(rc>=400)&(rc<420)].mean(0)
raw=np.clip(prof-base,0,None)
smooth=ndimage.uniform_filter1d(raw,9,axis=0)
w=np.clip((rc-(R_FILL+10))/20,0,1)[:,None]            # 0 near the core, 1 beyond it
glow=raw*(1-w)+smooth*w
glow*=(np.clip((420-rc)/40,0,1)**2)[:,None]          # taper to zero well inside the face
g=np.stack([np.interp(r,rc,glow[:,c],right=0) for c in range(3)],-1)
clean=np.clip(a-g,0,255)

# 2. Harmonic fill of the clipped disc from its boundary, then graft real stars in.
mask=r<=R_FILL
pad=R_FILL+6; y0,y1=int(cy)-pad,int(cy)+pad; x0,x1=int(cx)-pad,int(cx)+pad
win=clean[y0:y1,x0:x1].copy(); mw=mask[y0:y1,x0:x1]
rw=r[y0:y1,x0:x1]; win[mw]=win[(rw>R_FILL)&(rw<=R_FILL+4)].mean(0)
for _ in range(6000):
    avg=(np.roll(win,1,0)+np.roll(win,-1,0)+np.roll(win,1,1)+np.roll(win,-1,1))/4
    win[mw]=avg[mw]
H,W=win.shape[:2]; py,px=1024-H-60,1024-W-60
patch=clean[py:py+H,px:px+W]; stars=np.clip(patch-ndimage.gaussian_filter(patch,(3,3,0)),0,None)
win=win+stars*np.clip((R_FILL-rw)/6,0,1)[...,None]
clean[y0:y1,x0:x1]=np.where(mw[...,None],win,clean[y0:y1,x0:x1])

out=np.clip(np.round(clean),0,255).astype(np.uint8)
Image.fromarray(out).save('right_nosun.png',optimize=True)
src=a.astype(np.uint8)
border=all(np.array_equal(out[s],src[s]) for s in [np.s_[:,:40],np.s_[:,-40:],np.s_[:40],np.s_[-40:]])
print(f'sun centre ({cx:.1f},{cy:.1f})  fill radius {R_FILL}px  face border untouched: {border}')
for r0 in range(24,120,8):
    m=(r>=r0)&(r<r0+8); print(f'  r {r0:3}-{r0+8:3}: median RGB {np.median(out[m],0)}')
cmp=Image.new('RGB',(1024,512))
cmp.paste(Image.fromarray(src).resize((512,512)),(0,0)); cmp.paste(Image.fromarray(out).resize((512,512)),(512,0))
cmp.save(f'{SP}/sun_removed.png')
Image.fromarray(out[y0-60:y1+60,x0-60:x1+60]).resize((512,512),Image.NEAREST).save(f'{SP}/sun_removed_zoom.png')
