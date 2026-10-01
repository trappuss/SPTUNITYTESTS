import bpy, sys, os, json, glob, re, numpy as np
sys.path.insert(0,"/home/claude/cod2eft"); import cod2eft_porter as C, cod2eft_textures as TX
T="/mnt/user-data/uploads/SPTModdingTools/Testing"
files=[f for f in sorted(glob.glob(T+"/**/*.cast",recursive=True)+glob.glob(T+"/**/*.fbx",recursive=True))
       if "EFT_Converted" not in f and "CUSTOM" not in f and not re.search(r"lod[1-9]|vm_|_vm|viewarm|/_images/",f,re.I)]
# one model file per folder (prefer cast)
seen={}
for f in files:
    d=os.path.dirname(f)
    if d not in seen or f.endswith(".cast"): seen[d]=f
files=sorted(seen.values())
print(len(files),"files",flush=True)
def samp(img, uv):
    H,W=img.shape[:2]; u=np.mod(uv[:,0],1.0); v=np.mod(uv[:,1],1.0)
    return img[np.clip((v*H).astype(int),0,H-1), np.clip((u*W).astype(int),0,W-1)]
BARY=np.array([[1/3,1/3,1/3],[.6,.2,.2],[.2,.6,.2],[.2,.2,.6]])
out=json.load(open("codhist.json")) if os.path.exists("codhist.json") else {}
done_files={k.split("|")[0] for k in out}
for path in files:
    if os.path.relpath(path,T) in done_files: continue
    try:
        bpy.ops.wm.read_homefile(use_empty=True); TX.clear_cache()
        new=C.import_model(path, log=lambda m:None)
    except Exception as e: print("ERR",path,e,flush=True); continue
    for o in new:
        if o.type!="MESH" or not o.data.uv_layers: continue
        me=o.data; me.calc_loop_triangles()
        uvl=np.empty(len(me.loops)*2); me.uv_layers[0].data.foreach_get("uv",uvl); uvl=uvl.reshape(-1,2)
        tl=np.empty(len(me.loop_triangles)*3,int); me.loop_triangles.foreach_get("loops",tl); tl=tl.reshape(-1,3)
        tm=np.empty(len(me.loop_triangles),int); me.loop_triangles.foreach_get("material_index",tm)
        ta=np.empty(len(me.loop_triangles)); me.loop_triangles.foreach_get("area",ta)
        sc=abs(o.matrix_world.to_3x3().determinant())**(2/3); ta=ta*sc
        for i,m in enumerate(me.materials):
            if not m: continue
            sel=tm==i
            if not sel.any(): continue
            key=os.path.relpath(path,T)+"|"+TX.base_name(m.name)
            try: roles,how=TX.find_textures(m,path)
            except Exception as e: print("roles err",key,e); continue
            P=uvl[tl[sel]]; S=np.einsum("kj,fjc->fkc",BARY,P).reshape(-1,2); w=np.repeat(ta[sel]/4,4)
            r={"area_m2":float(ta[sel].sum()),"alpha_mat":TX.is_alpha_material(m.name),"roles":sorted(k for k,v in roles.items() if v and k!="opacity_cands"),"opacity":bool(roles.get("opacity_cands"))}
            if "color" in roles:
                c=samp(TX.load_image(roles["color"],512),S); lum=c[:,:3]@np.array([.2126,.7152,.0722])
                r["h_l"]=np.histogram(lum,50,(0,1),weights=w)[0].tolist(); r["h_a"]=np.histogram(c[:,3],50,(0,1),weights=w)[0].tolist()
                r["rgb"]=[float(x) for x in np.average(c[:,:3],axis=0,weights=w)]
            g=None
            if "nog" in roles: g=samp(TX.load_image(roles["nog"],512),S)[:,0]
            elif "gloss" in roles:
                g=samp(TX.load_image(roles["gloss"],512),S)[:,0]
                if roles.get("gloss_inverted"): g=1-g
            if g is not None: r["h_g"]=np.histogram(g,50,(0,1),weights=w)[0].tolist()
            out[key]=r
    print("done",os.path.relpath(path,T),len(out),flush=True)
    json.dump(out,open("codhist.json","w"))
