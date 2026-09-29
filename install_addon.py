"""Run by Install_COD2EFT_Addon.bat inside Blender (background):
  1. rebuilds COD2EFT_Blender_Addon.zip from this folder,
  2. removes the old single-file 'cod2eft_porter' add-on if an earlier version installed it,
  3. installs + enables the zip exactly like Preferences > Add-ons > Install from Disk, and
     makes it a "live" install: the add-on loads its code from THIS folder, so later updates
     here work after a Blender restart (or the Reload button) without running this again,
  4. points the add-on at this folder for its settings (bone map / pose tweaks) and at your
     EFT template (template_path.txt, or ..\\Testing\\CUSTOM\\EFT BASIC [Template].blend),
  5. saves preferences."""
import bpy
import os
import sys
import addon_utils

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_addon  # noqa: E402

zip_path = build_addon.build()
print(f"[COD2EFT] Built {zip_path}")

# old v1 single-file add-on
try:
    addon_utils.disable("cod2eft_porter", default_set=True)
except Exception:
    pass
addons_dir = bpy.utils.user_resource("SCRIPTS", path="addons")


def _installed_version():
    import re
    try:
        with open(os.path.join(addons_dir, "cod2eft", "__init__.py"), "r", encoding="utf-8") as fh:
            m = re.search(r'"version": \((\d+), (\d+), (\d+)\)', fh.read())
        return ".".join(m.groups()) if m else "unknown"
    except OSError:
        return None


print(f"[COD2EFT] Blender: {bpy.app.binary_path} ({bpy.app.version_string})")
print(f"[COD2EFT] Add-on folder of this Blender: {addons_dir}")
_real = os.path.realpath(addons_dir)
if os.path.normcase(_real) != os.path.normcase(os.path.abspath(addons_dir)):
    print(f"[COD2EFT]   (redirected to {_real})")
_old = _installed_version()
_new = ".".join(str(x) for x in build_addon.versions()[0])
print(f"[COD2EFT] Currently installed: {'v' + _old if _old else 'nothing'}  ->  installing v{_new}")
for f in ("cod2eft_porter.py", "cod2eft_home.txt"):
    p = os.path.join(addons_dir, f)
    if os.path.isfile(p):
        os.remove(p)
        print(f"[COD2EFT] Removed old {p}")

bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
# live install: the add-on loads its code from this folder from now on (see addon_init.py)
home_txt = os.path.join(addons_dir, "cod2eft", "cod2eft_home.txt")
with open(home_txt, "w", encoding="utf-8") as fh:
    fh.write(HERE)
print(f"[COD2EFT] Live install: code is loaded from {HERE} - updates there need no reinstall")
bpy.ops.preferences.addon_enable(module="cod2eft")
prefs = bpy.context.preferences.addons["cod2eft"].preferences

prefs.data_dir = HERE
tp = ""
txt = os.path.join(HERE, "template_path.txt")
if os.path.isfile(txt):
    with open(txt, "r", encoding="utf-8") as fh:
        tp = fh.read().strip().strip('"')
if not (tp and os.path.isfile(tp)):
    tp = os.path.normpath(os.path.join(HERE, "..", "Testing", "CUSTOM", "EFT BASIC [Template].blend"))
if os.path.isfile(tp):
    prefs.template_path = tp
    print(f"[COD2EFT] Template: {tp}")
else:
    print("[COD2EFT] Template not found automatically - set it in the COD2EFT panel (Setup)")

bpy.ops.wm.save_userpref()
# check what is on disk now
_now = _installed_version()
try:
    with open(home_txt, "r", encoding="utf-8") as fh:
        _live = fh.read().strip()
except OSError:
    _live = ""
if _now != _new or os.path.normcase(_live) != os.path.normcase(HERE):
    print(f"[COD2EFT] ERROR: after installing, {addons_dir} holds v{_now} (live: {_live or 'no'})")
    sys.exit(1)
print(f"[COD2EFT] Installed + enabled COD2EFT v{_new} in Blender {bpy.app.version_string}")
print(f"[COD2EFT]   in {os.path.join(addons_dir, 'cod2eft')}, code loaded live from {HERE}")
