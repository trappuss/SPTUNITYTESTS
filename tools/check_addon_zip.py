"""Builds the Blender add-on zip into a temp folder and checks it (no bpy needed).

    python tools/check_addon_zip.py

Checks: build_addon.build() runs (it crashed in 2.6.8 on a folder the repo no longer has), the versions in
cod2eft_porter.py and addon_init.py match, every cod2eft_*.py and the vendored Cast importer are in the zip, every
.py in the zip compiles, and nothing outside the package sneaks in. Exit 1 on any problem.
"""
import os
import shutil
import sys
import tempfile
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "blender", "COD2EFT")


def main():
    tmp = tempfile.mkdtemp(prefix="c2e_zip_")
    work = os.path.join(tmp, "COD2EFT")
    shutil.copytree(SRC, work, ignore=shutil.ignore_patterns("__pycache__", "*.zip"))
    sys.path.insert(0, work)
    import build_addon
    problems = []
    try:
        z = build_addon.build(os.path.join(tmp, "addon.zip"))
    except Exception as e:
        print(f"FAIL: build_addon.build() raised {type(e).__name__}: {e}")
        sys.exit(1)
    v, b = build_addon.versions()
    names = zipfile.ZipFile(z).namelist()
    want = ["cod2eft/__init__.py"] + [f"cod2eft/{f}" for f in sorted(os.listdir(SRC))
                                      if f.startswith("cod2eft_") and f.endswith(".py")]
    want += [f"cod2eft/vendor/cod2eft_cast/{f}" for f in sorted(os.listdir(os.path.join(SRC, "vendor", "cod2eft_cast")))
             if f.endswith((".py", ".txt"))]
    missing = [w for w in want if w not in names]
    if missing:
        problems.append(f"missing from the zip: {missing}")
    stray = [n for n in names if not n.startswith("cod2eft/")]
    if stray:
        problems.append(f"outside the package: {stray}")
    for n in names:
        if n.endswith(".py"):
            try:
                compile(zipfile.ZipFile(z).read(n), n, "exec")
            except SyntaxError as e:
                problems.append(f"{n}: {e}")
    print(f"version {'.'.join(map(str, v))} (addon_init {'.'.join(map(str, b))}), {len(names)} files in the zip")
    for p in problems:
        print("FAIL: " + p)
    shutil.rmtree(tmp, ignore_errors=True)
    print("OK" if not problems else f"{len(problems)} problem(s)")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
