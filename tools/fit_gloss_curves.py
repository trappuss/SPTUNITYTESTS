# Fit COD2EFT's enc=3 gloss curves (COD gloss -> EFT smoothness), one per material class, by
# quantile mapping: the COD distribution of a class (docs/cod_material_survey.json, from
# tools/cod_survey.py) onto the vanilla EFT smoothness distribution of that class (measured here
# from vanilla bundles, only texels the meshes' UVs cover, each material weighted equally).
#   python tools/fit_gloss_curves.py <folder with vanilla *.bundle> [survey.json]
# Writes docs/gloss_curves.json and prints the GLOSS_CURVES table for cod2eft_textures.py.
# Needs UnityPy, numpy, pillow.
import os, sys, glob, json
import numpy as np
import UnityPy
from PIL import Image, ImageDraw

R = 512
Q = np.linspace(0, 1, 21)
HERE = os.path.dirname(os.path.abspath(__file__))


def cov_mask(meshes):              # as tools/vanilla_stats.py
    m = Image.new("L", (R, R), 0); d = ImageDraw.Draw(m)
    for me in meshes:
        try: txt = me.export()
        except Exception: continue
        vt = []
        for ln in txt.splitlines():
            if ln.startswith("vt "):
                p = ln.split(); vt.append((float(p[1]) % 1.0, float(p[2]) % 1.0))
            elif ln.startswith("f "):
                ids = [int(t.split("/")[1]) - 1 for t in ln.split()[1:] if "/" in t and t.split("/")[1]]
                if len(ids) >= 3:
                    d.polygon([(vt[i][0] * R, (1 - vt[i][1]) * R) for i in ids], fill=255)
    return np.array(m) > 0


def skin(rgb):                     # Kovac et al. 2003 on 0-255 sRGB, as tools/vanilla_stats.py
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    mx = rgb.max(-1); mn = rgb.min(-1)
    return (r > 95) & (g > 40) & (b > 20) & (mx - mn > 15) & (np.abs(r - g) > 15) & (r > g) & (r > b)


def wq(vals, wts, q=Q):
    vals = np.asarray(vals, np.float64); wts = np.asarray(wts, np.float64)
    o = np.argsort(vals); cw = np.cumsum(wts[o]); cw /= cw[-1]
    return [float(vals[o][min(np.searchsorted(cw, x), len(vals) - 1)]) for x in q]


def vanilla(folder):
    """{class: [(texel smoothness array, material)]}"""
    out = {"cloth": [], "skin": [], "metal": []}
    for f in sorted(glob.glob(os.path.join(folder, "*.bundle"))):
        env = UnityPy.load(f)
        tex = {o.path_id: o for o in env.objects if o.type.name == "Texture2D"}
        cov = cov_mask([o.read() for o in env.objects if o.type.name == "Mesh"])
        for o in env.objects:
            if o.type.name != "Material": continue
            d = o.read_typetree(); P = d["m_SavedProperties"]; name = d["m_Name"]
            te = {k: v["m_Texture"]["m_PathID"] for k, v in P["m_TexEnvs"]}
            if not te.get("_MainTex") or te.get("_SpecMap") not in tex: continue
            fl = dict(P["m_Floats"]); S = fl.get("_Specularness", 1); G = fl.get("_Glossness", 1)
            main = np.asarray(tex[te["_MainTex"]].read().image.convert("RGBA").resize((R, R)), np.float32)
            sm = np.asarray(tex[te["_SpecMap"]].read().image.convert("RGBA").resize((R, R)), np.float32)[..., 0] / 255
            c = cov if cov.sum() > 1000 else np.ones((R, R), bool)
            a = main[..., 3] / 255; smooth = np.clip(sm * S, 0, 1)
            b = os.path.basename(f)
            if "head" in b.lower():
                # all covered texels: the skin rule keeps under 500 texels of the (dark) USEC head,
                # and heads are mostly skin; pooled median ~0.3 = the 393-material head median 0.29
                out["skin"].append((smooth[c], f"{b}:{name}"))
            elif G == 1.0 and name != "weapon_pr_taran":      # clothing (neutral values); the baton is gear
                out["cloth"].append((smooth[c & (a <= 0.5)], f"{b}:{name}"))
                hi = c & (a > 0.5)
                if hi.sum() > 200:
                    out["metal"].append((smooth[hi], f"{b}:{name}"))
    return out


def pooled_vanilla(items):
    vals = np.concatenate([v for v, _ in items])
    wts = np.concatenate([np.full(len(v), 1.0 / len(v)) for v, _ in items])
    return wq(vals, wts)


def pooled_cod(survey, cls):
    vals, wts, n = [], [], 0
    for r in survey:
        if cls == "metal":
            q, w = r.get("metal_gloss_q21"), r.get("metal_w", 0)
        else:
            q, w = (r.get("gloss_q21"), r.get("w", 0)) if r.get("cls") == cls else (None, 0)
        if not q or w <= 0: continue
        vals += q; wts += [w / len(q)] * len(q); n += 1
    return (wq(vals, wts), n) if vals else (None, 0)


def monotone(xs):
    xs = np.maximum.accumulate(np.asarray(xs, np.float64))
    for i in range(1, len(xs)):
        if xs[i] <= xs[i - 1]: xs[i] = xs[i - 1] + 1e-4
    return xs


def main():
    folder = sys.argv[1]
    survey = json.load(open(sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "..", "docs", "cod_material_survey.json")))
    van = vanilla(folder)
    V = {k: pooled_vanilla(v) for k, v in van.items()}
    # leather / rubber / plastic: no vanilla measurement yet (needs gloves / holsters bundles) -
    # hunch: the glossier half of vanilla clothing, where those pieces sit inside clothing atlases
    V["leather"] = [float(x) for x in np.interp(np.linspace(0.5, 1, 21), Q, V["cloth"])]
    res = {"note": "COD gloss -> EFT smoothness quantile maps; see docs/MATERIALS_PLAN.md (enc=3)",
           "vanilla_materials": {k: [n for _, n in v] for k, v in van.items()}, "curves": {}}
    for cls in ("cloth", "skin", "leather", "metal"):
        cq, n = pooled_cod(survey, cls)
        if cq is None:
            print("no COD samples for", cls); continue
        # knots at the 5 ... 95 % quantiles; the 0 / 100 % ones are single outliers (the USEC head's
        # _SpecMap x 4.37 clips to 1).  Ends: (0, 0), and above the 95th slope 1 (keeps the detail)
        xs = monotone([0.0] + list(cq[1:20]) + [1.0])
        v = list(np.maximum.accumulate(np.asarray(V[cls])))
        ys = np.array([0.0] + v[1:20] + [min(1.0, v[19] + (1.0 - xs[19]))])
        res["curves"][cls] = {"cod_materials": n, "cod_q": [round(x, 4) for x in xs],
                              "eft_q": [round(float(y), 4) for y in ys],
                              "cod_median": round(float(cq[10]), 3), "eft_median": round(float(V[cls][10]), 3)}
        print(f"{cls:8s} COD n={n:3d} median {cq[10]:.3f} (10% {cq[2]:.3f}, 90% {cq[18]:.3f})  ->  "
              f"EFT median {V[cls][10]:.3f} (10% {V[cls][2]:.3f}, 90% {V[cls][18]:.3f})")
    json.dump(res, open(os.path.join(HERE, "..", "docs", "gloss_curves.json"), "w"), indent=1)
    print("GLOSS_CURVES = {")
    for cls, c in res["curves"].items():
        print(f'    "{cls}": ({c["cod_q"]},\n              {c["eft_q"]}),')
    print("}")


if __name__ == "__main__":
    main()
