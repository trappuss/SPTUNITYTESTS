"""Check the enc=3 material classifier against hand labels (how 2.6.12 was measured, docs/MATERIALS_PLAN.md).

1. Convert with enc3 and keep the log:
       python tools/convert.py --out CONV FOLDERS... -- --material-mode enc3
2. Tiles of every COD material, cut from the converted atlas over its own faces (needs bpy):
       python tools/material_labels.py extract CONV WORK
3. Contact sheets to label by eye (the classifier's answer is hidden on purpose):
       python tools/material_labels.py sheets WORK            -> WORK/sheet_*.png, 42 tiles each
   Write labels as {"character|part|material": "skin|cloth|leather|metal|glass", ...}; add "?" for probable,
   "?" alone for unknown. Existing labels: docs/material_labels_2026-10-01.json (key "labels").
4. Score (no bpy):
       python tools/material_labels.py score WORK LABELS.json [LABELS2.json ...]
   Prints the confusion matrix, precision / recall per class and every miss, for sure labels and for sure + probable.
"""
import collections
import glob
import json
import os
import re
import sys

CLASSES = ["skin", "cloth", "leather", "metal", "glass"]
key_of = lambda r: f"{r['char']}|{r['part']}|{r['mat']}"


def extract(conv, work):
    import bpy
    import numpy as np
    from PIL import Image, ImageDraw
    os.makedirs(os.path.join(work, "tiles"), exist_ok=True)
    log = open(os.path.join(conv, "_convert_log.txt"), errors="ignore").read().splitlines()
    cls, char = {}, None
    for ln in log:
        m = re.search(r"===== (.+?) =====", ln)
        if m:
            char = m.group(1)
            continue
        m = re.search(r"\]   class (.+?): (\w+) \((.*?)\)(?:, gloss median ([\d.]+) -> smoothness ([\d.]+))?", ln)
        if m and char:
            cls.setdefault(char, {})[m.group(1)] = dict(cls=m.group(2), why=m.group(3), gloss=m.group(4))
    rows = []
    for blend in sorted(glob.glob(os.path.join(conv, "*_EFT.blend"))):
        char = os.path.basename(blend)[:-len("_EFT.blend")]
        bpy.ops.wm.open_mainfile(filepath=blend)
        for o in bpy.data.objects:
            if o.type != "MESH" or "cod2eft_orig_mats" not in o or "cod2eft_orig_mat" not in o.data.attributes:
                continue
            me = o.data
            names = json.loads(o["cod2eft_orig_mats"])
            nf = len(me.polygons)
            om = np.empty(nf, np.int32); me.attributes["cod2eft_orig_mat"].data.foreach_get("value", om)
            mi = np.empty(nf, np.int32); me.polygons.foreach_get("material_index", mi)
            lt = np.empty(nf, np.int32); me.polygons.foreach_get("loop_total", lt)
            ls = np.concatenate([[0], np.cumsum(lt)[:-1]])
            uv = np.empty(len(me.loops) * 2); me.uv_layers[0].data.foreach_get("uv", uv); uv = uv.reshape(-1, 2)
            slots = [m.name if m else "" for m in me.materials]
            atlas = {}
            for k in set(mi.tolist()):
                p = os.path.join(conv, (slots[k] if k < len(slots) else "") + "_d.png")
                if os.path.isfile(p):
                    atlas[k] = np.asarray(Image.open(p).convert("RGB"))[::-1]
            for j, nm in enumerate(names):
                sel = np.nonzero(om == j)[0]
                if not len(sel):
                    continue
                base = re.sub(r"\.\d{3}$", "", nm)
                k = int(np.bincount(mi[sel]).argmax())
                row = dict(char=char, part=o.name.rsplit("_", 1)[-1], mat=base, tile=None,
                           **cls.get(char, {}).get(base, {}))
                if k in atlas:
                    A = atlas[k]; H, W = A.shape[:2]
                    pts = np.concatenate([uv[ls[f]:ls[f] + lt[f]] for f in sel])
                    x0, x1 = np.clip(np.percentile(pts[:, 0], [1, 99]), 0, 1)
                    y0, y1 = np.clip(np.percentile(pts[:, 1], [1, 99]), 0, 1)
                    cx0, cy0 = int(x0 * W), int(y0 * H)
                    cx1, cy1 = max(int(x1 * W), cx0 + 2), max(int(y1 * H), cy0 + 2)
                    crop = A[cy0:cy1, cx0:cx1].copy()
                    mk = Image.new("L", (crop.shape[1], crop.shape[0]), 0); md = ImageDraw.Draw(mk)
                    for f in sel[:20000]:
                        md.polygon([(p[0] * W - cx0, p[1] * H - cy0) for p in uv[ls[f]:ls[f] + lt[f]]], fill=255)
                    mk = np.asarray(mk) > 0
                    crop[~mk] = (crop[~mk] * 0.15 + 60).astype(np.uint8)
                    row["tile"] = f"{len(rows):04d}.png"
                    Image.fromarray(np.ascontiguousarray(crop[::-1])).resize((150, 150)).save(
                        os.path.join(work, "tiles", row["tile"]))
                rows.append(row)
        print("done", char, len(rows), flush=True)
    json.dump(rows, open(os.path.join(work, "rows.json"), "w"), indent=0)


def sheets(work, per=42):
    from PIL import Image, ImageDraw
    rows = json.load(open(os.path.join(work, "rows.json")))
    idx = [i for i, r in enumerate(rows) if r.get("cls") != "cutout"]
    for s in range(0, len(idx), per):
        part = idx[s:s + per]
        im = Image.new("RGB", (6 * 154, ((len(part) + 5) // 6) * 180), (30, 30, 30))
        d = ImageDraw.Draw(im)
        for k, i in enumerate(part):
            r = rows[i]; x, y = (k % 6) * 154, (k // 6) * 180
            if r.get("tile"):
                im.paste(Image.open(os.path.join(work, "tiles", r["tile"])), (x, y))
            d.text((x + 2, y + 151), f"#{i} {r['part']} {r['char'][:14]}", fill=(255, 255, 0))
            d.text((x + 2, y + 164), r["mat"][-24:], fill=(200, 200, 200))
        out = os.path.join(work, f"sheet_{part[0]:04d}.png")
        im.save(out)
        print(out)


def score(work, label_files):
    rows = {key_of(r): r for r in json.load(open(os.path.join(work, "rows.json")))}
    labels = {}
    for f in label_files:
        d = json.load(open(f))
        labels.update(d.get("labels", d))
    for sure_only in (True, False):
        items = [(rows[k], v.rstrip("?")) for k, v in labels.items()
                 if v != "?" and k in rows and rows[k].get("cls") not in (None, "cutout")
                 and not (sure_only and v.endswith("?"))]
        conf = collections.Counter((t, r["cls"]) for r, t in items)
        print(f"\n== {'sure' if sure_only else 'sure + probable'} labels: {len(items)} ==")
        print("truth \\ got  " + " ".join(f"{c:>8s}" for c in CLASSES))
        for t in CLASSES:
            print(f"{t:12s} " + " ".join(f"{conf[(t, c)]:8d}" for c in CLASSES))
        for c in CLASSES:
            tp, pp, ap = conf[(c, c)], sum(conf[(t, c)] for t in CLASSES), sum(conf[(c, x)] for x in CLASSES)
            if pp or ap:
                print(f"  {c:8s} precision {tp}/{pp}" + (f" = {tp / pp:.0%}" if pp else "") +
                      f"   recall {tp}/{ap}" + (f" = {tp / ap:.0%}" if ap else ""))
        ok = sum(conf[(c, c)] for c in CLASSES)
        print(f"  accuracy {ok}/{len(items)}" + (f" = {ok / len(items):.0%}" if items else ""))
        if sure_only:
            for r, t in items:
                if r["cls"] != t:
                    print(f"  miss: {key_of(r)[:70]:70s} truth {t:7s} got {r['cls']} ({r.get('why')})")


if __name__ == "__main__":
    cmd, args = sys.argv[1], sys.argv[2:]
    {"extract": lambda: extract(*args[:2]), "sheets": lambda: sheets(args[0]),
     "score": lambda: score(args[0], args[1:])}[cmd]()
