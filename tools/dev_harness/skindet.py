import json, os, sys, numpy as np
sys.path.insert(0,"/home/claude/cod2eft")
import bpy, cod2eft_porter as C, cod2eft_textures as TX
from PIL import Image, ImageDraw
T="/mnt/user-data/uploads/SPTModdingTools/Testing"
d=json.load(open("codhist.json"))
def skin_share(rgb):
    # Kovac et al. 2003 uniform-daylight rule, on 8-bit sRGB
    R,G,B=[rgb[...,i]*255 for i in range(3)]
    mx=np.maximum(np.maximum(R,G),B); mn=np.minimum(np.minimum(R,G),B)
    k=(R>95)&(G>40)&(B>20)&((mx-mn)>15)&(np.abs(R-G)>15)&(R>G)&(R>B)
    return k
res=[]
for k,r in d.items():
    if r["alpha_mat"] or "color" not in r["roles"]: continue
    f,m=k.split("|"); path=os.path.join(T,f)
    mat=bpy.data.materials.new(m); roles,how=TX.find_textures(mat,path)
    if "color" not in roles: continue
    a=TX.load_image(roles["color"],256)
    if roles.get("tint"): a=a.copy(); a[...,:3]*=np.array(roles["tint"])
    s=float(skin_share(a[...,:3]).mean())
    res.append((s,k,roles["color"]))
res.sort(key=lambda x:-x[0])
tiles=[]
for s,k,p in res[:48]:
    a=TX.load_image(p,128); a=np.asarray(Image.fromarray((np.clip(a,0,1)*255).astype(np.uint8)).resize((120,120)))[::-1][...,:3]
    im=Image.fromarray(np.ascontiguousarray(a)); dr=ImageDraw.Draw(im); dr.rectangle((0,0,120,10),fill=(0,0,0)); dr.text((1,0),f"{len(tiles)} {s:.2f}",fill=(255,255,0)); tiles.append(np.asarray(im))
    print(len(tiles)-1, round(s,2), k[-75:])
cols=8
while len(tiles)%cols: tiles.append(np.zeros_like(tiles[0]))
Image.fromarray(np.concatenate([np.concatenate(tiles[i:i+cols],1) for i in range(0,len(tiles),cols)],0)).save("skindet.png")
print("share>=0.5:",sum(1 for s,_,_ in res if s>=0.5),"of",len(res))
