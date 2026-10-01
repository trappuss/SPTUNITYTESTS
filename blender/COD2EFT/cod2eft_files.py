"""
COD2EFT - input file discovery and grouping (pure Python, no Blender needed).

Turns a list of dropped files/folders into "characters": each character is the set of model
files that are converted together into one output scene.

Rules (all derived from how Greyhound/Saluki name COD exports):
  * One export = <stem>/<stem>_LOD0.<ext>.  When both .fbx and .cast exist, one is picked.
  * First-person models are skipped: vm_*, *_vm_*, *viewarms*, *viewbody*, *viewlegs*
    (their armature has tag_view as well - checked again after import).
  * Body parts / full bodies / heads are grouped by a key = the file stem with the part and
    quality tokens removed (body_, head_, rex_, torso, lowerbody, arms, head, lod1, high, fe,
    blendshape, pc ...).  body_X + head_X, and T9 *_torso/_lowerbody/_arms/_head, share a key.
  * Head alternatives of one character (head_X, head_X_high, head_X_lod1, head_X_blendshape):
    ONE is used - the standard one if present, else high, else lod1, else blendshape.
  * If a folder holds exactly one body character and exactly one head-only character with a
    different name (e.g. Vanguard: body "arthur", head "okonkwo"), they are paired.
  * Anything else (several bodies + several heads with unrelated names, e.g. the MW2019
    folder) is converted separately - a head on its own becomes a Head-only output.
"""
import os
import re

MODEL_EXT = (".fbx", ".cast")
SKIP_DIRS = {"eft_converted", "_images", "_mat_info"}

FIRSTPERSON_RE = re.compile(r"(^|_)(vm|viewarms\d*|viewbody\d*|viewlegs\d*|viewhands\d*)(_|$)")
HEAD_RE = re.compile(r"(^|_)head\d*(_|$)")
ACCESSORY_RE = re.compile(r"(^|_)parts(_|$)")
# tokens removed to build the character key
STRIP_TOKENS = re.compile(
    r"(^|_)(rex|body|head\d*|torso\d*|lowerbody\d*|upperbody\d*|arms\d*|legs\d*|fb\d*|"
    r"high|fe|blendshape|lod\d+|pc)(?=_|$)")
VARIANT_ORDER = ("std", "high", "lod", "blendshape")


def stem_of(path):
    s = os.path.splitext(os.path.basename(path))[0]
    return re.sub(r"_lod0$", "", s, flags=re.I)


def kind_of(stem):
    s = stem.lower()
    if FIRSTPERSON_RE.search(s):
        return "firstperson"
    if ACCESSORY_RE.search(s):
        return "accessory"
    if HEAD_RE.search(s):
        return "head"
    return "body"


def variant_of(stem):
    s = stem.lower()
    if "blendshape" in s:
        return "blendshape"
    if re.search(r"(^|_)high(_|$)", s):
        return "high"
    if re.search(r"(^|_)lod[1-9]\d*(_|$)", s):
        return "lod"
    return "std"


def key_of(stem):
    s = stem.lower()
    prev = None
    while prev != s:
        prev = s
        s = STRIP_TOKENS.sub(r"\1", s)
        s = re.sub(r"__+", "_", s).strip("_")
    return s


def char_dir_of(path):
    """Folder that holds the character's model folders (the parent of <stem>/ when the file sits
    in its own <stem> folder, as Greyhound/Saluki export it)."""
    d = os.path.dirname(os.path.abspath(path))
    if os.path.basename(d).lower() == stem_of(path).lower():
        return os.path.dirname(d)
    return d


PART_RE = re.compile(r"(^|_)(torso|lowerbody|upperbody|arms|legs|fb)\d*(_|$)")


def part_of(stem):
    m = PART_RE.search(stem.lower())
    return m.group(2) if m else "body"


def skeleton_sniff(path):
    """Look at the bone names stored in the file itself (FBX and Cast keep them as plain
    strings).  Returns 'character', 'viewmodel' or 'other'."""
    try:
        with open(path, "rb") as fh:
            data = fh.read()
    except OSError:
        return "other"
    if b"tag_view" in data:
        return "viewmodel"
    if b"j_spine4" in data or b"j_mainroot" in data or b"j_head" in data:
        return "character"
    return "other"


def written_by_blender(path):
    """True when an FBX was saved by Blender (a COD export is written by Greyhound / Saluki / Cast tools)."""
    try:
        with open(path, "rb") as fh:
            return b"Blender (stable FBX IO)" in fh.read(4096)
    except OSError:
        return False


def other_format(path):
    """The same export in the other format (<stem>.cast for <stem>.fbx and back), or None."""
    base, ext = os.path.splitext(path)
    for e in MODEL_EXT:
        if e != ext.lower() and os.path.isfile(base + e):
            return base + e
    return None


def discover(inputs, prefer_cast=False, log=print):
    files = []
    for p in inputs:
        p = p.strip().strip('"')
        if os.path.isdir(p):
            for root, dirs, fs in os.walk(p):
                dirs[:] = sorted(d for d in dirs if d.lower() not in SKIP_DIRS)
                for f in sorted(fs):
                    if f.lower().endswith(MODEL_EXT):
                        files.append(os.path.join(root, f))
        elif os.path.isfile(p) and p.lower().endswith(MODEL_EXT):
            files.append(p)
        else:
            log(f"skipping (not a .fbx/.cast file or folder): {p}")
    # one format per export
    by_stem = {}
    for f in files:
        by_stem.setdefault(os.path.splitext(os.path.abspath(f))[0].lower(), []).append(f)
    want = ".cast" if prefer_cast else ".fbx"
    chosen = []
    for fs in by_stem.values():
        pick = [f for f in fs if f.lower().endswith(want)] or sorted(fs)
        chosen.append(pick[0])
    return sorted(chosen)


FP_TOKENS = re.compile(r"(^|_)(vm|view(arms|body|legs)\d*)(?=_|$)")


def fp_arms_rank(stem):
    """First-person model usable for first-person hands: 0 = arms only (vm_arms / viewarms /
    vm_...), 1 = the arms piece of a first-person body (..._arms_viewbody), None = not arms
    (first-person legs, torso, lower body)."""
    s = stem.lower()
    if re.search(r"(^|_)viewlegs\d*(_|$)", s):
        return None
    if re.search(r"(^|_)viewbody\d*(_|$)", s):
        return 1 if re.search(r"(^|_)arms\d*(_|$)", s) else None
    return 0


def fp_key_of(stem):
    s = key_of(stem)
    prev = None
    while prev != s:
        prev = s
        s = FP_TOKENS.sub(r"\1", s)
        s = re.sub(r"__+", "_", s).strip("_")
    return s


def group(files, log=print):
    """Returns (characters, skipped).  characters: list of dict(key, dir, bodies, head, fp) -
    fp = the character's first-person arms model (same folder, same name without the
    vm / viewarms tokens) or None."""
    skipped = []
    chars = {}
    fp_files = []
    for f in files:
        st = stem_of(f)
        k = kind_of(st)
        sn = skeleton_sniff(f)
        if sn == "other":
            # 2.6.13: a source FBX that was overwritten (e.g. a converted export saved over body_X_LOD0.fbx) has no COD
            # bones; its .cast twin from the same export still has them, so use that instead of dropping the part
            alt = other_format(f)
            if alt and skeleton_sniff(alt) != "other":
                why = " (it was saved by Blender - probably overwritten by an export)" if written_by_blender(f) else ""
                log(f"{os.path.basename(f)} has no COD skeleton{why}: using {os.path.basename(alt)} instead")
                f, sn = alt, skeleton_sniff(alt)
            else:
                skipped.append((f, "no COD character skeleton in the file" +
                                (" - it was saved by Blender, probably overwritten by an export; re-export it from the game"
                                 if written_by_blender(f) else "")))
                continue
        if sn == "viewmodel" and k != "firstperson":
            k = "firstperson"
        if k in ("firstperson", "accessory"):
            skipped.append((f, "first-person model" if k == "firstperson" else
                            "accessory part (no body skeleton)"))
            if k == "firstperson":
                fp_files.append(f)
            continue
        key = key_of(st)
        cd = char_dir_of(f)
        c = chars.setdefault((cd, key), dict(key=key, dir=cd, bodies=[], heads=[]))
        (c["heads"] if k == "head" else c["bodies"]).append(f)

    # pair a lone head-only character with a lone body character in the same folder
    by_dir = {}
    for (cd, key), c in chars.items():
        by_dir.setdefault(cd, []).append(c)
    for cd, cs in by_dir.items():
        bodies = [c for c in cs if c["bodies"]]
        heads_only = [c for c in cs if not c["bodies"] and c["heads"]]
        if len(bodies) == 1 and len(heads_only) == 1 and not bodies[0]["heads"]:
            bodies[0]["heads"] = heads_only[0]["heads"]
            log(f"paired head '{heads_only[0]['key']}' with body '{bodies[0]['key']}' "
                f"(only body + only head in {os.path.basename(cd)})")
            heads_only[0]["heads"] = []

    out = []
    for c in chars.values():
        if not c["bodies"] and not c["heads"]:
            continue
        head = None
        if c["heads"]:
            ranked = sorted(c["heads"], key=lambda f: (VARIANT_ORDER.index(variant_of(stem_of(f))), f))
            head = ranked[0]
            for h in ranked[1:]:
                skipped.append((h, f"alternative head (using {os.path.basename(head)})"))
        c["head"] = head
        # the same part exported twice (e.g. T9 *_torso and *_torso_pc) -> keep one
        seen = {}
        for b in sorted(c["bodies"], key=lambda f: (len(stem_of(f)), f)):
            prt = part_of(stem_of(b))
            if prt in seen:
                skipped.append((b, f"duplicate '{prt}' part (using {os.path.basename(seen[prt])})"))
            else:
                seen[prt] = b
        c["bodies"] = sorted(seen.values())
        # first-person arms of this character (for first-person hands)
        cands = []
        for f in fp_files:
            r = fp_arms_rank(stem_of(f))
            if r is not None and char_dir_of(f) == c["dir"] and fp_key_of(stem_of(f)) == c["key"]:
                cands.append((r, variant_of(stem_of(f)) != "std", f))
        c["fp"] = sorted(cands)[0][2] if cands else None
        out.append(c)
    out.sort(key=lambda c: (c["dir"], c["key"]))
    return out, skipped


if __name__ == "__main__":
    import sys
    fs = discover(sys.argv[1:], prefer_cast="--cast" in sys.argv)
    chars, skipped = group(fs)
    for c in chars:
        print(f"== {c['key']}   [{os.path.basename(c['dir'])}]")
        for b in c["bodies"]:
            print("     body:", os.path.basename(b))
        if c.get("fp"):
            print("     first-person arms:", os.path.basename(c["fp"]))
        if c["head"]:
            print("     head:", os.path.basename(c["head"]))
    print("-- skipped:")
    for f, why in skipped:
        print(f"     {os.path.basename(f)}: {why}")
