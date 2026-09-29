import sys, os, json, glob
sys.path.insert(0, "/tmp/c2e_addon")
import bpy, numpy as np
import cod2eft
from cod2eft import cod2eft_porter as C, cod2eft_textures as TX
from PIL import Image, ImageDraw
ROOT = "/home/user/SPTUNITYTESTS/from_pc/20260928-223033/attached"
models = sorted(p for p in glob.glob(ROOT + "/**/*_LOD0.cast", recursive=True))
out, thumbs = [], []
W = 256
for mp in models:
    bpy.ops.wm.read_factory_settings(use_empty=True); cod2eft.register() if not hasattr(bpy.types.Scene, "cod2eft") else None
    try:
        objs = C.import_model(mp, log=lambda *a: None)
    except Exception as e:
        print("IMPORT FAIL", mp, e); continue
    model = os.path.basename(os.path.dirname(mp))
    seen = {}
    for o in objs:
        if o.type != "MESH" or not o.data.uv_layers: continue
        me = o.data
        uv = np.empty(len(me.loops) * 2); me.uv_layers[0].data.foreach_get("uv", uv); uv = uv.reshape(-1, 2)
        mi = np.empty(len(me.polygons), int); me.polygons.foreach_get("material_index", mi)
        ls = np.empty(len(me.polygons), int); me.polygons.foreach_get("loop_start", ls)
        lt = np.empty(len(me.polygons), int); me.polygons.foreach_get("loop_total", lt)
        area = np.empty(len(me.polygons)); me.polygons.foreach_get("area", area)
        fid = np.repeat(np.arange(len(mi)), lt)
        cen = np.stack([np.bincount(fid, uv[:, k]) for k in (0, 1)], 1) / lt[:, None]
        for idx, mat in enumerate(me.materials):
            if mat is None: continue
            faces = np.nonzero(mi == idx)[0]
            if not len(faces): continue
            key = TX.base_name(mat.name)
            pts = cen[faces] % 1.0; wts = area[faces]
            if key in seen: continue
            roles, how = TX.find_textures(mat, mp)
            if roles.get("fused"):
                n = TX.classify_alpha(roles, pts, wts)
                if n: how.append(n)
            try:
                d, nn, g, info = TX.material_maps(roles, W, W)
            except Exception as e:
                print("maps fail", key, e); continue
            r = np.clip((pts[:, 1] * W).astype(int), 0, W - 1); c = np.clip((pts[:, 0] * W).astype(int), 0, W - 1)
            def wq(a):
                o_ = np.argsort(a); cw = np.cumsum(wts[o_]) / wts.sum()
                return [round(float(a[o_][np.searchsorted(cw, q)]), 3) for q in (0.1, 0.5, 0.9)]
            spec = d[r, c, 3]; gl = g[r, c, 0]; rgb = d[r, c, :3]
            rec = {"i": len(out), "model": model, "mat": key, "faces": int(len(faces)), "area": round(float(wts.sum()), 4),
                   "alpha_kind": roles.get("alpha_kind"), "cutout": bool(roles.get("opacity_cands")),
                   "spec_q": wq(spec), "gloss_q": wq(gl), "rgb_mean": [round(float(x), 3) for x in np.average(rgb, 0, wts)],
                   "how": how}
            seen[key] = rec; out.append(rec)
            im = Image.fromarray((np.clip(d[::-1, :, :3], 0, 1) ** (1 / 1.0) * 255).astype(np.uint8)).resize((128, 128))
            thumbs.append((rec["i"], im))
            print(f'{rec["i"]:3d} {model[:28]:28s} {key[:30]:30s} {str(rec["alpha_kind"]):12s} cut={int(rec["cutout"])} spec{rec["spec_q"]} gloss{rec["gloss_q"]}')
json.dump(out, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "cod_material_survey.json", "w"), indent=1)
cols = 8; rows = (len(thumbs) + cols - 1) // cols
sheet = Image.new("RGB", (cols * 132, rows * 146), (30, 30, 30)); dr = ImageDraw.Draw(sheet)
for k, (i, im) in enumerate(thumbs):
    x, y = (k % cols) * 132 + 2, (k // cols) * 146 + 2
    sheet.paste(im, (x, y + 14)); dr.text((x, y), str(i), fill=(255, 255, 0))
sheet.save("/tmp/claude-0/-home-user-SPTUNITYTESTS/cb8e3e4b-fb79-5945-9a40-c987e6f580f4/scratchpad/cod_sheet.png")
print("models:", len(models), "materials:", len(out))
