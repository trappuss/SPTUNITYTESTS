"""Contact sheet of COD2EFT Inspector screenshots (for reviewing a send cheaply).

    python tools/contact_sheet.py from_pc/<time>/COD2EFT_Screenshots [--out sheet.jpg] [--width 480]

Turntables (<time>_<outfit>_front/_left/_back/_right.png|jpg) become one row per outfit, other screenshots one row
each; every tile is labelled with its outfit name. Needs Pillow. Writes a JPEG next to the input unless --out is given.
"""
import argparse, os, re, sys
from PIL import Image, ImageDraw

ANGLES = ["front", "left", "back", "right"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--out")
    ap.add_argument("--width", type=int, default=480, help="tile width in pixels")
    a = ap.parse_args()
    files = sorted(f for f in os.listdir(a.folder) if f.lower().endswith((".png", ".jpg", ".jpeg")))
    rows = {}   # outfit -> {angle or "shot": path}
    for f in files:
        stem = os.path.splitext(f)[0]
        m = re.match(r"^\d{8}-\d{6}_(.*?)(?:_(front|left|back|right))?$", stem)
        outfit, angle = (m.group(1), m.group(2)) if m else (stem, None)
        key = outfit if angle else stem
        rows.setdefault(key, {})[angle or "shot"] = os.path.join(a.folder, f)
    if not rows:
        sys.exit("no screenshots in " + a.folder)
    tw = a.width
    tiles = []
    for key, shots in rows.items():
        order = [s for s in ANGLES if s in shots] or ["shot"]
        tiles.append((key, [(s, shots[s]) for s in order]))
    first = Image.open(tiles[0][1][0][1])
    th = round(tw * first.height / first.width)
    label = 18
    cols = max(len(t[1]) for t in tiles)
    sheet = Image.new("RGB", (cols * tw, len(tiles) * (th + label)), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    for r, (key, shots) in enumerate(tiles):
        y = r * (th + label)
        d.text((4, y + 2), key[:150], fill=(230, 230, 230))
        for c, (angle, path) in enumerate(shots):
            im = Image.open(path).convert("RGB")
            im.thumbnail((tw, th))
            sheet.paste(im, (c * tw, y + label))
            if angle != "shot":
                d.text((c * tw + 4, y + label + 4), angle, fill=(255, 220, 80))
    out = a.out or os.path.join(a.folder, "_contact_sheet.jpg")
    sheet.save(out, quality=88)
    print(f"{out}: {len(tiles)} row(s), {sum(len(t[1]) for t in tiles)} image(s)")


if __name__ == "__main__":
    main()
