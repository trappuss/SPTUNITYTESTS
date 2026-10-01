import bpy, sys, os, json, numpy as np
ZIP="/home/claude/cod2eft/COD2EFT_Blender_Addon.zip"
U="/mnt/user-data/uploads/SPTModdingTools/Testing"; TPL=U+"/CUSTOM/EFT BASIC [Template].blend"
bpy.ops.preferences.addon_install(filepath=ZIP, overwrite=True); bpy.ops.preferences.addon_enable(module="cod2eft")
pf=bpy.context.preferences.addons["cod2eft"].preferences; pf.template_path=TPL; pf.data_dir="/home/claude/fitdev/ui/data4"; os.makedirs(pf.data_dir,exist_ok=True)
from cod2eft import cod2eft_ui as UI, cod2eft_tools as TL, cod2eft_porter as C
KB=U+"/WARZONE 2 - MW2 Female/body_mp_kleo_iw9_3_1"
def fresh(out):
    bpy.ops.wm.read_homefile(use_empty=True); st=bpy.context.scene.cod2eft; st.output_dir=out; st.texture_size="1024"; return st
def imp(d):
    fs=[x for x in os.listdir(d) if x.endswith((".fbx",".cast"))]
    return bpy.ops.cod2eft.import_cod(filepath=os.path.join(d,fs[0]), directory=d, files=[{"name":fs[0]}], convert=True)
def summary(tag):
    eft=C.find_eft_armature(); objs=TL.converted_meshes(eft)
    hands=[o for o in objs if str(o.get("cod2eft_part","")).startswith("Hands")]
    print(tag, "parts", len(objs), "hands", [(o.name, len(o.data.vertices), [m.name for m in o.data.materials][:3], o.hide_get()) for o in hands][:4], "n hands", len(hands))
# a: textured, then button
st=fresh("/home/claude/fitdev/ui/out4a/"); print("a import", imp(KB)); summary("a before")
print("a op", bpy.ops.cod2eft.fp_hands()); summary("a after"); print("a files", sorted(f for f in os.listdir("/home/claude/fitdev/ui/out4a") if "Hands" in f))
print("a op again", bpy.ops.cod2eft.fp_hands()); summary("a again")
# b: split on during import, then button
st=fresh("/home/claude/fitdev/ui/out4b/"); st.split_materials=True; print("b import", imp(KB)); summary("b before")
print("b op", bpy.ops.cod2eft.fp_hands()); summary("b after")
# c: setting on + split on
st=fresh("/home/claude/fitdev/ui/out4c/"); st.split_materials=True; st.fp_hands=True; print("c import", imp(KB)); summary("c")
print("c op again", bpy.ops.cod2eft.fp_hands()); summary("c again")
print("c export", bpy.ops.cod2eft.export(filepath="/home/claude/fitdev/ui/out4c/t.fbx"))
print("scene fp file", bpy.context.scene.get("cod2eft_fp_file"))
st.fp_source="THIRD"; print("c third", bpy.ops.cod2eft.fp_hands()); summary("c third")
# d: textures off
st=fresh("/home/claude/fitdev/ui/out4d/"); st.convert_textures=False; st.fp_hands=True; print("d import", imp(KB)); summary("d")
r=bpy.data.texts.get("COD2EFT_Report"); print("report", [l.body[:160] for l in r.lines if "First-person" in l.body][-1:])
bpy.ops.preferences.addon_disable(module="cod2eft"); print("disabled OK")
