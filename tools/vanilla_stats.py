import os, UnityPy, glob, json, sys, numpy as np
from PIL import Image, ImageDraw
R = 512
def cov_mask(meshes):
    m = Image.new("L", (R, R), 0); d = ImageDraw.Draw(m)
    for me in meshes:
        try: txt = me.export()
        except Exception: continue
        vt = []; 
        for ln in txt.splitlines():
            if ln.startswith("vt "):
                p = ln.split(); vt.append((float(p[1]) % 1.0, float(p[2]) % 1.0))
            elif ln.startswith("f "):
                ids = [int(t.split("/")[1]) - 1 for t in ln.split()[1:] if "/" in t and t.split("/")[1]]
                if len(ids) >= 3:
                    d.polygon([(vt[i][0] * R, (1 - vt[i][1]) * R) for i in ids], fill=255)
    return np.array(m) > 0
def q(a): return [round(float(x), 3) for x in np.quantile(a, [0.1, 0.5, 0.9])] if a.size else None
def skin(rgb):   # Kovac et al. 2003 skin rule on 0-255 sRGB
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    mx = rgb.max(-1); mn = rgb.min(-1)
    return (r > 95) & (g > 40) & (b > 20) & (mx - mn > 15) & (np.abs(r - g) > 15) & (r > g) & (r > b)
out = {}
for f in sorted(glob.glob("*.bundle")):
    env = UnityPy.load(f)
    tex = {o.path_id: o for o in env.objects if o.type.name == "Texture2D"}
    meshes = [o.read() for o in env.objects if o.type.name == "Mesh"]
    cov = cov_mask(meshes)
    for o in env.objects:
        if o.type.name != "Material": continue
        d = o.read_typetree(); P = d["m_SavedProperties"]
        te = {k: v["m_Texture"]["m_PathID"] for k, v in P["m_TexEnvs"]}
        if not te.get("_MainTex"): continue
        fl = dict(P["m_Floats"]); co = {k: (v["r"], v["g"], v["b"], v["a"]) for k, v in P["m_Colors"]}
        main = np.asarray(tex[te["_MainTex"]].read().image.convert("RGBA").resize((R, R)), np.float32)
        spm = np.asarray(tex[te["_SpecMap"]].read().image.convert("RGBA").resize((R, R)), np.float32) if te.get("_SpecMap") in tex else None
        c = cov if cov.sum() > 1000 else np.ones((R, R), bool)
        rgb = main[..., :3]; a = main[..., 3] / 255; sm = spm[..., 0] / 255 if spm is not None else None
        G = fl.get("_Glossness", 1); S = fl.get("_Specularness", 1)
        SV = co.get("_SpecVals", (1, 1, 0, 0)); DV = co.get("_DefVals", (1, 0, 0, 0)); RC = co.get("_ReflectColor", (0, 0, 0, 0))
        k0 = SV[0] / 2
        sk = skin(rgb) & c; rest = c & ~sk
        rec = {"G": round(G, 3), "S": round(S, 3), "SpecVals": [round(x, 3) for x in SV[:2]], "DefVals": [round(x, 3) for x in DV[:2]],
               "Reflect": round(RC[0], 3), "cover": round(float(c.mean()), 3), "skin_frac": round(float(sk.sum() / max(c.sum(), 1)), 3),
               "alpha_q": q(a[c]), "smap_q": q(sm[c]) if sm is not None else None,
               "spec_eff_q": q(a[c] * G * k0), "smooth_eff_q": q(np.clip(sm[c] * S, 0, 1)) if sm is not None else None}
        if sk.sum() > 500 and sm is not None:
            rec["skin_spec_eff_q"] = q(a[sk] * G * k0); rec["skin_smooth_eff_q"] = q(np.clip(sm[sk] * S, 0, 1))
            rec["cloth_spec_eff_q"] = q(a[rest] * G * k0); rec["cloth_smooth_eff_q"] = q(np.clip(sm[rest] * S, 0, 1))
        hi = c & (a > 0.5)
        if hi.sum() > 200 and sm is not None:
            rec["metalish_frac"] = round(float(hi.sum() / c.sum()), 3); rec["metalish_smooth_eff_q"] = q(np.clip(sm[hi] * S, 0, 1))
        out[f"{f}:{d['m_Name']}"] = rec
        print(f"{d['m_Name'][:34]:34s} G{G:<5.2f} S{S:<5.2f} SV{SV[0]:.2f},{SV[1]:.2f} cov{rec['cover']:.2f} skin{rec['skin_frac']:.2f} "
              f"spec_eff{rec['spec_eff_q']} smooth_eff{rec['smooth_eff_q']}" + (f" | skin spec{rec['skin_spec_eff_q']} smooth{rec['skin_smooth_eff_q']}" if "skin_spec_eff_q" in rec else "")
              + (f" | a>0.5 {rec['metalish_frac']:.3f} smooth{rec['metalish_smooth_eff_q']}" if "metalish_frac" in rec else ""))
json.dump(out, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "vanilla_material_stats.json"), "w"), indent=1)
