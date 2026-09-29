# Checks that the numbers both halves hard-code agree (docs/PROJECT_CONTEXT.md, queue item 2):
#   Blender preview  EFT_PART / EFT_NEUTRAL / CUTOFF   (blender/COD2EFT/cod2eft_textures.py)
#   Unity            Upper/Lower/Head/Hands presets, Cod2EftPreset, DefaultCutoff (EFTMaterialCore.cs)
# Plain Python, no bpy.  Exit code 1 when anything differs.   python tools/check_contract.py
import ast, os, re, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
PY = os.path.join(ROOT, "blender", "COD2EFT", "cod2eft_textures.py")
CS = os.path.join(ROOT, "unity", "EFTAutoPrefabber", "EFTMaterialCore.cs")


def py_consts():
    tree = ast.parse(open(PY, encoding="utf-8").read())
    out = {}
    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name) \
                and node.targets[0].id in ("EFT_PART", "EFT_NEUTRAL", "CUTOFF"):
            out[node.targets[0].id] = ast.literal_eval(node.value)
    return out


def fl(s):
    return [float(x.strip().rstrip("f")) for x in s.split(",")]


def cs_consts():
    src = open(CS, encoding="utf-8").read()
    presets = {}
    for name in ("Upper", "Lower", "Head", "Hands"):
        m = re.search(r"public static readonly EftPreset %s = new EftPreset\s*\{(.*?)\};" % name, src, re.S)
        body = m.group(1)
        presets[name] = {k: fl(re.search(k + r" = new float\[\] \{([^}]*)\}", body).group(1))
                         for k in ("SpecVals", "DefVals", "ReflectColor")}
    cod = {}
    for m in re.finditer(r"(case BodyPart\.(\w+)|default): p = (\w+)\.Clone\(\); p\.Glossness = ([\d.]+)f; "
                         r"p\.Specularness = ([\d.]+)f;", src):
        part = m.group(2) or "Upper"
        cod[part] = (float(m.group(4)), float(m.group(5)), m.group(3))
    cut = float(re.search(r"public const float DefaultCutoff = ([\d.]+)f;", src).group(1))
    # the enc=3 preset must stay "vanilla preset with G = S = 1"
    neutral_ok = bool(re.search(r"Cod2EftNeutralPreset\(BodyPart part\)\s*\{\s*var p = PresetFor\(part, false\)\.Clone\(\);"
                                r"\s*p\.Glossness = 1f;\s*p\.Specularness = 1f;", src))
    return presets, cod, cut, neutral_ok


def main():
    py = py_consts()
    presets, cod, cut, neutral_ok = cs_consts()
    bad = []

    def same(a, b):
        return all(abs(x - y) < 1e-6 for x, y in zip(a, b)) and len(a) == len(b)

    for part in ("Upper", "Lower", "Head", "Hands"):
        g, s, sv, dv = py["EFT_PART"][part]
        cg, cs_, base = cod[part]
        if base != part:
            bad.append(f"Cod2EftPreset {part} clones {base}")
        if not same((g, s), (cg, cs_)):
            bad.append(f"enc=2 {part}: Blender G/S {g}/{s}, Unity {cg}/{cs_}")
        if not same(sv, presets[part]["SpecVals"][:2]) or not same(dv, presets[part]["DefVals"][:2]):
            bad.append(f"enc=2 {part}: Blender SpecVals/DefVals {sv}/{dv}, Unity {presets[part]['SpecVals'][:2]}/"
                       f"{presets[part]['DefVals'][:2]}")
        ng, ns, nsv, ndv = py["EFT_NEUTRAL"][part]
        if not same((ng, ns), (1.0, 1.0)) or not same(nsv, presets[part]["SpecVals"][:2]) or \
                not same(ndv, presets[part]["DefVals"][:2]):
            bad.append(f"enc=3 {part}: Blender {py['EFT_NEUTRAL'][part]}, Unity G=S=1 SpecVals "
                       f"{presets[part]['SpecVals'][:2]} DefVals {presets[part]['DefVals'][:2]}")
    if not neutral_ok:
        bad.append("Cod2EftNeutralPreset is no longer 'PresetFor(part) with G = S = 1' - update this check and EFT_NEUTRAL")
    if abs(py["CUTOFF"] - cut) > 1e-6:
        bad.append(f"cutoff: Blender {py['CUTOFF']}, Unity {cut}")
    print("\n".join(bad) if bad else "OK: enc=2 and enc=3 values, SpecVals/DefVals and cutoff agree")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
