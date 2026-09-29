bl_info = {
    "name": "COD2EFT Porter",
    "author": "minmaxmaxminnning + Claude",
    "version": (2, 5, 2),              # = VERSION in cod2eft_porter.py
    "blender": (4, 2, 0),
    "location": "View3D > Sidebar (N) > COD2EFT",
    "description": "Import Call of Duty (.fbx/.cast) characters, auto-pose them onto the EFT "
                   "(Tarkov) skeleton, convert weights and split Head/Upper/Lower - single "
                   "models or whole folders",
    "category": "Import-Export",
}

import os  # noqa: E402

# "Live" install (set up by Install_COD2EFT_Addon.bat): cod2eft_home.txt next to this file names
# the COD2EFT folder, and the add-on's modules are loaded from THAT folder instead of the copies
# installed with the zip - so updated files there take effect on the next Blender start (or with
# the Reload button) without reinstalling.  If the folder is gone, the installed copies are used.
LIVE_HOME = None
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "cod2eft_home.txt"),
              "r", encoding="utf-8") as _fh:
        _home = os.path.normpath(_fh.read().strip().strip('"'))
    if all(os.path.isfile(os.path.join(_home, f)) for f in
           ("cod2eft_ui.py", "cod2eft_porter.py", "cod2eft_textures.py")):
        LIVE_HOME = _home
except OSError:
    pass
if LIVE_HOME and LIVE_HOME not in __path__:                               # noqa: F821
    __path__.insert(0, LIVE_HOME)                                          # noqa: F821

if "bpy" in locals():                      # re-enable in the same session -> reload modules
    import importlib
    from . import cod2eft_porter, cod2eft_files, cod2eft_tools, cod2eft_textures, cod2eft_ui
    for _m in (cod2eft_porter, cod2eft_files, cod2eft_tools, cod2eft_textures, cod2eft_ui):
        importlib.reload(_m)

import bpy  # noqa: E402,F401
from . import cod2eft_ui  # noqa: E402


def register():
    cod2eft_ui.register()


def unregister():
    cod2eft_ui.unregister()
