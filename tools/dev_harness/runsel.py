import sys, runpy
a=sys.argv[sys.argv.index("--")+1:]
out,extra,ins=a[0],a[1].split(),a[2:]
sys.argv=["blender","--","--template","/mnt/user-data/uploads/SPTModdingTools/Testing/CUSTOM/EFT BASIC [Template].blend","--out",out]+[e for e in extra if e]+ins
runpy.run_path("/home/claude/cod2eft/cod2eft_batch.py", run_name="__main__")
