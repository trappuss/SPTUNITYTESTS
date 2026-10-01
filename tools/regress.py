"""COD2EFT regression harness: convert every test character with the add-on in this repo and
record what matters, then (optionally) compare with a baseline.

Run with a Python that has bpy (Blender 4.4 as a module, see CLAUDE.md):
    python tools/regress.py --out /tmp/reg_new [--baseline tools/baselines/<name>.json]
                            [--no-textures] [--save-baseline tools/baselines/<name>.json]
                            [--only NAME_SUBSTRING ...] [-- extra batch flags ...]

What it records per character (from the batch report and the output folder):
  - the fit lines: body volume, body match after fit, soles, face, fingertips, body moved,
    limited section centres, head height, posture after fit (2.6.6+; compared only when the
    baseline has it, tolerance 0.3 deg)
  - per converted part: vertex / face counts (from the .blend)
  - every PNG: sha256 (texture output byte-identical or not) and a 16 x 16 block-mean signature
    (2026-10-01): two runs of the same code can differ by 1 step of 8 bits on a few dozen pixels
    (float noise), which changes the hash but not the signature. A hash change whose signature
    stays within PNG_NOISE is reported as a note, not as a difference.
Compare: numbers that moved more than --tol (cm, default 0.2), changed hashes, parts that
appeared / disappeared.  Exit code 1 when anything differs (0 = identical within tolerance).
"""
import argparse
import glob
import hashlib
import json
import os
import re
import runpy
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMPLATE_FBX = os.path.join(REPO, "from_pc", "20260928-231514", "attached", "EFT BASIC [Template].fbx")
TEST_FOLDERS = [
    "from_pc/20260928-223033/attached/WARZONE 2 - MW2 Female",
    "from_pc/20260928-223033/attached/New Replacer - sunflower_base - New Replacer",
    "from_pc/20260928-223033/attached/MW4 Beta Male",
    "from_pc/20260928-230805/attached/MW4 Beta Female",
]
NUM = r"[-+]?\d+(?:\.\d+)?"


def build_package(dst):
    """The add-on as an importable package 'cod2eft' (like the installed zip)."""
    src = os.path.join(REPO, "blender", "COD2EFT")
    pkg = os.path.join(dst, "cod2eft")
    shutil.rmtree(dst, ignore_errors=True)
    os.makedirs(pkg)
    for f in os.listdir(src):
        if f.endswith(".py") and f.startswith("cod2eft_"):
            shutil.copy(os.path.join(src, f), pkg)
    shutil.copy(os.path.join(src, "addon_init.py"), os.path.join(pkg, "__init__.py"))
    shutil.copytree(os.path.join(src, "vendor"), os.path.join(pkg, "vendor"))
    return pkg


def ensure_template(path):
    if os.path.isfile(path):
        return path
    code = ("import bpy\n"
            "bpy.ops.wm.read_factory_settings(use_empty=True)\n"
            f"bpy.ops.import_scene.fbx(filepath={TEMPLATE_FBX!r}, ignore_leaf_bones=True)\n"
            "a=[o for o in bpy.context.scene.objects if o.type=='ARMATURE']\n"
            "assert a and len(a[0].data.bones)==58, [(o.name, len(o.data.bones)) for o in a]\n"
            f"bpy.ops.wm.save_as_mainfile(filepath={path!r})\n")
    subprocess.run([sys.executable, "-c", code], check=True, capture_output=True)
    return path


def run_batch(pkg_parent, template, out, folder, extra):
    code = ("import sys, runpy\n"
            f"sys.path.insert(0, {pkg_parent!r}); sys.path.insert(0, {os.path.join(pkg_parent, 'cod2eft')!r})\n"
            f"sys.argv = ['blender', '--', '--template', {template!r}, '--out', {out!r}, '--cast'] + {extra!r} + [{folder!r}]\n"
            f"runpy.run_path({os.path.join(pkg_parent, 'cod2eft', 'cod2eft_batch.py')!r}, run_name='__main__')\n")
    r = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True)
    with open(os.path.join(out, "_batch_stdout.txt"), "a", encoding="utf-8") as fh:
        fh.write(r.stdout[-200000:] + "\n" + r.stderr[-50000:])


def parse_report(path):
    txt = open(path, encoding="utf-8", errors="ignore").read()
    rec = {"version": (re.search(r"COD2EFT v(\S+)", txt) or [None, None])[1]}

    def pairs(line_re, key):
        m = re.search(line_re, txt)
        if m:
            rec[key] = {k: float(v) for k, v in re.findall(rf"(\w+) ({NUM})(?:->|,|$|\s)", m.group(1))}
    m = re.search(r"Body volume: COD section centre vs joint \(fwd cm\) -> EFT: (.*)", txt)
    if m:
        rec["body_volume_cod"] = {k: float(a) for k, a, _ in re.findall(rf"(\w+) ({NUM})->({NUM})", m.group(1))}
    pairs(r"Body match after fit \(COD - EFT section centre, forward cm\): (.*)", "body_match")
    for key, rx in (("body_moved_cm", rf"Body moved ({NUM}) cm"),
                    ("soles_cm", rf"Soles after fit: ({NUM}) cm"),
                    ("fingertips_cm", rf"Fingertips after fit: ({NUM}) cm"),
                    ("head_height", rf"spine \+ neck x({NUM})")):
        m = re.search(rx, txt)
        if m:
            rec[key] = float(m.group(1))
    m = re.search(rf"Face after fit: .*? ({NUM}) cm forward, ({NUM}) cm up", txt)
    if m:
        rec["face_cm"] = [float(m.group(1)), float(m.group(2))]
    m = re.search(r"Posture after fit[^:]*: (.*)", txt)
    if m:
        rec["posture"] = {k: [float(c), float(e)] for k, c, e in
                          re.findall(rf"([\w~]+) COD ({NUM}) / EFT ({NUM})", m.group(1))}
    m = re.search(r"limited unusual section centres[^:]*: (.*)", txt)
    rec["limited"] = m.group(1).strip() if m else ""
    rec["parts"] = re.findall(r"Done: (.*) parented", txt)
    rec["warnings"] = [ln.strip() for ln in txt.splitlines() if "WARNING" in ln or "could not" in ln][:20]
    return rec


def mesh_counts(blend):
    code = ("import bpy, json, sys\n"
            f"bpy.ops.wm.open_mainfile(filepath={blend!r})\n"
            "print('JSON'+json.dumps({o.name: [len(o.data.vertices), len(o.data.polygons), [m.name for m in o.data.materials if m]] "
            "for o in bpy.context.scene.objects if o.type=='MESH' and o.get('cod2eft_part')}))\n")
    r = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True)
    for ln in r.stdout.splitlines():
        if ln.startswith("JSON"):
            return json.loads(ln[4:])
    return {}


PNG_NOISE = 1e-4      # max block-mean change (0..1) still counted as float noise


def png_signature(path):
    """16 x 16 block means per channel (0..1), as uint16 in base64; None without Pillow."""
    try:
        import base64
        import numpy as np
        from PIL import Image
    except ImportError:
        return None
    a = np.asarray(Image.open(path).convert("RGBA"), np.float64) / 255.0
    h, w = a.shape[0] // 16 * 16, a.shape[1] // 16 * 16
    if h == 0 or w == 0:
        return None
    b = a[:h, :w].reshape(16, h // 16, 16, w // 16, 4).mean((1, 3))
    return base64.b64encode(np.round(b * 65535).astype("<u2").tobytes()).decode("ascii")


def png_sig_diff(s1, s2):
    import base64
    import numpy as np
    x = np.frombuffer(base64.b64decode(s1), "<u2").astype(np.float64)
    y = np.frombuffer(base64.b64decode(s2), "<u2").astype(np.float64)
    return float(np.abs(x - y).max() / 65535) if x.shape == y.shape else 1.0


def collect(out):
    res = {}
    for rep in sorted(glob.glob(os.path.join(out, "**", "*_EFT_report.txt"), recursive=True)):
        name = os.path.basename(rep)[:-len("_EFT_report.txt")]
        d = os.path.dirname(rep)
        rec = parse_report(rep)
        blend = os.path.join(d, name + "_EFT.blend")
        rec["meshes"] = mesh_counts(blend) if os.path.isfile(blend) else {}
        pngs = sorted(glob.glob(os.path.join(d, name + "_*.png")))
        rec["png"] = {os.path.basename(p): hashlib.sha256(open(p, "rb").read()).hexdigest()[:16]
                      for p in pngs}
        sigs = {os.path.basename(p): png_signature(p) for p in pngs}
        if any(sigs.values()):
            rec["png_sig"] = sigs
        res[name] = rec
    return res


def compare(old, new, tol, tol_deg=0.3, notes=None):
    diffs = []
    notes = notes if notes is not None else []
    for name in sorted(set(old) | set(new)):
        if name not in new:
            diffs.append(f"{name}: MISSING in new run")
            continue
        if name not in old:
            diffs.append(f"{name}: new character (no baseline)")
            continue
        a, b = old[name], new[name]
        for key in ("body_volume_cod", "body_match"):
            for k in sorted(set(a.get(key, {})) | set(b.get(key, {}))):
                va, vb = a.get(key, {}).get(k), b.get(key, {}).get(k)
                if va is None or vb is None or abs(va - vb) > tol:
                    diffs.append(f"{name}: {key}.{k} {va} -> {vb}")
        for key in ("body_moved_cm", "soles_cm", "fingertips_cm", "head_height"):
            va, vb = a.get(key), b.get(key)
            if (va is None) != (vb is None) or (va is not None and abs(va - vb) > tol):
                diffs.append(f"{name}: {key} {va} -> {vb}")
        if a.get("face_cm") and b.get("face_cm") and max(abs(x - y) for x, y in zip(a["face_cm"], b["face_cm"])) > tol:
            diffs.append(f"{name}: face_cm {a['face_cm']} -> {b['face_cm']}")
        for k in sorted(set(a.get("posture", {})) & set(b.get("posture", {}))):
            if abs(a["posture"][k][0] - b["posture"][k][0]) > tol_deg:
                diffs.append(f"{name}: posture.{k} {a['posture'][k][0]} -> {b['posture'][k][0]} deg")
        if a.get("limited") != b.get("limited"):
            diffs.append(f"{name}: limited '{a.get('limited')}' -> '{b.get('limited')}'")
        if a.get("parts") != b.get("parts"):
            diffs.append(f"{name}: parts {a.get('parts')} -> {b.get('parts')}")
        for m in sorted(set(a.get("meshes", {})) | set(b.get("meshes", {}))):
            if a.get("meshes", {}).get(m) != b.get("meshes", {}).get(m):
                diffs.append(f"{name}: mesh {m} {a.get('meshes', {}).get(m)} -> {b.get('meshes', {}).get(m)}")
        for p in sorted(set(a.get("png", {})) | set(b.get("png", {}))):
            if a.get("png", {}).get(p) != b.get("png", {}).get(p):
                sa, sb = a.get("png_sig", {}).get(p), b.get("png_sig", {}).get(p)
                if sa and sb and p in a.get("png", {}) and p in b.get("png", {}):
                    dv = png_sig_diff(sa, sb)
                    if dv <= PNG_NOISE:
                        notes.append(f"{name}: png {p} hash changed, block means within {dv:.1e} (float noise)")
                        continue
                diffs.append(f"{name}: png {p} {a.get('png', {}).get(p)} -> {b.get('png', {}).get(p)}")
    return diffs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--baseline")
    ap.add_argument("--save-baseline")
    ap.add_argument("--no-textures", action="store_true")
    ap.add_argument("--only", nargs="*", default=[])
    ap.add_argument("--tol", type=float, default=0.2)
    ap.add_argument("--template", default="/tmp/c2e_regress_template/EFT BASIC [Template].blend")
    ap.add_argument("--reuse", action="store_true", help="don't convert again, only collect/compare --out")
    args, extra = ap.parse_known_args()
    extra = [e for e in extra if e != "--"]
    if args.no_textures:
        extra.append("--no-textures")
    if not args.reuse:
        os.makedirs(os.path.dirname(args.template), exist_ok=True)
        template = ensure_template(args.template)
        pkg_parent = "/tmp/c2e_regress_pkg"
        build_package(pkg_parent)
        shutil.rmtree(args.out, ignore_errors=True)
        os.makedirs(args.out)
        for f in TEST_FOLDERS:
            if args.only and not any(s.lower() in f.lower() for s in args.only):
                continue
            src = os.path.join(REPO, f)
            if not os.path.isdir(src):
                print(f"(skipped, not found: {f})")
                continue
            out = os.path.join(args.out, os.path.basename(f.rstrip("/")))
            os.makedirs(out, exist_ok=True)
            print(f"converting {f} ...", flush=True)
            run_batch(pkg_parent, template, out, src, extra)
    res = collect(args.out)
    json.dump(res, open(os.path.join(args.out, "regress.json"), "w"), indent=1)
    print(f"{len(res)} character(s): " + ", ".join(f"{k} (v{v.get('version')})" for k, v in res.items()))
    if args.save_baseline:
        os.makedirs(os.path.dirname(os.path.abspath(args.save_baseline)), exist_ok=True)
        json.dump(res, open(args.save_baseline, "w"), indent=1)
        print(f"baseline saved: {args.save_baseline}")
    if args.baseline:
        notes = []
        diffs = compare(json.load(open(args.baseline)), res, args.tol, notes=notes)
        print(f"\n{len(diffs)} difference(s) vs {args.baseline}:" if diffs else f"\nidentical to {args.baseline} (tol {args.tol})")
        for d in diffs:
            print("  " + d)
        for n in notes:
            print("  note: " + n)
        sys.exit(1 if diffs else 0)


if __name__ == "__main__":
    main()
