"""Builds COD2EFT_Blender_Addon.zip (Blender: Edit > Preferences > Add-ons > Install from Disk)
from the files in this folder.  Run: python build_addon.py   (or via Install_COD2EFT_Addon.bat)"""
import os
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = "cod2eft"
FILES = {"addon_init.py": "__init__.py", "cod2eft_porter.py": None, "cod2eft_ui.py": None,
         "cod2eft_files.py": None, "cod2eft_tools.py": None, "cod2eft_textures.py": None,
         "cod2eft_batch.py": None,
         "README.md": None, "CHANGELOG.md": None, "cod2eft_bonemap.example.json": None}


def versions():
    """(VERSION in cod2eft_porter.py, bl_info version in addon_init.py)"""
    import re
    with open(os.path.join(HERE, "cod2eft_porter.py"), "r", encoding="utf-8") as fh:
        v = re.search(r"^VERSION = \((\d+), (\d+), (\d+)\)", fh.read(), re.M)
    with open(os.path.join(HERE, "addon_init.py"), "r", encoding="utf-8") as fh:
        b = re.search(r'"version": \((\d+), (\d+), (\d+)\)', fh.read())
    return (tuple(int(x) for x in v.groups()) if v else None,
            tuple(int(x) for x in b.groups()) if b else None)


def build(out=os.path.join(HERE, "COD2EFT_Blender_Addon.zip")):
    v, b = versions()
    if v is None or v != b:
        raise RuntimeError(f"version mismatch: cod2eft_porter.py {v} vs addon_init.py {b}")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for src, dst in FILES.items():
            z.write(os.path.join(HERE, src), f"{PKG}/{dst or src}")
        vend = os.path.join(HERE, "vendor", "cod2eft_cast")
        for f in sorted(os.listdir(vend)):
            if f.endswith((".py", ".txt")):
                z.write(os.path.join(vend, f), f"{PKG}/vendor/cod2eft_cast/{f}")
    return out


if __name__ == "__main__":
    print("built", build())
