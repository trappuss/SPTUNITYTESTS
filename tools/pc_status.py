"""One-screen status of the PC side, for the start of a Cowork session (runs in the Cowork VM, plain Python 3).

    python3 tools/pc_status.py

Reports: git branch / unpushed commits / stray lock file; repo versions vs what is deployed in the workspace's
COD2EFT folder and (if mounted) the Unity project; Unity's last "EFT Tools vX loaded" line and compile errors (if
Editor.log is mounted); the newest from_pc send. Mount names follow the Cowork VM ($HOME/mnt/<folder>); anything not
mounted is reported as such, never guessed.
"""
import glob
import hashlib
import os
import re
import subprocess

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WS = os.path.dirname(REPO)                       # the SPTModdingTools workspace
MNT = os.path.join(os.path.expanduser("~"), "mnt")


def sh(*a):
    r = subprocess.run(["git", "-c", "safe.directory=*", "-C", REPO] + list(a), capture_output=True, text=True)
    return r.stdout.strip()


def sha(p):
    return hashlib.sha256(open(p, "rb").read()).hexdigest()


def compare(src, dst, skip=("__pycache__",), pc_owned=("blender_path.txt", "template_path.txt", "wtt_path.txt",
                                                       "COD2EFT_Blender_Addon.zip")):
    if not os.path.isdir(dst):
        return "not mounted / not found"
    diff = []
    for d, _, fs in os.walk(src):
        if any(s in d for s in skip):
            continue
        for f in fs:
            if f in pc_owned:
                continue
            rel = os.path.relpath(os.path.join(d, f), src)
            t = os.path.join(dst, rel)
            if not os.path.isfile(t) or sha(os.path.join(d, f)) != sha(t):
                diff.append(rel)
    return "in sync" if not diff else f"{len(diff)} file(s) differ, e.g. {diff[:4]} -> run SYNC_TO_MY_PC.bat"


def version(path, rx):
    try:
        m = re.search(rx, open(path, encoding="utf-8", errors="ignore").read(), re.M)
        return m.group(1) if m else "?"
    except OSError:
        return "missing"


def main():
    print(f"repo: {REPO}")
    print(f"git: {sh('rev-parse', '--abbrev-ref', 'HEAD')} at {sh('log', '-1', '--format=%h %s')[:90]}")
    ahead = sh("rev-list", "--count", "@{u}..HEAD") or "?"
    print(f"     {ahead} commit(s) not on GitHub" + (" -> SEND_RESULTS_TO_CLAUDE.bat pushes them" if ahead not in ("0", "?") else ""))
    dirty = [ln for ln in sh("status", "--porcelain").splitlines() if not ln.startswith("?? from_pc/")]
    print(f"     working tree: {'clean' if not dirty else f'{len(dirty)} change(s): ' + str(dirty[:5])}")
    if os.path.exists(os.path.join(REPO, ".git", "index.lock")):
        print("     WARNING .git/index.lock exists (blocks the sync) - delete it if no git is running")
    pv = version(os.path.join(REPO, "blender", "COD2EFT", "cod2eft_porter.py"), r"^VERSION = \((\d+, \d+, \d+)\)")
    uv = version(os.path.join(REPO, "unity", "EFTAutoPrefabber", "EFTToolsVersion.cs"), r'Version = "([^"]+)"')
    print(f"versions in repo: COD2EFT {pv.replace(', ', '.')}, EFT Tools {uv}")
    print("deployed COD2EFT (workspace):", compare(os.path.join(REPO, "blender", "COD2EFT"), os.path.join(WS, "COD2EFT")))
    unity = os.path.join(MNT, "WTT-SDK-2022", "Assets", "Editor", "EFTAutoPrefabber")
    print("deployed EFT Tools (Unity):  ", compare(os.path.join(REPO, "unity", "EFTAutoPrefabber"), unity))
    log = os.path.join(MNT, "Editor", "Editor.log")
    if os.path.isfile(log):
        txt = open(log, encoding="utf-8", errors="ignore").read()
        loads = re.findall(r"\[EFT Tools\] v(\S+) \(([^)]*)\) loaded", txt)
        errs = sorted(set(re.findall(r"^.*error CS\d+.*$", txt, re.M)))
        print(f"Unity Editor.log: last load v{loads[-1][0] if loads else '?'}; {len(errs)} distinct compile error(s)")
        for e in errs[:5]:
            print("   ", e[:160])
    else:
        print("Unity Editor.log: not mounted (request C:\\Users\\notso\\AppData\\Local\\Unity\\Editor)")
    sends = sorted(glob.glob(os.path.join(REPO, "from_pc", "*")))
    if sends:
        last = sends[-1]
        print(f"newest from_pc: {os.path.basename(last)} ({len(os.listdir(last))} item(s))"
              + ("  [not committed]" if sh("status", "--porcelain", last) else ""))
    reports = sorted(glob.glob(os.path.join(WS, "COD2EFT", "reports", "*.txt")), key=os.path.getmtime)
    if reports:
        print(f"newest conversion report: {os.path.basename(reports[-1])}")


if __name__ == "__main__":
    main()
