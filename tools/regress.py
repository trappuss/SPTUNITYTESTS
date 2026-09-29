# Regression harness for COD2EFT (docs/PROJECT_CONTEXT.md, queue item 4).  Converts the test characters with the
# add-on as it is in this repo, headless, and compares against a stored baseline:
#   * the fit report lines ("Body volume", "Body match after fit", ...),
#   * the SHA-256 of every PNG written,
#   * the PNG tag of each file.
# Needs Blender as a Python module (see CLAUDE.md: bpy 4.4 in a Python 3.11 venv, plus numpy<2 pillow).
#
#   python tools/regress.py run   OUT_DIR [--mode enc2|enc3] [--only NAME] [-- extra batch args]
#   python tools/regress.py save  OUT_DIR [--mode ...]      -> tools/regress_baseline_<mode>.json
#   python tools/regress.py check OUT_DIR [--mode ...]      -> differences vs that baseline (exit 1 if any)
# "run" converts; "save" / "check" only read OUT_DIR.  Each character runs in its own Python process.
# The template is rebuilt from the FBX in from_pc (58 bones on "Body EFT Armature") into OUT_DIR/_template.blend;
# it is NOT byte-identical to the user's EFT BASIC [Template].blend.
import hashlib, json, os, re, shutil, subprocess, sys, glob

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
ADDON = os.path.join(ROOT, "blender", "COD2EFT")
TEMPLATE_FBX = os.path.join(ROOT, "from_pc", "20260928-231514", "attached", "EFT BASIC [Template].fbx")
CHARACTERS = {                    # name -> folder with the COD export (see CLAUDE.md "Test data")
    "kleo": "from_pc/20260928-223033/attached/WARZONE 2 - MW2 Female",
    "sunflower": "from_pc/20260928-223033/attached/New Replacer - sunflower_base - New Replacer",
    "mw4_male": "from_pc/20260928-223033/attached/MW4 Beta Male",
    "mw4_female": "from_pc/20260928-230805/attached/MW4 Beta Female",
}
BASE_ARGS = ["--cast", "--fp-hands"]
REPORT_KEYS = ("Body volume", "Body match after fit", "pelvis vs waist")

CHILD = r'''
import sys, runpy
pkg, template, out, folder = sys.argv[1:5]
sys.path.insert(0, pkg)
import cod2eft  # noqa
if template.endswith(".fbx"):
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=template, ignore_leaf_bones=True)
    arm = [o for o in bpy.data.objects if o.type == "ARMATURE" and o.name == "Body EFT Armature"]
    assert arm and len(arm[0].data.bones) == 58, "template rebuild: expected 58 bones on Body EFT Armature"
    bpy.ops.wm.save_as_mainfile(filepath=out)
    sys.exit(0)
sys.argv = ["blender", "--", "--template", template, "--out", out] + sys.argv[5:] + [folder]
runpy.run_path(pkg + "/cod2eft/cod2eft_batch.py", run_name="__main__")
'''


def make_package(dest):
    pkg = os.path.join(dest, "_pkg")
    shutil.rmtree(pkg, ignore_errors=True)
    os.makedirs(os.path.join(pkg, "cod2eft"))
    for f in glob.glob(os.path.join(ADDON, "*.py")):
        shutil.copy(f, os.path.join(pkg, "cod2eft", "__init__.py" if os.path.basename(f) == "addon_init.py"
                                     else os.path.basename(f)))
    shutil.copytree(os.path.join(ADDON, "vendor"), os.path.join(pkg, "cod2eft", "vendor"))
    child = os.path.join(dest, "_child.py")
    open(child, "w").write(CHILD)
    return pkg, child


def png_tag(path):
    """The COD2EFT tEXt tag of a PNG (walks the chunks before the image data), or None."""
    head = open(path, "rb").read(4096)
    i = 8
    while i + 8 <= len(head):
        n = int.from_bytes(head[i:i + 4], "big")
        kind = head[i + 4:i + 8]
        if kind == b"IDAT" or n > len(head):
            break
        body = head[i + 8:i + 8 + n]
        if kind == b"tEXt" and body.startswith(b"Software\x00COD2EFT"):
            return body.split(b"\x00", 1)[1].decode("latin-1")
        i += 12 + n
    return None


def collect(out, mode):
    res = {}
    for name in CHARACTERS:
        d = os.path.join(out, mode, name)
        if not os.path.isdir(d):
            continue
        pngs = {os.path.basename(p): {"sha256": hashlib.sha256(open(p, "rb").read()).hexdigest()[:16], "tag": png_tag(p)}
                for p in sorted(glob.glob(os.path.join(d, "*.png")))}
        lines = []
        for rp in sorted(glob.glob(os.path.join(d, "*_report.txt"))):
            for ln in open(rp, encoding="utf-8", errors="replace"):
                if any(k in ln for k in REPORT_KEYS):
                    lines.append(ln.strip())
        res[name] = {"pngs": pngs, "report": lines}
    return res


def main():
    a = sys.argv[1:]
    extra = a[a.index("--") + 1:] if "--" in a else []
    a = a[:a.index("--")] if "--" in a else a
    if len(a) < 2 or a[0] not in ("run", "save", "check"):
        print(__doc__ if __doc__ else "", open(__file__).read().split("import hashlib")[0]); sys.exit(2)
    cmd, out = a[0], os.path.abspath(a[1])
    mode = a[a.index("--mode") + 1] if "--mode" in a else "enc2"
    only = a[a.index("--only") + 1] if "--only" in a else None
    base = os.path.join(ROOT, "tools", f"regress_baseline_{mode}.json")
    if cmd == "run":
        os.makedirs(out, exist_ok=True)
        pkg, child = make_package(out)
        tpl = os.path.join(out, "_template.blend")
        if not os.path.isfile(tpl):
            subprocess.run([sys.executable, child, pkg, TEMPLATE_FBX, tpl, ""], check=True)
        for name, folder in CHARACTERS.items():
            if only and name != only:
                continue
            d = os.path.join(out, mode, name)
            shutil.rmtree(d, ignore_errors=True)
            os.makedirs(d)
            args = BASE_ARGS + ["--material-mode", mode] + extra
            with open(d + ".log", "w") as log:
                r = subprocess.run([sys.executable, child, pkg, tpl, d, os.path.join(ROOT, folder)] + args,
                                   stdout=log, stderr=subprocess.STDOUT, cwd=ROOT)
            print(f"{name}: exit {r.returncode}, {len(glob.glob(os.path.join(d, '*.png')))} PNGs  ({d}.log)")
        return
    cur = collect(out, mode)
    if cmd == "save":
        json.dump(cur, open(base, "w"), indent=1)
        print("saved", base, {k: len(v["pngs"]) for k, v in cur.items()})
        return
    ref = json.load(open(base))
    diffs = []
    for name, r in ref.items():
        c = cur.get(name)
        if c is None:
            diffs.append(f"{name}: not converted"); continue
        for f in sorted(set(r["pngs"]) | set(c["pngs"])):
            x, y = r["pngs"].get(f), c["pngs"].get(f)
            if x != y:
                diffs.append(f"{name}: {f}: {x} -> {y}")
        for x, y in zip(r["report"], c["report"]):
            if x != y:
                diffs.append(f"{name}: report\n    was {x}\n    now {y}")
        if len(r["report"]) != len(c["report"]):
            diffs.append(f"{name}: {len(r['report'])} report lines -> {len(c['report'])}")
    print("\n".join(diffs) if diffs else f"OK: identical to {os.path.basename(base)}")
    sys.exit(1 if diffs else 0)


if __name__ == "__main__":
    main()
