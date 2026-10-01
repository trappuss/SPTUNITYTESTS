import sys, runpy
a=sys.argv[sys.argv.index("--")+1:]
sys.argv=["blender","--","--template","/mnt/user-data/uploads/SPTModdingTools/Testing/CUSTOM/EFT BASIC [Template].blend","--out",a[0],"--export-fbx"]+a[1:]
runpy.run_path("/home/claude/cod2eft/cod2eft_batch.py", run_name="__main__")
