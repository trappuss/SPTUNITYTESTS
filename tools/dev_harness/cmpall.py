import bpy, sys, glob, os, numpy as np
a,b=sys.argv[sys.argv.index("--")+1:]
def load(p):
    bpy.ops.wm.open_mainfile(filepath=p); out={}
    for o in bpy.data.objects:
        if o.type=="MESH" and any(c.name.startswith('COD2EFT_') for c in o.users_collection):
            co=np.empty(len(o.data.vertices)*3); o.data.vertices.foreach_get("co",co); out[o.name]=co.reshape(-1,3)
    return out
worst=0
for f in sorted(glob.glob(a+"/*_EFT.blend")):
    A=load(f); B=load(os.path.join(b,os.path.basename(f)))
    for k in A:
        dd=np.abs(A[k]-B[k]).max() if k in B and A[k].shape==B[k].shape else 1e9
        worst=max(worst,dd)
        if dd>1e-4: print("DIFF",os.path.basename(f),k,dd)
print("max vertex difference 5.0 vs 4.4:", worst)
