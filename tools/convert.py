"""Convert any COD export folder(s) with the add-on in THIS repo (cloud or PC, no Blender UI).

    python tools/convert.py --out OUT [--template BLEND] [--blender-python PY] FOLDER [FOLDER ...] [-- batch flags]

- Runs `blender/COD2EFT/cod2eft_batch.py` as an importable package, like the installed zip (same packaging as
  `tools/regress.py`).
- `--template`: the EFT template .blend. Default: the user's real one if this repo sits in the PC workspace
  (`..\\Testing\\CUSTOM\\EFT BASIC [Template].blend`), else the one rebuilt from the FBX in from_pc (see CLAUDE.md).
- Everything after `--` goes to the batch unchanged, e.g. `-- --material-mode enc3 --export-fbx --fp-hands`.
- The full batch log is written to OUT/_convert_log.txt; the last lines are printed.
Needs a Python with bpy (`--blender-python`, default: this Python).
"""
import argparse
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import regress  # noqa: E402  (build_package, ensure_template)

REAL_TEMPLATE = os.path.normpath(os.path.join(regress.REPO, "..", "Testing", "CUSTOM", "EFT BASIC [Template].blend"))


def main():
    argv = sys.argv[1:]
    extra = []
    if "--" in argv:
        i = argv.index("--")
        argv, extra = argv[:i], argv[i + 1:]
    ap = argparse.ArgumentParser()
    ap.add_argument("folders", nargs="+")
    ap.add_argument("--out", required=True)
    ap.add_argument("--template")
    ap.add_argument("--blender-python", default=sys.executable)
    a = ap.parse_args(argv)
    template = a.template or (REAL_TEMPLATE if os.path.isfile(REAL_TEMPLATE) else
                              regress.ensure_template(os.path.join(tempfile.gettempdir(), "c2e_regress_template",
                                                                   "EFT BASIC [Template].blend")))
    pkg_parent = os.path.join(tempfile.gettempdir(), "c2e_convert_pkg")
    regress.build_package(pkg_parent)
    os.makedirs(a.out, exist_ok=True)
    code = ("import sys, runpy\n"
            f"sys.path.insert(0, {pkg_parent!r}); sys.path.insert(0, {os.path.join(pkg_parent, 'cod2eft')!r})\n"
            f"sys.argv = ['blender', '--', '--template', {template!r}, '--out', {os.path.abspath(a.out)!r}, '--cast'] + "
            f"{extra!r} + {[os.path.abspath(f) for f in a.folders]!r}\n"
            f"runpy.run_path({os.path.join(pkg_parent, 'cod2eft', 'cod2eft_batch.py')!r}, run_name='__main__')\n")
    log = os.path.join(a.out, "_convert_log.txt")
    with open(log, "w", encoding="utf-8") as fh:
        r = subprocess.run([a.blender_python, "-c", code], stdout=fh, stderr=subprocess.STDOUT, text=True)
    lines = [ln for ln in open(log, encoding="utf-8", errors="ignore") if ln.startswith("[COD2EFT]")]
    print(f"template: {template}")
    print("".join(lines[-12:]))
    print(f"exit {r.returncode}; full log: {log}")
    sys.exit(r.returncode)


if __name__ == "__main__":
    main()
