"""
COD2EFT - textures: turn the COD material textures of each converted part (Head / Upper / Lower)
into ONE EFT texture set per part, laid out in an atlas:

    <name>_<Part>_d.png   RGB = diffuse colour, A = specular reflectance F0 -> _MainTex
    <name>_<Part>_n.png   tangent-space normal map, OpenGL / Unity (Y+)   -> _BumpMap (import as
                                                                             "Normal map")
    <name>_<Part>_g.png   gloss = Unity smoothness (white = glossy)       -> _SpecMap
    (+ <name>_<Part>_alpha_*.png for hair / lashes / fur cards that need a cut-out shader)
Each PNG carries a tEXt chunk "COD2EFT enc=2" (see ENCODING / png_mark) so the Unity side (the
EFT Auto Prefabber) can recognise the files - see docs/COD2EFT_TEXTURE_SPEC.md (the contract).

What EFT does with them - read from EFT's character shaders, "p0/Reflective/Bumped Specular
SMap(_Decal)" (decompiled copies in the WTT-SDK).  Their deferred pass writes Unity's G-buffer:
colour = _MainTex.rgb x (_DefVals.x + _DefVals.y x F), specular = _MainTex.a x _Glossness x
(_SpecVals.x + _SpecVals.y x F) / 2, smoothness = _SpecMap.r x _Specularness, with
F = (1 - N.V)^2 / 2.  EFT renders in Gamma colour space with its own deferred lighting shader,
so the final look of these values is not known yet.  The Unity side (the EFT Auto Prefabber's
material fixer) sets the shader values, as docs/COD2EFT_TEXTURE_SPEC.md specifies.

What COD provides (checked on the test exports of every game):
  * Infinity Ward engine games (MW2019, Vanguard, MW2, MW3, BO6, BO7, MW4 beta):
      colour map "_c&_s" - RGB colour, A = specular reflectance / metal mask (fused)
      "NOG" map  "_n&_g" - R = gloss, G / A = normal X / Y (hemi-octahedron), B = occlusion
      (layout as in Scobalula's GameImageUtil "CoD Normal/Gloss/Occlusion" processor)
      The colour alpha is only a metal mask where it is (near) two-level; on skin it is a
      smooth map in between and is not used as specular (classify_alpha).
      Greyhound "split" exports (MW2019 test characters) have X_n / X_g / X_o files instead
      of the NOG and a fused PNG without alpha (_split_nog, _split_alpha).
  * Cold War (T9): separate colorMap, normalMap (R/G = X/Y), glossMap, specColorMap, aoMap
  * COD normal maps are DirectX style (green = down): a height-field test (normal_is_directx)
    gives the DirectX sign on 77 of the 81 COD maps of the test characters at full size (the 4
    others flip sign with the scale - rotated / mirrored UV islands confuse the test) and the
    OpenGL sign on all of EFT's own maps in the template, whose shader unpacks them the standard
    Unity way.  So every COD map gets its green flipped (not decided per map); the "DirectX"
    normal style option flips it back for anyone who wants DirectX maps.
Layout ("used parts only", the default): each COD texture is cut down to the rectangles its UV
islands use (uv_rects), and every texture set is drawn at the same texel density on the model
(atlas px per metre, never above the COD texture's own resolution); plan_atlas finds the largest
density at which all rectangles pack.  "Whole textures" puts each COD texture in whole, sized by
the material's surface area (the older layout).
Occlusion: multiplied into the colour (ao_strength) and, by default, into the specular too - the
EFT character shader has no occlusion slot.

Which image is which is read from the exporter's files (_mat_info/*.txt, *_images.txt, *.mtl,
the image the importer put on the material) and, where those only have hashed names, from the
image content (a NOG map has B ~ 0.9 and G / A ~ 0.5).
"""
import os
import re
import glob
import math
import json
import numpy as np
import bpy

IMG_EXT = (".png", ".dds", ".tga", ".tif", ".tiff", ".jpg", ".jpeg", ".exr")
# Texture encoding written by this version ("enc=2", stored in each PNG as a tEXt chunk so the
# Unity side knows which encoding it gets - docs/COD2EFT_TEXTURE_SPEC.md):
#   _MainTex.a = specular reflectance F0 (linear, x "Specular strength"),  _SpecMap.r = gloss.
# The Unity side sets the shader values (docs/COD2EFT_TEXTURE_SPEC.md).
# For reference, EFT's own character materials put into the G-buffer: specular median 0.05
# (cloth and heads), smoothness median 0.22 (cloth), 0.30 (heads) - measured over 422 materials.
ENCODING = 2
# Multiplier on the COD specular reflectance written to _MainTex.a (1 = COD's own value)
SPEC_SCALE = 1.0
# Specular reflectance of a non-metal when the COD material gives none (Unity's / COD's usual
# dielectric default, and about EFT's own median, 0.05)
F0_DIELECTRIC = 0.04
# How much of a metal's colour stays in the diffuse (the rest is taken out, as COD's metal colour
# is its specular colour).  Calibrated: in EFT's own gear textures (51 diffuse maps from the
# game's equipment bundles) the high-specular (metal) areas have a median diffuse luminance of
# 0.35 - brighter than the rest (0.19), not black; COD's metal areas (48 materials of the test
# characters) have a median colour luminance of 0.50.  0.35 / 0.50 = 0.7.
METAL_KEEP = 0.7
ALPHA_WORDS = ("hair", "beard", "brow", "lash", "fur", "fuzz", "stubble", "card", "tearline",
               "tear_line", "eyeao", "eye_ao", "eyeshadow", "cornea", "lens", "glass", "caustic")
# Cut-out (opacity) maps.  EFT's hair (p0/Cutout/Bumped Diffuse: Big Pipe, Birdeye, Partizan)
# keeps the cut-out in the ALPHA of _MainTex (DXT5), _Cutoff 0.48 - 0.87, no spec map, back
# faces culled.  COD keeps it in a separate single-channel map, found per game from the test
# exports (hair / lash / brow / beard / fringe / fray cards, checked by eye):
#   Cold War (T9):              named "alphaMap" (or the colour map's alpha on hair cards)
#   MW2019 / Vanguard / MW2 / MW3 (hashed "unk_semantic_0x..."): 0xC, 0x1B, 0x1C
#   BO6 / BO7 / MW4 beta (hashed "unk_semantic_.."):             49, 4a
# The same slots also hold other masks on other materials (a face's detail mask, a weapon's
# strap mask ...), so a candidate is only used when it really cuts the mesh like cards do:
# see _pick_opacity.
OPACITY_NAMED = re.compile(r"^(alpha|opacity|transparency)(map)?$", re.I)
OPACITY_SLOTS = {"unk_semantic_0xc", "unk_semantic_0x1b", "unk_semantic_0x1c",
                 "unk_semantic_49", "unk_semantic_4a"}
# Alpha cutoff of the cut-out material (Unity's default, EFT Tools 1.7.1; the preview uses it).  EFT's
# own hair uses 0.48 - 0.87, but COD's cards are softer: at 0.5 valeria (MW4) lost 7 - 11 % of the
# texels with alpha >= 0.25 and fine hair / fringe details vanished in Unity and in game; the user
# found 0.25 - 0.35 best.  (2.6.1; was 0.5)
CUTOFF = 0.3
# Threshold of the cut-out detection test (_pick_opacity) - kept at 0.5 so which materials become
# cut-outs does not change with the render cutoff
PICK_CUTOFF = 0.5
SKIP_WORDS = ("thermal", "heat", "_lut", "sheen", "velvet", "reveal", "emissive", "wetness",
              "cube", "irradiance", "caustic_l")


def base_name(n):
    return re.sub(r"\.\d{3}$", "", n)


# ---------------------------------------------------------------------------------------------
# image IO (Blender only - no PIL in Blender's Python).  Arrays are float32 (H, W, 4), row 0 =
# BOTTOM of the image (Blender / UV convention), values = the raw file values 0..1.
# ---------------------------------------------------------------------------------------------
_CACHE = {}

# Windows paths of 260 characters or more (MAX_PATH) can't be opened by Blender or by plain Python
# file calls unless long paths are switched on in Windows.  COD exports get there easily (deep
# test folders + long packed image names): the Park 24_1 test character's blouse-button decal is
# 269 characters, and its conversion stopped there.  Python can still reach such a file through
# the Win32 long-path prefix; Blender gets a copy under a short temporary path (short_path).
IS_WIN = os.name == "nt"
LONG_PATH = 248                   # leave room: Blender / Windows add to the path internally
_SHORT = {}


def io_path(path):
    """`path` as Python file calls can open it: on Windows, long paths get the long-path prefix."""
    if not IS_WIN or not path or len(path) < LONG_PATH or path.startswith("\\\\?\\"):
        return path
    return "\\\\?\\" + os.path.abspath(path).replace("/", "\\")


def is_file(path):
    return bool(path) and os.path.isfile(io_path(path))


def short_path(path):
    """A path Blender can load `path` from: itself, or (a long Windows path) a copy of the file
    in the temp folder under a short name, made once per session."""
    if not IS_WIN or not path or len(path) < LONG_PATH:
        return path
    got = _SHORT.get(path)
    if got and os.path.isfile(got):
        return got
    import hashlib
    import shutil
    import tempfile
    d = os.path.join(tempfile.gettempdir(), "cod2eft_long_paths")
    os.makedirs(d, exist_ok=True)
    dst = os.path.join(d, hashlib.sha1(path.encode("utf-8")).hexdigest()[:16] +
                       os.path.splitext(path)[1])
    shutil.copyfile(io_path(path), dst)
    _SHORT[path] = dst
    return dst


def load_image(path, max_size=None):
    key = (path, max_size)
    if key in _CACHE:
        return _CACHE[key]
    im = bpy.data.images.load(short_path(path), check_existing=False)
    try:
        im.colorspace_settings.name = "Non-Color"
    except TypeError:
        pass
    w, h = im.size
    if w == 0 or h == 0:
        bpy.data.images.remove(im)
        raise RuntimeError(f"could not read {path}")
    px = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(px)
    bpy.data.images.remove(im)
    a = px.reshape(h, w, 4)
    if max_size and max(w, h) > max_size:
        f = max(w, h) / max_size
        a = resize(a, max(1, int(round(w / f))), max(1, int(round(h / f))))
    if max_size and max_size <= 256:              # only small previews are kept
        if len(_CACHE) > 200:
            _CACHE.clear()
        _CACHE[key] = a
    return a


def clear_cache():
    _CACHE.clear()


def image_size(path):
    """(width, height) from the file header (PNG / DDS), else via Blender."""
    try:
        with open(io_path(path), "rb") as fh:
            h = fh.read(32)
        if h[:8] == b"\x89PNG\r\n\x1a\n":
            return int.from_bytes(h[16:20], "big"), int.from_bytes(h[20:24], "big")
        if h[:4] == b"DDS ":
            return int.from_bytes(h[16:20], "little"), int.from_bytes(h[12:16], "little")
    except OSError:
        pass
    a = load_image(path, max_size=128)
    return a.shape[1], a.shape[0]


def resize(a, W, H):
    """Area-average down / bilinear up resize of (h, w, c) float arrays."""
    h, w = a.shape[:2]
    if (w, h) == (W, H):
        return a
    fy, fx = max(1, h // max(H, 1)), max(1, w // max(W, 1))
    if fy > 1 or fx > 1:
        hh, ww = h // fy * fy, w // fx * fx
        a = a[:hh, :ww].reshape(hh // fy, fy, ww // fx, fx, -1).mean(axis=(1, 3))
        h, w = a.shape[:2]
    if (w, h) == (W, H):
        return a.astype(np.float32)
    ys = np.clip((np.arange(H) + 0.5) * h / H - 0.5, 0, h - 1)
    xs = np.clip((np.arange(W) + 0.5) * w / W - 0.5, 0, w - 1)
    y0 = np.floor(ys).astype(int)
    x0 = np.floor(xs).astype(int)
    y1 = np.minimum(y0 + 1, h - 1)
    x1 = np.minimum(x0 + 1, w - 1)
    wy = (ys - y0)[:, None, None]
    wx = (xs - x0)[None, :, None]
    top = a[y0][:, x0] * (1 - wx) + a[y0][:, x1] * wx
    bot = a[y1][:, x0] * (1 - wx) + a[y1][:, x1] * wx
    return (top * (1 - wy) + bot * wy).astype(np.float32)


def save_png(path, arr, srgb=True, mark=None):
    """arr: (H, W, 1|3|4) float 0..1, row 0 = bottom.  mark: the COD2EFT tag text (PNG_MARK)."""
    mark = mark or PNG_MARK
    h, w = arr.shape[:2]
    c = arr.shape[2] if arr.ndim == 3 else 1
    rgba = np.ones((h, w, 4), np.float32)
    if c == 1:
        rgba[..., :3] = arr.reshape(h, w, 1)
    else:
        rgba[..., :min(c, 4)] = arr[..., :4]
    name = os.path.basename(path)
    im = bpy.data.images.new(name, w, h, alpha=(c == 4), float_buffer=False)
    # colour space FIRST: changing it after the pixels are set throws the new pixels away
    if not srgb:
        try:
            im.colorspace_settings.name = "Non-Color"
        except TypeError:
            pass
    im.pixels.foreach_set(np.clip(rgba, 0, 1).astype(np.float32).ravel())
    im.filepath_raw = path
    im.file_format = "PNG"
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im.save()
    png_mark(path, mark)
    return im


PNG_MARK = f"COD2EFT enc={ENCODING}"


def mark_text(material_mode="ENC2", normal_style="OPENGL"):
    """The PNG tag for these settings: "COD2EFT enc=2" / "COD2EFT enc=3", plus " n=dx" when the
    normal map is written DirectX style (green down).  No "n=" means OpenGL (all files before
    2.6.0 are OpenGL unless the DirectX option was on - they said nothing, which is the bug the
    audit found: Unity flipped those maps a second time)."""
    enc = 3 if material_mode == "ENC3" else 2
    return f"COD2EFT enc={enc}" + (" n=dx" if normal_style == "DIRECTX" else "")


def png_mark(path, text=PNG_MARK):
    """Put a tEXt chunk ("Software" = COD2EFT enc=N) right after the PNG header, so the Unity
    side can tell COD2EFT textures (and which encoding they use) wherever they are copied to."""
    import zlib
    try:
        with open(io_path(path), "rb") as fh:
            data = fh.read()
    except OSError:
        return False
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return False
    end = 8 + 12 + int.from_bytes(data[8:12], "big")            # after the IHDR chunk
    if b"COD2EFT enc=" in data[:end + 256]:
        return True
    body = b"Software\x00" + text.encode("latin-1")
    chunk = len(body).to_bytes(4, "big") + b"tEXt" + body + \
        (zlib.crc32(b"tEXt" + body) & 0xFFFFFFFF).to_bytes(4, "big")
    with open(io_path(path), "wb") as fh:
        fh.write(data[:end] + chunk + data[end:])
    return True


def png_marker(path):
    """The COD2EFT marker text of a PNG ("COD2EFT enc=N"), or None."""
    try:
        with open(io_path(path), "rb") as fh:
            head = fh.read(4096)
    except OSError:
        return None
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    i = 8
    while i + 8 <= len(head):
        n = int.from_bytes(head[i:i + 4], "big")
        kind = head[i + 4:i + 8]
        if kind == b"IDAT":
            break
        if kind == b"tEXt":
            body = head[i + 8:i + 8 + n]
            if body.startswith(b"Software\x00COD2EFT"):
                return body.split(b"\x00", 1)[1].decode("latin-1", "ignore")
        i += 12 + n
    return None


# ---------------------------------------------------------------------------------------------
# finding the textures of a COD material
# ---------------------------------------------------------------------------------------------
def _parse_semantics(path):
    out = []
    with open(io_path(path), "r", encoding="utf-8", errors="ignore") as fh:
        for ln in fh:
            ln = ln.strip()
            if "," not in ln or ln.startswith(("semantic,", "name,", "#")):
                continue
            sem, img = ln.split(",", 1)
            out.append((sem.strip(), img.strip()))
    return out


_SEM_INDEX = {}


def semantics_file(folder, name):
    """The exporter's image list for material `name`: <name>_images.txt (Greyhound) or
    _mat_info/<name>.txt.  BO6 / BO7 name some materials "m/<name>" - Blender (Cast / FBX)
    calls them "m_<name>" while the file is _mat_info/<name>.txt with "Name: m/<name>" inside,
    so those are matched through that Name line."""
    names = [name]
    if name.lower().startswith("m_"):
        # BO6 / BO7 / MW4: "m/<name>" is also written as _mat_info/<name>.txt WITHOUT a Name line
        # (sunflower_base's first-person arm skin, teeth, MW4 glass, eyelashes): before 2.5.1
        # these materials lost their normal / gloss maps and came out flat (gloss 0.5)
        names.append(name[2:])
    for n in names:
        for info in (os.path.join(folder, "_mat_info", n + ".txt"),
                     os.path.join(folder, n + "_images.txt")):
            if is_file(info):
                return info
    d = os.path.join(folder, "_mat_info")
    if not os.path.isdir(io_path(d)):
        return None
    if d not in _SEM_INDEX:
        idx = {}
        for f in glob.glob(os.path.join(d, "*.txt")):
            try:
                with open(io_path(f), "r", encoding="utf-8", errors="ignore") as fh:
                    first = fh.readline().strip()
            except OSError:
                continue
            if first.lower().startswith("name:"):
                idx[first[5:].strip().replace("/", "_").replace("\\", "_").lower()] = f
        _SEM_INDEX[d] = idx
    return _SEM_INDEX[d].get(name.lower())


def _mtl_map_kd(folder, mat):
    for mtl in glob.glob(os.path.join(folder, "*.mtl")):
        cur = None
        try:
            with open(io_path(mtl), "r", encoding="utf-8", errors="ignore") as fh:
                for ln in fh:
                    ln = ln.strip()
                    if ln.startswith("newmtl "):
                        cur = ln[7:].strip()
                    elif cur == mat and ln.startswith("map_Kd "):
                        return ln[7:].strip().replace("\\\\", "/").replace("\\", "/")
        except OSError:
            pass
    return None


def _resolve(folder, mat, name):
    """File for image `name` of material `mat` exported next to a model in `folder`."""
    if not name or name.startswith("$"):
        return None
    if "/" in name:                                   # a relative path (mtl)
        p = os.path.join(folder, name)
        return p if is_file(p) else _resolve(folder, mat, os.path.splitext(
            os.path.basename(name))[0])
    for d in (os.path.join(folder, "_images", mat), os.path.join(folder, "_images")):
        for ext in IMG_EXT:
            p = os.path.join(d, name + ext)
            if is_file(p):
                return p
    hits = [p for p in glob.glob(os.path.join(folder, "_images", "**", glob.escape(name) + ".*"),
                                 recursive=True) if p.lower().endswith(IMG_EXT)]
    return hits[0] if hits else None


def _stats(path):
    a = load_image(path, max_size=128)
    return a.reshape(-1, 4).mean(0), a.reshape(-1, 4).std(0), a.shape[1], a.shape[0]


WRINKLE_TILE_CORR = 0.8


def _tile_corr(path):
    """How alike the four quadrants of a map are (normal G channel, mean pairwise correlation).
    A COD wrinkle map is a 2 x 2 tile of expression versions of the same face, so its quadrants
    repeat: 0.91 - 1.0 on every wrinkle map of the test characters (11), while every real NOG /
    normal map scores 0.61 or less (483 maps)."""
    a = load_image(path, max_size=128)
    h, w = (a.shape[0] // 2) * 2, (a.shape[1] // 2) * 2
    g = a[:h, :w, 1].astype(np.float64)
    q = [g[:h // 2, :w // 2], g[:h // 2, w // 2:], g[h // 2:, :w // 2], g[h // 2:, w // 2:]]
    q = [x - x.mean() for x in q]
    cs = []
    for i in range(4):
        for j in range(i + 1, 4):
            d = np.sqrt((q[i] ** 2).sum() * (q[j] ** 2).sum())
            cs.append(float((q[i] * q[j]).sum() / d) if d > 0 else 0.0)
    return float(np.mean(cs))


def _is_nog(m, s):
    return m[2] > 0.7 and abs(m[1] - 0.5) < 0.15 and abs(m[3] - 0.5) < 0.15 and \
        (s[1] > 0.015 or s[3] > 0.015)


def _is_nog_loose(m, s):
    """A NOG map as it can look in the IW slot that holds it (unk_semantic_0x4): G and A (the
    normal) around 0.5; R (gloss) and B (occlusion) anything.  Needed for maps with a dark
    occlusion (MW2 Kleo's watch: B mean 0.43) or a flat normal / constant gloss (MW3 papa)."""
    return abs(m[1] - 0.5) < 0.12 and abs(m[3] - 0.5) < 0.12


# the slot that holds the NOG map in the "0x" hashed games (MW2019 .. MW3): 30 of the 41 content-
# found NOG maps of the test characters are in it, the rest in 0x9 / 0xA (checked on all of them)
NOG_SLOT_0X = "unk_semantic_0x4"


def _is_rg_normal(m, s):
    return abs(m[0] - 0.5) < 0.12 and abs(m[1] - 0.5) < 0.12 and m[2] < 0.3 and \
        (s[0] > 0.015 or s[1] > 0.015)


SPLIT_N = re.compile(r"^(.+)_n(_[a-z0-9]+)?$", re.I)


def _split_nog(folder, name, sems, color):
    """Greyhound 'split' exports (the MW2019 test characters): the semantics name a packed
    "X_n&Y_g~hash" image that isn't in the export; X_n (normal), Y_g / X_g (gloss) and X_o
    (occlusion) are there as separate files instead.  X_n holds the NOG's two hemi-octahedron
    channels in R / G (checked: decoding them as hemi-octahedron gives the same DirectX height
    test as the IW NOG maps, 18 of 23 maps; read as plain X / Y they give none).  Without
    semantics (an empty _images.txt), X = the colour map's name without "_c"."""
    parts = []
    for sem, img in sems:
        if "&" not in img or _resolve(folder, name, img):
            continue
        first = img.split("~")[0].split("&")
        if SPLIT_N.match(first[0]):
            parts.append(first)
    if not parts and color:
        b = os.path.basename(color).split("~")[0].split("&")[0]
        b = os.path.splitext(b)[0]
        if b.lower().endswith("_c"):
            parts.append([b[:-2] + "_n", b[:-2] + "_g"])
    for first in parts:
        base = SPLIT_N.match(first[0]).group(1)
        n = _resolve(folder, name, base + "_n")
        if not n:
            continue
        out = {"normal_ho": n}
        g = None
        for q in first[1:]:
            if q.lower().endswith("_g"):
                g = _resolve(folder, name, q)
        g = g or _resolve(folder, name, base + "_g")
        if g:
            out["gloss"] = g
        o = _resolve(folder, name, base + "_o")
        if o:
            out["ao"] = o
        return out
    return None


def _split_colour_spec(folder, name, color):
    """(X_c, Y_s) files next to a fused "X_c&Y_s~hash" colour map, or None."""
    parts = os.path.basename(color).split("~")[0].split("&")
    if len(parts) != 2 or not parts[0].lower().endswith("_c") or \
            not parts[1].lower().endswith("_s"):
        return None
    c, sp = _resolve(folder, name, parts[0]), _resolve(folder, name, parts[1])
    return (c, sp) if c and sp else None


def _has_export(folder, name):
    """True when the export in `folder` has at least one image file of material `name`."""
    d = os.path.join(folder, "_images", name)
    if os.path.isdir(io_path(d)) and any(f.lower().endswith(IMG_EXT) for f in os.listdir(io_path(d))):
        return True
    info = semantics_file(folder, name)
    if info and any(_resolve(folder, name, img) for _, img in _parse_semantics(info)):
        return True
    return bool(_resolve(folder, name, _mtl_map_kd(folder, name)))


def _sibling_export(folder, name):
    """A folder next to the model's own that has the images of material `name`, when the model's
    own export has none of them.  The exporters write the images of a material shared by
    several models (head / torso / arms of one character) into one model's folder only: on the
    test characters a Cold War torso's logo, MW2019 patches and pants.  COD material names are
    unique (hashes or full names), so the same name is the same material."""
    if _has_export(folder, name):
        return None
    folder = os.path.normpath(folder)
    parent = os.path.dirname(folder)
    try:
        sibs = sorted(os.listdir(parent))
    except OSError:
        return None
    for s in sibs:
        d = os.path.join(parent, s)
        if d != os.path.normpath(folder) and os.path.isdir(os.path.join(d, "_images", name)) \
                and _has_export(d, name):
            return d
    return None


def _decal_alpha(path):
    """True when a colour map's alpha looks like a decal's cut-out: at least 20 % of the image
    has alpha <= 0.1 and 90 % of that is pure black (sRGB luminance < 0.02) - nothing is drawn
    there.  The test characters' logos, patches, a necklace, a tattoo-like emblem (MW2 Kleo)
    and mouth pieces: without a cut-out they showed as black shapes.  A metal mask's non-metal
    part has real colour, so it does not pass."""
    try:
        a = load_image(path, 256).reshape(-1, 4)
    except Exception:
        return False
    lo = a[:, 3] <= 0.1
    if lo.mean() < 0.2 or float(a[:, 3].std()) < 0.004:
        return False
    lum = a[lo, :3] @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    return float((lum < 0.02).mean()) >= 0.9


def find_textures(mat, model_file, log=None):
    """Roles -> file for one COD material: color, nog (IW packed), normal (R/G), gloss, spec,
    ao.  Plus 'how' notes.  mat = Blender material (or its name)."""
    name = base_name(mat.name if hasattr(mat, "name") else mat)
    folder = os.path.dirname(model_file) if model_file else ""
    roles, how = {}, []
    if not folder or not os.path.isdir(folder):
        return roles, ["model folder unknown"]
    alt = _sibling_export(folder, name)
    if alt:
        folder = alt
        how.append(f"textures from the export in ../{os.path.basename(alt)}")
    info = semantics_file(folder, name)
    sems = _parse_semantics(info) if info else []
    named = {s.lower(): img for s, img in sems}
    # 1) Cold War style named semantics
    for role, keys in (("color", ("colormap",)), ("normal", ("normalmap",)),
                       ("gloss", ("glossmap",)), ("spec", ("speccolormap", "specularmap")),
                       ("ao", ("aomap", "occlusionmap"))):
        for k in keys:
            p = _resolve(folder, name, named.get(k))
            if p:
                roles[role] = p
                how.append(f"{role}={os.path.basename(p)} ({k})")
                break
    # Cold War's "glossMap" holds ROUGHNESS: measured on the BO5 test exports, metal parts
    # (belt buckles, bracelets, pistol) average 0.06 - 0.17 and cloth (hoodies, pants) 0.85 - 0.94
    roles["gloss_inverted"] = "gloss" in roles
    # 2) the colour map the importer / exporter names
    if "color" not in roles and hasattr(mat, "node_tree") and mat.node_tree:
        for n in mat.node_tree.nodes:
            if n.type == "TEX_IMAGE" and n.image and n.outputs["Color"].links:
                if any(l.to_socket.name == "Base Color" for l in n.outputs["Color"].links):
                    p = bpy.path.abspath(n.image.filepath)
                    if is_file(p):
                        roles["color"] = p
                        how.append(f"color={os.path.basename(p)} (material)")
                        break
    if "color" not in roles:
        p = _resolve(folder, name, _mtl_map_kd(folder, name))
        if p:
            roles["color"] = p
            how.append(f"color={os.path.basename(p)} (mtl)")
    # 3) hashed semantics: classify the images by name hints and content
    cand = []
    seen = set()
    for sem, img in sems:
        p = _resolve(folder, name, img)
        if not p or p in seen or p in roles.values():
            continue
        seen.add(p)
        low = os.path.basename(p).lower()
        if any(w in low for w in SKIP_WORDS) or "micro" in low or "detail" in sem.lower():
            continue
        try:
            m, s, w, h = _stats(p)
            W, H = image_size(p)
        except Exception:
            continue
        slot_nog = sem.strip().lower() == NOG_SLOT_0X and _is_nog_loose(m, s)
        if W * H <= 64 and not slot_nog:
            continue
        # a single-channel map in a cut-out slot is never the colour map (MW4 beta lashes: the
        # colour map is not in the .mtl, and the cut-out map was taken for it)
        op_like = bool(OPACITY_NAMED.match(sem.strip()) or sem.strip().lower() in OPACITY_SLOTS) \
            and abs(m[0] - m[1]) < 0.01 and abs(m[1] - m[2]) < 0.01 \
            and abs(s[0] - s[1]) < 0.01 and abs(s[1] - s[2]) < 0.01
        cand.append((p, low, m, s, W * H, op_like, slot_nog))
    if "nog" not in roles and "normal" not in roles:
        nogs = [c for c in cand if _is_nog(c[2], c[3])] or [c for c in cand if c[6]]
        rgs = [c for c in cand if _is_rg_normal(c[2], c[3])]
        if nogs:
            # Several NOG-like maps: skin materials also carry wrinkle maps (e.g. BO7
            # orange_outlaw's face: a 2 x 2 tile of expression wrinkles, same size as the real
            # NOG, gloss a flat 1.0).  So prefer a map that is not a 2 x 2 tile (2.6.10: BO7
            # brie's wrinkle map has a gloss that varies a little, 0.92 +- 0.05, so the gloss test
            # alone took it and his skin came out at gloss 0.95 instead of 0.44), then one whose
            # gloss (R) actually varies, then one the size of the colour map, then the biggest;
            # ties keep the exporter's order.
            try:
                cres = image_size(roles["color"]) if "color" in roles else None
            except Exception:
                cres = None
            tiled = set()
            if len(nogs) > 1:
                for c in nogs:
                    try:
                        if _tile_corr(c[0]) >= WRINKLE_TILE_CORR:
                            tiled.add(c[0])
                    except Exception:
                        pass
            p = max(nogs, key=lambda c: (c[0] not in tiled, c[3][0] > 0.005,
                                         cres is not None and image_size(c[0]) == tuple(cres),
                                         c[4]))[0]
            roles["nog"] = p
            how.append(f"nog={os.path.basename(p)} (content" +
                       (f"; {len(tiled)} wrinkle map(s) skipped)" if tiled else ")"))
        elif rgs:
            p = max(rgs, key=lambda c: c[4])[0]
            roles["normal"] = p
            how.append(f"normal={os.path.basename(p)} (content)")
    if "nog" not in roles and "normal" not in roles:
        sp = _split_nog(folder, name, sems, roles.get("color"))
        if sp:
            for role, p in sp.items():
                roles.setdefault(role, p)
            how.append(", ".join(f"{r}={os.path.basename(p)}" for r, p in sp.items()) +
                       " (Greyhound split files)")
    if "color" not in roles:
        cols = [c for c in cand if c[0] not in roles.values() and not _is_nog(c[2], c[3])
                and not _is_rg_normal(c[2], c[3]) and not c[5] and not c[6]
                and (max(c[3][:3]) > 0.02 or c[4] >= 4096)]
        # masks / occlusion / transparency maps are not colour maps
        cols = [c for c in cols if not re.search(
            r"_(t|m\d?|mask|msk|o|ao|sss|e|a)([~&.]|$)", os.path.splitext(c[1])[0] + ".")]
        hinted = [c for c in cols if "_c&" in c[1] or c[1].endswith("_c") or "_c." in c[1]
                  or "_col" in c[1]]
        pick = hinted or cols
        if pick:
            # a big map with some variation first (a black lash / brow colour map is flat)
            p = max(pick, key=lambda c: (max(c[3][:3]) > 0.02, c[4]))[0]
            roles["color"] = p
            how.append(f"color={os.path.basename(p)} ({'name' if hinted else 'content'})")
    # Tint: some materials (MW2's kleo jacket) have a colour map with no colour in it
    # (saturation ~0) and one constant colour among their images (a 1 x 1 image that is not
    # black / white / grey and not a pure-primary mask like (1, 0, 0)).  The colour is then
    # colour map x tint.  (Worked out from the export - not checked against the game.)
    if "color" in roles:
        try:
            c = load_image(roles["color"], 64)[..., :3]
            grey = float((c.max(-1) - c.min(-1)).mean()) < 0.01
        except Exception:
            grey = False
        if grey:
            tints = []
            for sem, img in sems:
                p = _resolve(folder, name, img)
                if not p or os.path.basename(p).startswith("$"):
                    continue
                try:
                    if tuple(image_size(p)) != (1, 1):
                        continue
                    v = load_image(p, 4).reshape(-1, 4)[0][:3]
                except Exception:
                    continue
                if float(v.max() - v.min()) < 0.02:
                    continue                                  # black / white / grey
                if all(min(abs(x - 0.0), abs(x - 0.5), abs(x - 1.0)) < 0.02 for x in v):
                    continue                                  # (1,0,0)-style mask flags
                tints.append((sem, tuple(float(x) for x in v)))
            if len(set(t for _, t in tints)) == 1:
                roles["tint"] = tints[0][1]
                how.append("tint=(" + ", ".join(f"{x:.2f}" for x in tints[0][1]) +
                           f") ({tints[0][0]})")
    # no colour map but one constant colour (a 1 x 1 image - MW4 beta's dense eyelashes):
    # use it as the colour
    if "color" not in roles:
        consts = []
        for sem, img in sems:
            p = _resolve(folder, name, img)
            if not p or os.path.basename(p).startswith("$"):
                continue
            try:
                if tuple(image_size(p)) != (1, 1):
                    continue
                v = load_image(p, 4).reshape(-1, 4)[0][:3]
            except Exception:
                continue
            if all(min(abs(x - 0.0), abs(x - 0.5), abs(x - 1.0)) < 0.02 for x in v):
                continue                                      # black / white / flag values
            consts.append((sem, tuple(float(x) for x in v)))
        if len(set(t for _, t in consts)) == 1:
            roles["color_const"] = consts[0][1]
            how.append("color=(" + ", ".join(f"{x:.2f}" for x in consts[0][1]) +
                       f") (constant, {consts[0][0]})")
    # IW fused colour/spec: colour alpha = specular / metal when the material has a NOG map
    roles["fused"] = ("nog" in roles or "normal_ho" in roles) and "spec" not in roles
    # Greyhound split exports (MW2019 test characters): the fused "X_c&Y_s" PNG has lost its
    # alpha (1.0 everywhere); the split X_c (colour with metal taken out) and Y_s (specular
    # colour) next to it still carry it - see _split_alpha
    if roles.get("fused") and "color" in roles and "&" in os.path.basename(roles["color"]):
        cs = _split_colour_spec(folder, name, roles["color"])
        if cs:
            roles["split_c"], roles["split_s"] = cs
    if roles.get("tint"):
        # a tinted colour map (colour-less, x one constant colour): its alpha is not taken as a
        # metal mask - MW2 Kleo's pants / collar have alpha 0.82 - 0.95 there, which would
        # make navy-blue metal.  (Judgement: likely the tint mask; not checked against the game.)
        roles["alpha_kind"] = "tint"
    # cut-out candidates (which one - if any - is used is decided on the mesh: _pick_opacity)
    ops, seen_op = [], set()
    for sem, img in sems:
        kind = "named" if OPACITY_NAMED.match(sem.strip()) else \
            "slot" if sem.strip().lower() in OPACITY_SLOTS else None
        if kind and any(w in img.lower() for w in SKIP_WORDS + ("infrared",)):
            continue                                   # thermal / infrared views of the model
        p = _resolve(folder, name, img) if kind else None
        if p and p not in seen_op and p not in (roles.get("color"), roles.get("nog"),
                                                roles.get("normal"), roles.get("gloss")):
            seen_op.add(p)
            ops.append((kind, sem.strip(), p, 0))
    if "color" in roles and is_alpha_material(name):
        ops.append(("colour", "colour map alpha", roles["color"], 3))
    elif "color" in roles and _decal_alpha(roles["color"]):
        ops.append(("decal", "colour alpha, decal on black", roles["color"], 3))
    roles["opacity_cands"] = ops
    # images the export lists but didn't write (on the test exports: the longest paths - an
    # exporter that can't write 260+ character Windows paths skips them)
    # (split exports - MW2019 - leave out the packed image on purpose: one of its parts is there)
    miss = [len(os.path.abspath(os.path.join(folder, "_images", name, img + ".dds")))
            for _sem, img in sems
            if img and not img.startswith("$") and not any(w in img.lower() for w in SKIP_WORDS)
            and not _resolve(folder, name, img)
            and not any(_resolve(folder, name, q) for q in img.split("~")[0].split("&"))]
    lacks = "color" not in roles or not any(roles.get(k) for k in ("nog", "normal", "normal_ho"))
    if miss and max(miss) >= 260 and lacks:
        roles["missing"] = (len(miss), max(miss))
    return roles, how


# ---------------------------------------------------------------------------------------------
# per-material EFT maps
# ---------------------------------------------------------------------------------------------
def _decode_hemi_oct(a):
    u = a[..., 1] * 2 - 1
    v = a[..., 3] * 2 - 1
    x = (u + v) * 0.5
    y = (u - v) * 0.5
    z = 1 - np.abs(x) - np.abs(y)
    n = np.stack([x, y, z], -1)
    return n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)


def _decode_hemi_oct_rg(a):
    """Hemi-octahedron normal kept in R / G (Greyhound's split X_n files)."""
    b = np.zeros(a.shape[:-1] + (4,), np.float32)
    b[..., 1], b[..., 3] = a[..., 0], a[..., 1]
    return _decode_hemi_oct(b)


def _decode_rg(a):
    x = a[..., 0] * 2 - 1
    y = a[..., 1] * 2 - 1
    z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
    return np.stack([x, y, z], -1)


def normal_is_directx(n):
    """Height-field consistency: with rows going UP (Blender), an OpenGL map has
    corr(dNx/drow, dNy/dcol) > 0 and a DirectX map < 0.  (EFT's own maps: OpenGL.)"""
    if n.shape[0] < 8 or n.shape[1] < 8:
        return None, 0.0
    a = np.gradient(n[..., 0], axis=0)
    b = np.gradient(n[..., 1], axis=1)
    m = (np.abs(a) + np.abs(b)) > 1e-3
    if m.sum() < 100:
        return None, 0.0
    c = float(np.corrcoef(a[m], b[m])[0, 1])
    if abs(c) < 0.05:
        return None, c
    return c < 0, c


FULL = (0.0, 0.0, 1.0, 1.0)


def resample(a, crop, W, H):
    """Region crop = (u0, v0, u1, v1) of image a (h, w, c; row 0 = v 0) -> (H, W, c): box
    pre-filter by the integer part of the scale, then bilinear at the exact sample points."""
    h, w = a.shape[:2]
    u0, v0, u1, v1 = crop
    fw, fh = float(w), float(h)
    f = int(min((u1 - u0) * w / max(W, 1), (v1 - v0) * h / max(H, 1)))
    if f > 1:
        hh, ww = h // f * f, w // f * f
        a = a[:hh, :ww].reshape(hh // f, f, ww // f, f, -1).mean(axis=(1, 3))
        h, w = a.shape[:2]
        fw, fh = fw / f, fh / f
    xs = np.clip(u0 * fw + (np.arange(W) + 0.5) * (u1 - u0) * fw / W - 0.5, 0, w - 1)
    ys = np.clip(v0 * fh + (np.arange(H) + 0.5) * (v1 - v0) * fh / H - 0.5, 0, h - 1)
    x0 = np.floor(xs).astype(int)
    y0 = np.floor(ys).astype(int)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    wx = (xs - x0)[None, :, None]
    wy = (ys - y0)[:, None, None]
    top = a[y0][:, x0] * (1 - wx) + a[y0][:, x1] * wx
    bot = a[y1][:, x0] * (1 - wx) + a[y1][:, x1] * wx
    return (top * (1 - wy) + bot * wy).astype(np.float32)


def _load_region(path, crop, W, H):
    """Region `crop` of the image at `path`, sampled to W x H (loaded at ~2x that density)."""
    u0, v0, u1, v1 = crop
    need = int(math.ceil(2 * max(W / max(u1 - u0, 1e-6), H / max(v1 - v0, 1e-6))))
    return resample(load_image(path, need), crop, W, H)


def material_maps(roles, W, H, spec_scale=SPEC_SCALE, ao_strength=1.0, crop=FULL,
                  ao_in_spec=True, metal_keep=METAL_KEEP, colour_gain=1.0, colour_sat=1.0):
    """EFT maps for region `crop` (UV 0..1) of one material at W x H:
    d (H,W,4), n (H,W,3), g (H,W,1), info.
      ao_strength  how much of the COD occlusion is multiplied into the colour (0 = none)
      ao_in_spec   multiply the occlusion into the specular (diffuse alpha) too, same strength
      metal_keep   share of a metal's colour kept in the diffuse (0 = black, as physically-
                   based metal; EFT's own textures keep metal fairly bright - see METAL_KEEP)
    The normal map comes out OpenGL / Unity style (green = up)."""
    info = {}
    if "color" in roles:
        c = _load_region(roles["color"], crop, W, H)
    elif roles.get("color_const"):
        c = np.zeros((H, W, 4), np.float32)
        c[..., :3] = np.array(roles["color_const"], np.float32)
    else:
        c = np.full((H, W, 4), 0.5, np.float32)
        c[..., 3] = 0.0
        info["no_color"] = True
    rgb, a_c = c[..., :3], c[..., 3]
    if roles.get("tint"):
        rgb = rgb * np.array(roles["tint"], np.float32)
        info["tinted"] = True
    gloss = np.full((H, W), 0.5, np.float32)
    ao = np.ones((H, W), np.float32)
    n = None
    if "nog" in roles:
        g = _load_region(roles["nog"], crop, W, H)
        gloss, ao, n = g[..., 0], g[..., 2], _decode_hemi_oct(g)
    elif "normal" in roles:
        n = _decode_rg(_load_region(roles["normal"], crop, W, H))
    elif "normal_ho" in roles:
        n = _decode_hemi_oct_rg(_load_region(roles["normal_ho"], crop, W, H))
    if "gloss" in roles:
        gloss = _load_region(roles["gloss"], crop, W, H)[..., 0]
        if roles.get("gloss_inverted"):
            gloss = 1.0 - gloss
    if "ao" in roles:
        ao = _load_region(roles["ao"], crop, W, H)[..., 0]
    if n is None:
        n = np.zeros((H, W, 3), np.float32)
        n[..., 2] = 1
    else:
        n[..., 1] *= -1                         # COD (DirectX) -> OpenGL / Unity
    # specular / metal
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    if roles.get("split_s") and roles.get("fused"):
        a_c = _split_alpha(roles, rgb, crop, W, H)
    kind = roles.get("alpha_kind")
    if kind is None:                     # not judged on a mesh (classify_alpha): as before 2.4
        kind = "metal_mask" if float(a_c.std()) > 0.004 and float(a_c.mean()) < 0.995 \
            else "no_alpha"
    if "spec" in roles:
        f0 = _load_region(roles["spec"], crop, W, H)[..., :3].mean(-1)
        diffuse = rgb
        # metal share for the enc=3 gloss curves only: a specular colour map's metals have F0
        # 0.5 - 1, its non-metals 0.02 - 0.08
        info["m"] = np.clip((f0 - 0.1) / 0.4, 0, 1).astype(np.float32)
    elif roles.get("fused") and kind == "metal_mask":
        # IW fused colour/spec (GameImageUtil "CoD Specular/Albedo"): alpha <= 0.1 = the
        # reflectance of a non-metal, above that it blends to metal (colour = specular colour)
        m = np.clip((a_c - 0.1) / 0.9, 0, 1)
        diffuse = rgb * (1 - (1 - metal_keep) * m)[..., None]
        f0 = np.minimum(a_c, 0.1) + m * lum
        info["metal_share"] = round(float((m > 0.5).mean()), 3)
        info["m"] = m
    elif roles.get("fused") and kind == "continuous":
        # the alpha is not a metal mask (skin and a few others - see classify_alpha): a non-
        # metal, except where the alpha stands far above the material's own level (rivets on
        # BO7 silver's vest: level 0.3, rivets 1.0)
        thr = max(0.9, roles.get("alpha_level", 0.5) + 0.3)
        m = np.clip((a_c - thr) / max(1.0 - thr, 0.02), 0, 1) if thr < 1.0 else \
            np.zeros_like(a_c)
        diffuse = rgb * (1 - (1 - metal_keep) * m)[..., None]
        f0 = F0_DIELECTRIC * (1 - m) + m * lum
        info["metal_share"] = round(float((m > 0.5).mean()), 3)
        info["m"] = m
    else:
        # no specular information ("no_alpha": a colour map without alpha, "tint": see
        # find_textures): a non-metal
        diffuse = rgb
        f0 = np.full((H, W), F0_DIELECTRIC, np.float32)
        info["spec_default"] = True
    if ao_strength > 0:
        occ = (1 - ao_strength + ao_strength * ao)
        diffuse = diffuse * occ[..., None]
        if ao_in_spec:
            f0 = f0 * occ
    if colour_sat != 1.0 or colour_gain != 1.0:
        # user look adjustments (2.6.4; defaults 1 = untouched): saturation around the pixel's
        # own luminance, then a brightness gain
        lw = (diffuse @ np.array([0.2126, 0.7152, 0.0722], np.float32))[..., None]
        diffuse = np.clip((lw + (diffuse - lw) * colour_sat) * colour_gain, 0, 1)
    d = np.concatenate([diffuse, np.clip(f0 * spec_scale, 0, 1)[..., None]], -1)
    return d.astype(np.float32), (n * 0.5 + 0.5).astype(np.float32), gloss[..., None], info


def _split_alpha(roles, rgb, crop, W, H):
    """The fused colour alpha of a Greyhound split export, rebuilt from its split files (the
    fused PNG lost it): where the specular colour Y_s is above its non-metal floor (0.222 in
    every split file of the test characters), the metal share is what X_c took out of the
    colour (GameImageUtil's split: X_c = colour x (1 - m)), and alpha = 0.1 + 0.9 m; elsewhere
    a non-metal (alpha = F0_DIELECTRIC).  Only m > 0.8 counts as metal: the split also took
    about half the colour out of skin (m 0.45 - 0.5 on the MW2019 faces) - the same smooth
    skin alpha that classify_alpha keeps from being read as metal."""
    c = _load_region(roles["split_c"], crop, W, H)[..., :3]
    sp = _load_region(roles["split_s"], crop, W, H)[..., :3].mean(-1)
    w = np.array([0.2126, 0.7152, 0.0722], np.float32)
    m = np.clip(1.0 - (c @ w) / np.maximum(rgb @ w, 1e-3), 0, 1)
    return np.where((sp > 0.235) & (m > 0.8), 0.1 + 0.9 * m, F0_DIELECTRIC).astype(np.float32)


def classify_alpha(roles, uv, weights):
    """What the colour map's alpha of a fused IW material means, judged on the mesh (uv: face
    sample points, weights: their areas).  Sets roles["alpha_kind"] / ["alpha_level"]:
      no_alpha    the same value everywhere (>= 0.9): a colour map without alpha (BC1)
      metal_mask  mostly <= 0.12 (non-metal reflectance) or >= 0.88 (metal) - IW's convention
      continuous  over half the surface in between: not a metal mask.  On the test characters
                  (146 fused materials) that is 9 skins, 1 mouth, a hair fray and 4 gear
                  materials; read as metal the skins came out grey and shiny (F0 0.2 - 0.3
                  instead of about 0.04), which is what made converted skin look wet
    Returns a note for the report, or None."""
    if not roles.get("fused") or "color" not in roles or roles.get("alpha_kind") == "tint":
        return "colour alpha: tint mask, not used as specular" \
            if roles.get("alpha_kind") == "tint" else None
    try:
        img = load_image(roles["color"], 512)
    except Exception:
        return None
    H, W = img.shape[:2]
    x = np.clip((np.mod(uv[:, 0], 1.0) * W).astype(int), 0, W - 1)
    y = np.clip((np.mod(uv[:, 1], 1.0) * H).astype(int), 0, H - 1)
    a = img[y, x, 3].astype(np.float64)
    if roles.get("split_s"):
        try:
            c = load_image(roles["split_c"], 512)
            sp = load_image(roles["split_s"], 512)
            cx = np.clip((np.mod(uv[:, 0], 1.0) * c.shape[1]).astype(int), 0, c.shape[1] - 1)
            cy = np.clip((np.mod(uv[:, 1], 1.0) * c.shape[0]).astype(int), 0, c.shape[0] - 1)
            sx = np.clip((np.mod(uv[:, 0], 1.0) * sp.shape[1]).astype(int), 0, sp.shape[1] - 1)
            sy = np.clip((np.mod(uv[:, 1], 1.0) * sp.shape[0]).astype(int), 0, sp.shape[0] - 1)
            wl = np.array([0.2126, 0.7152, 0.0722])
            m = np.clip(1.0 - (c[cy, cx, :3] @ wl) / np.maximum(img[y, x, :3] @ wl, 1e-3), 0, 1)
            a = np.where((sp[sy, sx, :3].mean(-1) > 0.235) & (m > 0.8), 0.1 + 0.9 * m,
                         F0_DIELECTRIC)
        except Exception:
            pass
    w = np.asarray(weights, np.float64)
    w = w / max(w.sum(), 1e-12)
    mean = float((a * w).sum())
    std = float(np.sqrt(((a - mean) ** 2 * w).sum()))
    if std < 0.004 and mean >= 0.9:
        # 2.6.12: Cold War packs "default_black_0&<name>_s": the albedo is black and the RGB is
        # the specular colour, so an alpha of 1 everywhere means all metal, not "no alpha" (the
        # four in the test exports: a steel zipper pull 0.71, brass 0.69/0.57/0.38, a gold
        # sunglasses frame and a button - all bright metal F0).  Read as no alpha they were grey
        # or gold *paint* with a dielectric F0 of 0.04.
        if re.match(r"default_black[^&~]*&(?!default)[^~]*_s(~|$)",
                    os.path.splitext(os.path.basename(roles["color"]))[0], re.I):
            roles["alpha_kind"] = "metal_mask"
            return "colour alpha: all metal (black albedo + a spec map: the colour is the metal's)"
        roles["alpha_kind"] = "no_alpha"
        return "colour alpha: none (colour map without alpha) - non-metal"
    # a colour-less (grey) colour map whose alpha is a copy of it: MW2 Kleo's jacket (colour
    # and alpha both 0.94, correlation 0.99) - read as a metal mask it made a white-chrome
    # jacket.  (Same family as the tinted materials; real metal on grey maps - BO7 silver's
    # armour plates - doesn't follow its colour: correlation 0.88, means 0.4 vs 0.9.)
    col = img[y, x, :3].astype(np.float64)
    lum = col @ np.array([0.2126, 0.7152, 0.0722])
    chroma = float(((col.max(1) - col.min(1)) * w).sum())
    # 2.5.1: also when both are FLAT - MW2 Kleo's first-person sleeves: grey 0.969, alpha 0.968,
    # both std < 0.01, so the correlation says nothing (it came out -0.99) and the whole sleeve
    # read as 100% metal = white chrome in first person.  Real metal keeps its alpha well above
    # its grey colour (MW4 carabiner: colour 0.55, alpha 0.88), so the means test still holds.
    ls, as_ = float(lum.std()), float(a.std())
    flat = ls < 0.02 and as_ < 0.02
    if chroma < 0.01 and abs(float((lum * w).sum()) - mean) < 0.1 and \
            (flat or (ls > 0.004 and as_ > 0.004 and float(np.corrcoef(lum, a)[0, 1]) > 0.9)):
        roles["alpha_kind"] = "tint"
        return "colour alpha: a copy of the (colour-less) colour map, not used as specular"
    mid = float(w[(a > 0.12) & (a < 0.88)].sum())
    o = np.argsort(a)
    level = float(a[o][min(np.searchsorted(np.cumsum(w[o]), 0.5), len(a) - 1)])
    if mid > 0.5:
        roles["alpha_kind"] = "continuous"
        roles["alpha_level"] = level
        return (f"colour alpha: not a metal mask (level {level:.2f} over {mid:.0%} of the "
                "surface) - non-metal")
    roles["alpha_kind"] = "metal_mask"
    metal = float(w[a >= 0.55].sum())
    return f"colour alpha: metal mask ({metal:.0%} metal)" if metal >= 0.005 else None


def alpha_mask(roles, W, H, crop=FULL):
    """Cut-out mask (1 = visible) from the opacity source _pick_opacity chose."""
    op = roles.get("opacity")
    if not op:
        return np.ones((H, W, 1), np.float32)
    a = _load_region(op[0], crop, W, H)[..., op[1]:op[1] + 1]
    gain = op[2] if len(op) > 2 else 1.0
    return np.clip(a * gain, 0.0, 1.0) if gain != 1.0 else a


def _pick_opacity(cands, color_path, faces_uv, faces_w, hair_name=False):
    """Choose the cut-out map of one material from its candidates, on the mesh itself.
    faces_uv (F, K, 2): sample points inside each face (UV 0..1), faces_w (F,): face areas.
    A map is used when it cuts the surface the way cards do:
      share T of the surface below PICK_CUTOFF, and the share M of faces that have both clearly
      cut (< 0.25) and clearly kept (> 0.6) points inside them (strands / fringe drawn across
      the face).  Region masks (a face's detail mask) are whole faces on or off (M ~ 0).
      named alphaMap: T >= 0.02 | slot map: greyscale, same shape as the colour map,
      T 0.1 - 0.98 and M >= 0.2 (measured: hair / lash / brow / fringe cards M 0.34 - 0.94,
      other masks in these slots M <= 0.15); a material NAMED hair / lash / brow ... only needs
      T >= 0.1 (sparse brow shells: nearly all cut, strands thinner than the samples) |
      colour alpha (hair-named materials, and decals on black - _decal_alpha): two-level
      alpha and T >= 0.02.
    Returns (path, channel, gain, how) or None."""
    if not cands or len(faces_uv) == 0:
        return None
    try:
        cw, ch_ = image_size(color_path) if color_path else (0, 0)
    except Exception:
        cw, ch_ = 0, 0
    wsum = float(faces_w.sum()) or 1.0
    for kind, sem, p, ch in cands:
        if any(w in os.path.basename(p).lower() for w in ("thermal", "infrared", "heat")):
            continue
        try:
            W, H = image_size(p)
            if min(W, H) < 16:
                continue
            a = load_image(p, 512)
        except Exception:
            continue
        if kind == "slot":
            grey = float(np.abs(a[..., 0] - a[..., 1]).mean() + np.abs(a[..., 1] - a[..., 2]).mean())
            if grey > 0.02:
                continue
            if min(cw, ch_) >= 64 and abs(W / H - cw / ch_) > 0.1 * (cw / ch_):
                continue
        # soft maps (a brow shell whose strands peak at 0.25) would be cut away completely at
        # EFT's _Cutoff: stretch them so their brightest strands reach 1
        top = float(np.percentile(a[..., ch], 99.9))
        if kind == "slot" and not hair_name and top < 0.9:
            continue          # a real cut-out map reaches full white (soft glass / infrared don't)
        gain = 1.0 / max(top, 0.05) if top < 0.9 and hair_name else 1.0
        if gain != 1.0:
            a = a.copy()
            a[..., ch] = np.clip(a[..., ch] * gain, 0.0, 1.0)
        h, w = a.shape[:2]
        uv = faces_uv.reshape(-1, 2)
        x = np.clip((uv[:, 0] * w).astype(np.int64), 0, w - 1)
        y = np.clip((uv[:, 1] * h).astype(np.int64), 0, h - 1)
        v = a[y, x, ch].reshape(faces_uv.shape[0], faces_uv.shape[1])
        T = float((faces_w * (v < PICK_CUTOFF).mean(1)).sum() / wsum)
        M = float((faces_w * ((v.min(1) < 0.25) & (v.max(1) > 0.6))).sum() / wsum)
        if kind in ("colour", "decal"):
            # (as before) the colour alpha of a hair / lash / brow material is its cut-out when
            # it is two-level: hair shells over the scalp fade out in big areas (few strands);
            # the same for decals on black (_decal_alpha)
            al = a[..., 3]
            two_level = (al < 0.2).mean() > 0.05 and (al > 0.8).mean() > 0.05
        ok = (T >= 0.02 if kind == "named" else
              (T >= 0.1 and (M >= 0.2 and T <= 0.98 or hair_name)) if kind == "slot" else
              (T >= 0.02 and two_level))
        if ok:
            return p, ch, gain, (f"cut-out={os.path.basename(p)} ({sem}; cuts {T:.0%} of the "
                                 f"surface, strands in {M:.0%} of it" +
                                 (f"; soft map, x{gain:.1f}" if gain != 1.0 else "") + ")")
    return None


# ---------------------------------------------------------------------------------------------
# atlas
# ---------------------------------------------------------------------------------------------
PACK_ORDERS = (lambda r: (-r[1], -r[0]),              # tallest first
               lambda r: (-r[0] * r[1], -r[1]),        # biggest first
               lambda r: (-max(r), -min(r)),           # longest side first
               lambda r: (-r[0], -r[1]))               # widest first


def _pack(rects, W, H, pad, order_key=PACK_ORDERS[0]):
    """Skyline bottom-left packing of rects [(w, h)] (+pad all round) into W x H.
    Returns [(x, y)] or None.  (Letting pieces turn 90 degrees was measured on the test
    characters: +0 % texture detail, so pieces are never turned.)"""
    order = sorted(range(len(rects)), key=lambda i: order_key(rects[i]))
    sky = [(0, 0, W)]                       # segments (x, y, width)
    pos = [None] * len(rects)
    for i in order:
        w, h = rects[i][0] + 2 * pad, rects[i][1] + 2 * pad
        best = None
        for k in range(len(sky)):
            x = sky[k][0]
            if x + w > W:
                break
            # height of the skyline under [x, x + w)
            y, rem, j = 0, w, k
            while rem > 0 and j < len(sky):
                y = max(y, sky[j][1])
                rem -= sky[j][2] if j > k else sky[j][2] - (x - sky[j][0])
                j += 1
            if y + h <= H and (best is None or (y, x) < (best[1], best[0])):
                best = (x, y)
        if best is None:
            return None
        x, y = best
        pos[i] = (x, y)
        # update the skyline
        new, x1 = [], x + w
        for (sx, sy, sw) in sky:
            ex = sx + sw
            if ex <= x or sx >= x1:
                new.append((sx, sy, sw))
                continue
            if sx < x:
                new.append((sx, sy, x - sx))
            if ex > x1:
                new.append((x1, sy, ex - x1))
        new.append((x, y + h, w))
        new.sort()
        merged = []
        for seg in new:
            if merged and merged[-1][1] == seg[1] and merged[-1][0] + merged[-1][2] == seg[0]:
                merged[-1] = (merged[-1][0], seg[1], merged[-1][2] + seg[2])
            else:
                merged.append(seg)
        sky = merged
    return pos


def plan_atlas(items, W, H, pad=4, min_px=2, grow=True, surface=None):
    """items: [(key, weight, cap, rects, asp)] - one per texture set.  Each set is drawn at
    k = min(s * weight, cap) atlas pixels per UV unit (geometric mean; k * sqrt(asp) across,
    k / sqrt(asp) up, so the texture keeps its pixel aspect), each of its UV rects
    (u0, v0, u1, v1) becoming one atlas rect.  Finds the largest s for which all rects pack
    (even texel density).  With grow, it then tries keeping the 1 - 3 largest sets at that
    size and growing the rest further - a few big pieces often leave the atlas a third empty -
    and keeps whichever layout gives the most texture detail over the whole surface.
    Returns ({key: k}, {(key, i): (x, y, w, h)}) or None - inner rects in pixels."""
    flat = [(key, i, r, asp) for key, _, _, rects, asp in items for i, r in enumerate(rects)]
    wt = {key: (w, c) for key, w, c, _, _ in items}

    def ks_for(s, fixed):
        return {key: fixed[key] if key in fixed else min(s * w, c) for key, (w, c) in wt.items()}

    def sizes(ks):
        out = []
        for key, i, r, asp in flat:
            k = ks[key]
            # no clamping to the atlas: a rect that is too big must make the packing fail
            out.append((max(min_px, int(math.ceil((r[2] - r[0]) * k * math.sqrt(asp))) + 2),
                        max(min_px, int(math.ceil((r[3] - r[1]) * k / math.sqrt(asp))) + 2)))
        return out

    def pack(rects):
        for ok in PACK_ORDERS:                  # the first order that fits
            pos = _pack(rects, W, H, pad, ok)
            if pos is not None:
                return pos
        return None

    def solve(fixed, lo=1e-3, hi=1e8):
        best = None
        if fixed:
            pos = pack(sizes(ks_for(lo, fixed)))
            if pos is None:
                return None
            best = (lo, sizes(ks_for(lo, fixed)), pos)
        for _ in range(48):
            s = math.sqrt(lo * hi)
            rects = sizes(ks_for(s, fixed))
            pos = pack(rects)
            if pos is None:
                hi = s
            else:
                lo = s
                best = (s, rects, pos)
            if hi / lo < 1.002:
                break
        return best

    def detail(ks):          # surface-weighted log texel density (surface = {key: area})
        return sum((surface[key] if surface else w * w) * math.log(max(ks[key], 1e-9))
                   for key, (w, c) in wt.items())

    best = solve({})
    if best is None:
        return None
    s0 = best[0]
    ks0 = ks_for(s0, {})
    best_ks, best_sol = ks0, best
    if grow and len(items) > 1:
        area = {key: sum((r[2] - r[0]) * (r[3] - r[1]) for r in rects) * ks0[key] ** 2
                for key, _, _, rects, _ in items}
        order = [k for k in sorted(area, key=lambda k: -area[k]) if ks0[k] < wt[k][1] * 0.999]
        for n in range(1, min(3, len(order) - 1) + 1):
            fixed = {k: ks0[k] for k in order[:n]}
            sol = solve(fixed, lo=s0)
            if sol is None:
                continue
            ks = ks_for(sol[0], fixed)
            if detail(ks) > detail(best_ks) + 1e-9:
                best_ks, best_sol = ks, sol
    s, rects, pos = best_sol
    return best_ks, {(flat[j][0], flat[j][1]): (pos[j][0] + pad, pos[j][1] + pad) + rects[j]
                     for j in range(len(flat))}


def uv_rects(fmin, fmax, G=64, max_rects=48):
    """Rectangles (UV 0..1) that together cover faces with UV bounding boxes fmin / fmax:
    regions that are connected on a G x G grid (1 cell of slack), each cut to the exact bounds
    of its faces, merged where they overlap.  Returns (rects, rect index per face)."""
    n = len(fmin)
    if n == 0:
        return [FULL], np.zeros(0, int)
    c0 = np.clip(np.floor(fmin * G).astype(int), 0, G - 1)
    c1 = np.clip(np.floor(fmax * G).astype(int), 0, G - 1)
    occ = np.zeros((G, G), bool)                               # [v, u]
    for i in range(n):
        occ[c0[i, 1]:c1[i, 1] + 1, c0[i, 0]:c1[i, 0] + 1] = True
    dil = occ.copy()
    dil[1:] |= occ[:-1]
    dil[:-1] |= occ[1:]
    d2 = dil.copy()
    d2[:, 1:] |= dil[:, :-1]
    d2[:, :-1] |= dil[:, 1:]
    lab = -np.ones((G, G), int)
    k = 0
    for v, u in zip(*np.nonzero(d2)):
        if lab[v, u] >= 0:
            continue
        lab[v, u] = k
        stack = [(v, u)]
        while stack:
            y, x = stack.pop()
            for yy, xx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
                if 0 <= yy < G and 0 <= xx < G and d2[yy, xx] and lab[yy, xx] < 0:
                    lab[yy, xx] = k
                    stack.append((yy, xx))
        k += 1
    comp = lab[c0[:, 1], c0[:, 0]]
    rmin = np.full((k, 2), 2.0)
    rmax = np.full((k, 2), -1.0)
    np.minimum.at(rmin, comp, fmin)
    np.maximum.at(rmax, comp, fmax)
    rects = [[*rmin[i], *rmax[i]] for i in range(k) if rmax[i, 0] >= 0]
    ids = [i for i in range(k) if rmax[i, 0] >= 0]
    owner = {c: j for j, c in enumerate(ids)}
    parent = list(range(len(rects)))
    tol = 1.0 / G
    changed = True
    while changed:                                             # merge overlapping rects
        changed = False
        live = [j for j in range(len(rects)) if parent[j] == j]
        for ai, a in enumerate(live):
            for b in live[ai + 1:]:
                if parent[b] != b or parent[a] != a:
                    continue
                ra, rb = rects[a], rects[b]
                if ra[0] < rb[2] + tol and rb[0] < ra[2] + tol and \
                        ra[1] < rb[3] + tol and rb[1] < ra[3] + tol:
                    rects[a] = [min(ra[0], rb[0]), min(ra[1], rb[1]),
                                max(ra[2], rb[2]), max(ra[3], rb[3])]
                    parent[b] = a
                    changed = True
    def root(j):
        while parent[j] != j:
            j = parent[j]
        return j
    live = [j for j in range(len(rects)) if parent[j] == j]
    if len(live) > max_rects and G > 8:
        return uv_rects(fmin, fmax, G // 2, max_rects)
    # one rect for everything when the pieces would save little
    union = [min(rects[j][0] for j in live), min(rects[j][1] for j in live),
             max(rects[j][2] for j in live), max(rects[j][3] for j in live)]
    area = lambda r: max(r[2] - r[0], 1e-6) * max(r[3] - r[1], 1e-6)
    if len(live) == 1 or sum(area(rects[j]) for j in live) > 0.8 * area(union):
        return [tuple(union)], np.zeros(n, int)
    idx = {j: i for i, j in enumerate(live)}
    face_rect = np.array([idx[root(owner[c])] for c in comp])
    return [tuple(rects[j]) for j in live], face_rect


def _loop_arrays(me, uv_layer):
    nl = len(me.loops)
    uv = np.empty(nl * 2, np.float64)
    uv_layer.data.foreach_get("uv", uv)
    uv = uv.reshape(-1, 2)
    npoly = len(me.polygons)
    mi = np.empty(npoly, np.int64)
    me.polygons.foreach_get("material_index", mi)
    lt = np.empty(npoly, np.int64)
    me.polygons.foreach_get("loop_total", lt)
    return uv, mi, lt


def _material_areas(o):
    me = o.data
    mi = np.empty(len(me.polygons), np.int64)
    me.polygons.foreach_get("material_index", mi)
    ar = np.empty(len(me.polygons))
    me.polygons.foreach_get("area", ar)
    out = {}
    for i, a in zip(mi, ar):
        out[i] = out.get(i, 0.0) + a
    return out


def is_alpha_material(name):
    low = base_name(name).lower()
    return any(w in low for w in ALPHA_WORDS)


def _texture_res(roles):
    """(width, height) of the material's most detailed texture (colour, NOG / normal, gloss),
    or None - a 1 x 1 colour with a 1024 normal map still needs 1024 of detail."""
    best = None
    for r in ("color", "nog", "normal", "normal_ho", "gloss"):
        if r in roles:
            try:
                wh = image_size(roles[r])
            except Exception:
                continue
            if best is None or wh[0] * wh[1] > best[0] * best[1]:
                best = wh
    return best


# ---------------------------------------------------------------------------------------------
# enc=3: the material look baked into the pixels (docs/MATERIALS_PLAN.md, "enc=3 contract")
# ---------------------------------------------------------------------------------------------
# Unity gives enc=3 textures EFT's NEUTRAL clothing values (as all 12 vanilla clothing materials
# measured: _Glossness 1, _Specularness 1) with the vanilla median _SpecVals / _DefVals /
# _ReflectColor of the part (the presets EFTMaterialCore.Upper / Lower / Head / Hands).  So the
# G-buffer gets  specular = _d.a x SpecVals.x / 2  (at F = 0)  and  smoothness = _g, and the
# textures carry the target values directly:
#   _d.a = COD F0 / (SpecVals.x / 2)          (COD's specular was already close to vanilla)
#   _g   = gloss curve of the material's class (quantile map, COD -> vanilla EFT smoothness)
MATERIAL_MODES = ("ENC2", "ENC3")
#          _Glossness  _Specularness  _SpecVals   _DefVals   - Unity 1.7.0's enc=3 values
EFT_NEUTRAL = {"Upper": (1.0, 1.0, (1.1, 2.0), (0.85, 0.7)),
               "Lower": (1.0, 1.0, (1.1, 2.0), (0.85, 0.7)),
               "Head": (1.0, 1.0, (1.0, 3.0), (0.8, 1.0)),
               "Hands": (1.0, 1.0, (1.1, 2.0), (0.8, 0.7))}
CLASSES = ("cloth", "skin", "leather", "metal", "glass", "cutout")
CLASS_LABEL = {"cloth": "cloth", "skin": "skin", "leather": "leather / rubber / plastic",
               "metal": "metal", "glass": "glass / lens", "cutout": "hair / cut-out"}
# name words (whole words of the COD material name; most IW names are hashed, so these only
# catch the readable ones - "handcuffs" is not "hand", "armor" is not "arm")
# 2.6.12 (labelled check of 259 materials, docs/MATERIALS_PLAN.md): "goggle" names the frame
# (usa_goggle_01) as often as the lens (usa_goggle_01_transparent), so it moved to leather and
# "transparent" marks the lens; "straps" were webbing (cloth) in all 4 labelled cases;
# headsets / headphones / ear comms / knee pads are hard plastic.  A cloth word wins over the
# leather words (headset_wrap is cloth).
GLASS_WORDS = {"glass", "lens", "lenses", "visor", "transparent"}
LEATHER_WORDS = {"leather", "glove", "gloves", "boot", "boots", "shoe", "shoes", "holster",
                 "plastic", "rubber", "rubberband", "strap", "ziptie", "handcuff",
                 "handcuffs", "sole", "soles", "goggle", "goggles", "headset", "headsets",
                 "headphone", "headphones", "comm", "kneepad", "kneepads", "elbowpad",
                 "elbowpads"}
CLOTH_WORDS = {"wrap", "cloth", "fabric"}
SKIN_WORDS = {"skin", "arm", "arms", "hand", "hands", "viewarm", "viewarms", "teeth",
              "mouth", "lips", "tongue"}
# hunch (to validate on labelled materials): a non-metal whose median COD gloss is this high is a
# hard surface - in the test set these are dark grey gear pieces, patches, handcuffs, a holster
LEATHER_GLOSS = 0.6
# glass has no transparency in EFT's character shaders: opaque, tinted and smooth (hunches)
GLASS_SMOOTH = 0.85
GLASS_TINT = 0.3
# Gloss transfer curves: COD gloss -> EFT smoothness, fitted by tools/fit_gloss_curves.py
# (docs/gloss_curves.json): (COD knots, EFT knots), both rising; np.interp between them.
# Quantile maps at 5 % steps from the COD distribution of the class (174 test materials, classes
# from classify_material) onto vanilla EFT smoothness (15 bundles, UV-covered texels):
#   cloth    COD median 0.34 -> 0.17   (vanilla: 12 clothing materials)
#   skin     COD median 0.47 -> 0.32   (vanilla: the 2 heads, all covered texels)
#   leather  COD median 0.65 -> 0.27   (HUNCH: the glossier half of vanilla clothing - no vanilla
#                                       gloves / holsters measured yet)
#   metal    COD median 0.69 -> 0.67   (vanilla: texels with _MainTex.a > 0.5 in 2 pants)
# Line lengths: generated table.
GLOSS_CURVES = {
    "cloth": ([0.0, 0.0865, 0.1333, 0.1787, 0.1975, 0.2396, 0.2463, 0.2784, 0.318, 0.3255, 0.336, 0.3529, 0.3801, 0.4011, 0.4278, 0.4495, 0.4781, 0.507, 0.5612, 0.632, 1.0],
              [0.0, 0.051, 0.0706, 0.0863, 0.098, 0.1098, 0.1216, 0.1333, 0.1451, 0.1569, 0.1686, 0.1843, 0.2, 0.2196, 0.2416, 0.2667, 0.302, 0.3647, 0.451, 0.5569, 0.9249]),
    "skin": ([0.0, 0.0004, 0.1256, 0.232, 0.3979, 0.417, 0.4272, 0.4382, 0.4515, 0.4672, 0.4739, 0.4817, 0.4909, 0.5007, 0.5101, 0.521, 0.5513, 0.5907, 0.6471, 0.681, 1.0],
              [0.0, 0.1542, 0.1885, 0.2059, 0.2399, 0.2529, 0.2571, 0.2742, 0.2913, 0.3059, 0.3235, 0.3412, 0.3647, 0.3882, 0.4118, 0.4353, 0.4529, 0.4706, 0.5313, 0.7026, 1.0]),
    "leather": ([0.0, 0.4039, 0.4902, 0.5466, 0.5711, 0.5892, 0.602, 0.6118, 0.6225, 0.6353, 0.6466, 0.6618, 0.6757, 0.6863, 0.699, 0.71, 0.7306, 0.7603, 0.8098, 0.8497, 1.0],
              [0.0, 0.1765, 0.1843, 0.1922, 0.2, 0.2098, 0.2196, 0.2306, 0.2416, 0.2541, 0.2667, 0.2843, 0.302, 0.3333, 0.3647, 0.4078, 0.451, 0.5039, 0.5569, 0.761, 0.9113]),
    "metal": ([0.0, 0.2713, 0.4216, 0.4625, 0.4931, 0.5196, 0.5493, 0.5725, 0.6225, 0.6532, 0.6902, 0.7203, 0.7382, 0.7588, 0.7686, 0.7801, 0.8206, 0.8613, 0.8814, 0.9127, 1.0],
              [0.0, 0.4196, 0.4392, 0.4549, 0.4706, 0.4863, 0.5216, 0.5765, 0.6235, 0.6549, 0.6667, 0.6745, 0.6824, 0.6902, 0.702, 0.7098, 0.7137, 0.7216, 0.7333, 0.7608, 0.8481]),
}


def _name_words(names):
    out = set()
    for n in names:
        out.update(w for w in re.split(r"[^a-z]+", base_name(n).lower()) if w)
    return out


def skin_tone(rgb):
    """A skin colour (mean colour of a material, sRGB 0..1): hue 5 - 28 degrees (red-orange),
    saturation 0.15 - 0.6, max channel >= 0.3.  Checked by eye on the 174 test materials (2.6.0):
    skin (faces, torsos, arms) sits at hue 7 - 24, saturation 0.2 - 0.52, max 0.39 - 0.73; khaki /
    tan gear at 34 - 45 degrees; the misses left: a dark brown pouch (hue 15, max 0.35) - and
    eyes, hair and a bald scalp colour, which are fine as skin or become cut-outs."""
    r, g, b = (float(x) for x in rgb[:3])
    mx, mn = max(r, g, b), min(r, g, b)
    if mx < 0.3 or r < mx or mx - mn < 1e-3:
        return False
    sat = (mx - mn) / mx
    hue = 60.0 * (g - b) / (mx - mn)
    return 5.0 <= hue <= 28.0 and 0.15 <= sat <= 0.6


def classify_material(names, st, cutout=False):
    """Material class of a COD material (or of the materials sharing one texture set).
    st: stats on its faces - gloss_med, metal (share of the surface with metal share > 0.5),
    rgb (mean colour).  Returns (class, why)."""
    if cutout:
        return "cutout", "cut-out cards"
    words = _name_words(names)
    hit = sorted(words & GLASS_WORDS)
    if hit:
        return "glass", f"name '{hit[0]}'"
    if st["metal"] > 0.5:
        return "metal", f"{st['metal']:.0%} metal"
    hit = sorted(words & LEATHER_WORDS)
    if hit and not words & CLOTH_WORDS:
        return "leather", f"name '{hit[0]}'"
    hit = sorted(words & SKIN_WORDS)
    if hit:
        return "skin", f"name '{hit[0]}'"
    if st["metal"] < 0.2 and skin_tone(st["rgb"]):
        return "skin", "skin colour"
    if st["metal"] < 0.05 and st["gloss_med"] >= LEATHER_GLOSS:
        return "leather", "high COD gloss"
    return "cloth", "default"


def _wquant(a, w, q):
    o = np.argsort(a)
    cw = np.cumsum(w[o])
    cw = cw / max(cw[-1], 1e-12)
    return float(a[o][min(np.searchsorted(cw, q), len(a) - 1)])


def material_stats(d, g, m, px, w):
    """Stats of one material's maps at its face sample points px ((n, 2) int col, row)."""
    c, r = px[:, 0], px[:, 1]
    w = np.asarray(w, np.float64)
    w = w / max(w.sum(), 1e-12)
    gl = g[r, c, 0].astype(np.float64)
    mm = m[r, c] if m is not None else np.zeros(len(c))
    return {"gloss_med": _wquant(gl, w, 0.5), "metal": float(w[mm > 0.5].sum()),
            "rgb": [float(x) for x in (d[r, c, :3] * w[:, None]).sum(0)]}


def gloss_curve(cls, g):
    xs, ys = GLOSS_CURVES[cls]
    return np.interp(g, xs, ys).astype(np.float32)


def bake_enc3(d, g, m, cls, part, gloss_match=1.0):
    """d (H,W,4: colour, F0), g (H,W,1: COD gloss), m (H,W) metal share or None -> the enc=3
    d, g in place: _d.a = F0 / (SpecVals.x / 2), _g = the class's gloss curve, blended per pixel
    towards the metal curve by the metal share.  Returns the share of pixels whose F0 was cut
    at _d.a = 1 (F0 above SpecVals.x / 2 = 0.5 - 0.55: bright metal)."""
    k0 = EFT_NEUTRAL.get(part, EFT_NEUTRAL["Upper"])[2][0] / 2.0
    f0 = d[..., 3]
    if cls == "glass":
        d[..., :3] *= GLASS_TINT
        g[...] = GLASS_SMOOTH
        f0 = np.maximum(f0, F0_DIELECTRIC)
    elif cls != "cutout":
        base = "leather" if cls == "metal" else cls
        g0 = g[..., 0]
        gb = gloss_curve(base, g0)
        if m is not None and m.shape == g0.shape and float(m.max()) > 0:
            gb = gb * (1 - m) + gloss_curve("metal", g0) * m
        if gloss_match != 1.0:
            # "Gloss match" (2.6.4): 0 = COD's own gloss, 1 = the full vanilla curve
            gb = g0 + (gb - g0) * gloss_match
        g[..., 0] = gb
    else:
        return 0.0
    a = f0 / k0
    clipped = float((a > 1.0).mean())
    d[..., 3] = np.clip(a, 0, 1)
    return clipped


def load_class_overrides(path):
    """{material name: class} from a JSON file ({"name": "skin", ...}); unknown classes dropped."""
    try:
        with open(io_path(path), "r", encoding="utf-8") as fh:
            raw = json.load(fh)
    except (OSError, ValueError):
        return {}
    return {base_name(str(k)): str(v).lower() for k, v in raw.items()
            if str(v).lower() in CLASSES and str(v).lower() != "cutout"}


def uv_tile_counts(centres):
    """{(u tile, v tile): faces} from the faces' UV centres.  convert_part wraps every face by its
    centre's tile, i.e. it reads UVs outside 0..1 as a repeating texture."""
    t = np.floor(np.asarray(centres, np.float64)).astype(int)
    keys, n = np.unique(t, axis=0, return_counts=True) if len(t) else ([], [])
    return {(int(k[0]), int(k[1])): int(c) for k, c in zip(keys, n)}


def tile_text(tiles):
    return ", ".join(f"u{u:+d} v{v:+d}: {n}" if (u, v) != (0, 0) else f"u0 v0: {n}"
                     for (u, v), n in sorted(tiles.items()))


def cod_listed_images(mat, model_file):
    """How many different images a COD material's export lists (Greyhound / _mat_info file),
    constants such as $white left out.  None when there is no list.  COD-specific."""
    name = base_name(mat.name if hasattr(mat, "name") else mat)
    folder = os.path.dirname(model_file) if model_file else ""
    if not folder or not os.path.isdir(folder):
        return None
    folder = _sibling_export(folder, name) or folder
    info = semantics_file(folder, name)
    if not info:
        return None
    return len({img for _, img in _parse_semantics(info) if img and not img.startswith("$")})


def convert_part(o, out_dir, basename, size=2048, spec_scale=SPEC_SCALE, ao_strength=1.0,
                 log=print, ao_in_spec=True, normal_style="OPENGL", uv_layout="ISLANDS",
                 metal_keep=METAL_KEEP, material_mode="ENC2", class_overrides=None,
                 colour_gain=1.0, colour_sat=1.0, gloss_match=1.0):
    """Replace the materials of converted part `o` by one EFT material (+ one cut-out material
    for hair/lash cards if any) with atlas textures written to out_dir.  Returns dict.
    uv_layout "ISLANDS": only the parts of each COD texture the mesh uses go into the atlas,
    sized for an even texel density on the model; "WHOLE": every COD texture goes in whole.
    material_mode "ENC2": COD's values as they are (Unity calibrates per part); "ENC3": each COD
    material is classified and its look baked into the pixels for EFT's neutral values
    (bake_enc3).  class_overrides: {COD material name: class} that win over the classifier."""
    enc3 = material_mode == "ENC3"
    class_overrides = class_overrides or {}
    part = o.get("cod2eft_part") or o.name.rsplit("_", 1)[-1]
    me = o.data
    if not me.uv_layers:
        log(f"  {o.name}: no UV map - textures skipped")
        return None
    src = json.loads(o.get("cod2eft_mat_src", "{}"))
    uv_src = me.uv_layers[0]
    restored = _restore_originals(o)
    if restored:
        log("  (re-run: starting again from the original COD materials and UVs)")
    elif "cod2eft_orig_mats" in o:
        log(f"  {o.name}: textures were already converted and the original COD materials are "
            "no longer in this file - re-import to convert again")
        return None
    uv, mi, lt = _loop_arrays(me, uv_src)
    if "COD_original_UV" in me.uv_layers:
        uv = np.empty(len(me.loops) * 2, np.float64)
        me.uv_layers["COD_original_UV"].data.foreach_get("uv", uv)
        uv = uv.reshape(-1, 2)
    areas = _material_areas(o)
    groups = {"main": [], "alpha": []}
    mats = {}
    for idx, a in areas.items():
        if idx >= len(me.materials) or me.materials[idx] is None:
            continue
        mats[idx] = me.materials[idx]
    # per-face tile shift (wrapped UVs -> 0..1) and faces that cross tiles
    nf = len(mi)
    fid = np.repeat(np.arange(nf), lt)
    cnt = np.bincount(fid, minlength=nf)
    cen = np.stack([np.bincount(fid, uv[:, k], minlength=nf) for k in (0, 1)], 1) / \
        np.maximum(cnt, 1)[:, None]
    shift = np.floor(cen)
    local = uv - shift[fid]
    outside = ((local < -0.01) | (local > 1.01)).any(1)
    crossing = int(np.unique(fid[outside]).size)
    local = np.clip(local, 0.0, 1.0)
    # per-face UV bounds and UV area (shoelace), per-face 3D area in world units
    fmin = np.full((nf, 2), 2.0)
    fmax = np.full((nf, 2), -1.0)
    np.minimum.at(fmin, fid, local)
    np.maximum.at(fmax, fid, local)
    ls = np.concatenate([[0], np.cumsum(lt)[:-1]]) if nf else np.zeros(0, int)
    nxt = np.arange(len(local)) + 1
    last = ls + lt - 1
    nxt[last] = ls
    cr = local[:, 0] * local[nxt, 1] - local[nxt, 0] * local[:, 1]
    uv_area = np.abs(np.bincount(fid, cr, minlength=nf)) * 0.5
    far = np.empty(nf)
    me.polygons.foreach_get("area", far)
    sc = o.matrix_world.to_3x3().determinant()
    far = far * abs(sc) ** (2.0 / 3.0)
    new_uv = uv.copy()
    result = {"part": part, "materials": len(mats), "files": [], "notes": [], "density": {},
              "classes": [], "tiles": []}
    new_mats = []
    # textures of every material, and whether it is cut out (hair / lash / fringe cards):
    # sample points inside each face = its corners, its centre, and corner-centre midpoints
    cen_l = np.clip(cen - shift, 0.0, 1.0)
    mid_l = (local + cen_l[fid]) * 0.5
    found = {}
    for idx in sorted(mats):
        mat = mats[idx]
        mfile = src.get(base_name(mat.name)) or src.get(mat.name)
        roles, how = find_textures(mat, mfile)
        faces = np.nonzero(mi == idx)[0]
        op = None
        if roles.get("opacity_cands") and len(faces):
            fl = np.isin(fid, faces)
            kmax = int(lt[faces].max())
            K = 2 * kmax + 1
            S = np.repeat(cen_l[faces][:, None, :], K, axis=1)       # pad with the centre
            pos = {f: i for i, f in enumerate(faces)}
            li = np.nonzero(fl)[0]
            rowf = np.array([pos[f] for f in fid[li]])
            k = li - ls[fid[li]]
            S[rowf, 1 + k] = local[li]
            S[rowf, 1 + kmax + k] = mid_l[li]
            op = _pick_opacity(roles["opacity_cands"], roles.get("color"), S, far[faces],
                               hair_name=is_alpha_material(mat.name))
        if op:
            roles["opacity"] = (op[0], op[1], op[2])
            how.append(op[3])
        elif roles.get("fused") and len(faces):
            # what the colour alpha means for this material (metal mask or not), judged on the
            # faces that use it: centres and corner-centre midpoints, weighted by area
            fl = np.isin(fid, faces)
            pts = np.concatenate([cen_l[faces], mid_l[fl]])
            wts = np.concatenate([far[faces] * 0.5, far[fid[fl]] * 0.5 / lt[fid[fl]]])
            note = classify_alpha(roles, pts, wts)
            if note:
                how.append(note)
        found[idx] = roles
        groups["alpha" if op else "main"].append(idx)
        tl = uv_tile_counts(cen[faces])
        if len(tl) > 1 or (tl and (0, 0) not in tl):
            result["tiles"].append({"material": base_name(mat.name), "tiles": tl,
                                    "images": cod_listed_images(mat, mfile)})
        log(f"  {base_name(mat.name)}: " + (", ".join(how) if how else "no textures found"))
        if roles.get("missing"):
            k, n = roles["missing"]
            result["notes"].append(
                f"{base_name(mat.name)}: {k} image(s) its export lists are missing from the "
                f"_images folder. Their paths would be up to {n} characters - over Windows' "
                "260 limit, a common reason an exporter skips files: export to a shorter folder "
                "(e.g. C:\\COD\\) and convert again")
    for gname, idxs in groups.items():
        if not idxs:
            continue
        # materials that use exactly the same textures share their texture space
        cells = {}
        for idx in idxs:
            key = tuple(sorted((k, v) for k, v in found[idx].items()
                               if isinstance(v, (str, tuple))))
            key = key or ("none", idx)
            cells.setdefault(key, []).append(idx)
        if not any(k in r for r in found.values() for k in ("color", "nog", "normal", "color_const")):
            result["notes"].append(f"{gname}: no COD textures found next to the model files "
                                   "(missing _images folder?) - COD materials kept")
            continue
        Wg = size if gname == "main" else max(256, size // 2)
        pad = 4
        items, cinfo = [], {}
        for key, members in cells.items():
            faces = np.nonzero(np.isin(mi, members))[0]
            a3 = float(far[faces].sum())
            uva = float(uv_area[faces].sum())
            res = _texture_res(found[members[0]])
            asp = res[0] / max(res[1], 1) if res else 1.0
            cap = math.sqrt(res[0] * res[1]) if res else 4.0     # no texture: flat grey
            if uv_layout == "ISLANDS":
                rects, frect = uv_rects(fmin[faces], fmax[faces])
                weight = math.sqrt(max(a3, 1e-12) / max(uva, 1e-6))
            else:
                rects, frect = [FULL], np.zeros(len(faces), int)
                weight = math.sqrt(max(a3, 1e-12))
            cinfo[key] = (members, faces, rects, frect, asp, a3, uva, cap)
            items.append((key, weight, cap, rects, asp))
        plan = plan_atlas(items, Wg, Wg, pad, surface={k: v[5] for k, v in cinfo.items()})
        if plan is None:
            result["notes"].append(f"{gname}: atlas packing failed")
            continue
        ks, place = plan
        D = np.zeros((Wg, Wg, 4), np.float32)
        N = np.zeros((Wg, Wg, 3), np.float32)
        N[..., :] = (0.5, 0.5, 1.0)
        G = np.full((Wg, Wg, 1), 0.5, np.float32)
        A = np.ones((Wg, Wg, 1), np.float32)
        used = 0.0
        for key, (members, faces, rects, frect, asp, a3, uva, cap) in cinfo.items():
            idx = members[0]
            k = ks[key]
            ku0, kv0 = k * math.sqrt(asp), k / math.sqrt(asp)
            # one map computation over the union of this texture's rects (+ padding)
            U = (max(0.0, min(r[0] for r in rects) - pad / ku0),
                 max(0.0, min(r[1] for r in rects) - pad / kv0),
                 min(1.0, max(r[2] for r in rects) + pad / ku0),
                 min(1.0, max(r[3] for r in rects) + pad / kv0))
            Wu = max(1, int(math.ceil((U[2] - U[0]) * ku0)))
            Hu = max(1, int(math.ceil((U[3] - U[1]) * kv0)))
            ku, kv = Wu / (U[2] - U[0]), Hu / (U[3] - U[1])      # exact sampling density
            try:
                d, n, g, info = material_maps(found[idx], Wu, Hu, spec_scale, ao_strength, U,
                                              ao_in_spec, metal_keep, colour_gain, colour_sat)
                am = alpha_mask(found[idx], Wu, Hu, U) if gname == "alpha" else None
            except Exception as e:
                # one unreadable image must not stop the whole character (before 2.4.3 it did:
                # the parts after it kept their COD materials) - this material comes out grey
                d = np.full((Hu, Wu, 4), 0.5, np.float32)
                d[..., 3] = F0_DIELECTRIC
                n = np.zeros((Hu, Wu, 3), np.float32)
                n[...] = (0.5, 0.5, 1.0)
                g = np.full((Hu, Wu, 1), 0.3, np.float32)
                am = np.zeros((Hu, Wu, 1), np.float32)     # cut-outs: hidden, not a grey card
                info = {}
                result["notes"].append(f"{base_name(mats[idx].name)}: could not read its "
                                       f"textures ({e}) - " +
                                       ("left out (cut away)" if gname == "alpha" else "grey"))
            if info.get("no_color"):
                result["notes"].append(f"{base_name(mats[idx].name)}: no colour map found - grey")
            if enc3:
                # class from the maps at the faces' centres (as tools/cod_survey.py samples them)
                names = [base_name(mats[i].name) for i in members]
                pc = cen_l[faces]
                px = np.stack([np.clip(((pc[:, 0] - U[0]) * ku).astype(int), 0, Wu - 1),
                               np.clip(((pc[:, 1] - U[1]) * kv).astype(int), 0, Hu - 1)], 1)
                mm = info.get("m")
                st = material_stats(d, g, mm, px, far[faces])
                cls, why = classify_material(names, st, gname == "alpha")
                auto = cls
                ov = next((class_overrides[nm] for nm in names if nm in class_overrides), None)
                if ov and gname == "main" and ov != cls:
                    cls, why = ov, f"override (auto: {auto}, {why})"
                g_before = st["gloss_med"]
                clipped = bake_enc3(d, g, mm, cls, part, gloss_match)
                g_after = material_stats(d, g, mm, px, far[faces])["gloss_med"]
                for nm in names:
                    result["classes"].append({"material": nm, "class": cls, "auto": auto,
                                              "why": why, "gloss": round(g_before, 3),
                                              "smooth": round(g_after, 3),
                                              "metal": round(st["metal"], 3),
                                              "clipped": round(clipped, 3)})
            tiles = [(D, d), (N, n), (G, g)]
            if gname == "alpha":
                tiles.append((A, am))
            loops_of = np.isin(fid, faces)
            face_pos = -np.ones(nf, int)
            face_pos[faces] = frect
            for i, r in enumerate(rects):
                x, y, w, h = place[(key, i)]
                cx0 = int(math.floor((r[0] - U[0]) * ku))
                cy0 = int(math.floor((r[1] - U[1]) * kv))
                cols = np.clip(np.arange(cx0 - pad, cx0 + w + pad), 0, Wu - 1)
                rows = np.clip(np.arange(cy0 - pad, cy0 + h + pad), 0, Hu - 1)
                for arr, tile in tiles:
                    arr[y - pad:y + h + pad, x - pad:x + w + pad] = tile[rows][:, cols]
                sel = loops_of & (face_pos[fid] == i)
                new_uv[sel, 0] = (x - cx0 + (local[sel, 0] - U[0]) * ku) / Wg
                new_uv[sel, 1] = (y - cy0 + (local[sel, 1] - U[1]) * kv) / Wg
                used += (w + 2 * pad) * (h + 2 * pad)
            del d, n, g, tiles
            clear_cache()
            if a3 > 0 and uva > 0:
                result["density"][base_name(mats[idx].name)] = (k * math.sqrt(uva / a3), a3,
                                                                  uva, len(rects), cap < 16)
        clear_cache()
        result.setdefault("fill", {})[gname] = used / (Wg * Wg)
        tag = f"{basename}_{part}" + ("" if gname == "main" else "_alpha")
        if gname == "alpha":
            D[..., 3:4] = A                                    # cut-out: alpha = opacity
        if normal_style == "DIRECTX":
            N[..., 1] = 1.0 - N[..., 1]                        # green down
        fd = os.path.join(out_dir, tag + "_d.png")
        fn = os.path.join(out_dir, tag + "_n.png")
        fg = os.path.join(out_dir, tag + "_g.png")
        mk = mark_text(material_mode, normal_style)
        imd = save_png(fd, D, srgb=True, mark=mk)
        imn = save_png(fn, N, srgb=False, mark=mk)
        img = save_png(fg, G, srgb=False, mark=mk)
        result["files"] += [fd, fn, fg]
        new_mats.append((gname, idxs, _make_material(tag, imd, imn, img, gname == "alpha",
                                                     normal_style, part, material_mode)))
    # swap in the new UVs and materials
    if not new_mats:
        return result
    if "cod2eft_orig_mats" not in o:
        o["cod2eft_orig_mats"] = json.dumps([m.name if m else "" for m in me.materials])
        # keep the COD materials in the .blend (fake user) so Convert Textures can run again
        # later - e.g. after Separate by COD Material or joining pieces
        for m in me.materials:
            if m is not None:
                m.use_fake_user = True
        at = me.attributes.get("cod2eft_orig_mat") or \
            me.attributes.new("cod2eft_orig_mat", "INT", "FACE")
        at.data.foreach_set("value", mi.astype(np.int32))
    uv_name = uv_src.name
    if "COD_original_UV" in me.uv_layers:
        pass
    elif len(me.uv_layers) < 8:
        keep = me.uv_layers.new(name="COD_original_UV")
        if keep is not None:
            keep.data.foreach_set("uv", uv.ravel())
    me.uv_layers[uv_name].data.foreach_set("uv", new_uv.ravel())
    me.uv_layers.active = me.uv_layers[uv_name]
    try:
        me.uv_layers[uv_name].active_render = True
    except AttributeError:
        pass
    remap = {}
    me.materials.clear()
    for k, (gname, idxs, mat) in enumerate(new_mats):
        me.materials.append(mat)
        for i in idxs:
            remap[i] = k
    new_mi = np.array([remap.get(i, 0) for i in mi], np.int64)
    me.polygons.foreach_set("material_index", new_mi)
    me.update()
    if crossing:
        result["notes"].append(f"{crossing} face(s) had UVs across a texture repeat - their "
                               "texture may look stretched")
    return result


def _restore_originals(o):
    """Put back the COD materials / material indices saved by an earlier texture run."""
    me = o.data
    if "cod2eft_orig_mats" not in o or "cod2eft_orig_mat" not in me.attributes:
        return False
    names = json.loads(o["cod2eft_orig_mats"])
    if any(n and n not in bpy.data.materials for n in names):
        return False
    mi = np.empty(len(me.polygons), np.int32)
    me.attributes["cod2eft_orig_mat"].data.foreach_get("value", mi)
    me.materials.clear()
    for n in names:
        me.materials.append(bpy.data.materials.get(n))
    me.polygons.foreach_set("material_index", mi)
    me.update()
    return True


# EFT's material values per part, as the Unity side (WTT-SDK Auto Prefabber) sets them -
# docs/COD2EFT_TEXTURE_SPEC.md, "Chosen values" (2026-09-27).  Only the Blender preview uses
# them; keep them in step with that table.
#          _Glossness  _Specularness  _SpecVals   _DefVals
EFT_PART = {"Upper": (2.4, 1.0, (1.1, 2.0), (0.85, 0.7)),
            "Lower": (2.0, 0.75, (1.1, 2.0), (0.85, 0.7)),
            "Head": (2.2, 0.6, (1.0, 3.0), (0.8, 1.0)),
            "Hands": (2.6, 0.8, (1.1, 2.0), (0.8, 0.7))}


def _math(nt, op, a=None, b=None, loc=(0, 0), label="", clamp=False):
    n = nt.nodes.new("ShaderNodeMath")
    n.operation = op
    n.location = loc
    n.use_clamp = clamp
    if label:
        n.label = label
    for i, v in enumerate((a, b)):
        if v is None:
            continue
        if isinstance(v, (int, float)):
            n.inputs[i].default_value = v
        else:
            nt.links.new(v, n.inputs[i])
    return n


def _make_material(name, imd, imn, img, cutout, normal_style="OPENGL", part=None,
                   material_mode="ENC2"):
    """Preview of the part as EFT draws it.  Main sets: EFT's deferred maths (character shader +
    its GGX lighting, as the Unity side found it):
      F          = (1 - N.V)^2 / 2
      colour     = _MainTex.rgb x (_DefVals.x + _DefVals.y F)
      specular   = _MainTex.a x _Glossness x (_SpecVals.x + _SpecVals.y F) / 2   (-> F0)
      smoothness = _SpecMap x _Specularness                                     (-> roughness)
    EFT lights in gamma space and Blender in linear space: the colour factor is converted
    (^2.2); the specular value is used as it is, so highlights are close, not exact.
    Cut-out sets: p0/Cutout/Bumped Diffuse - no specular at all.
    material_mode "ENC3": the neutral values Unity uses for enc=3 (EFT_NEUTRAL) instead of the
    per-part calibration (EFT_PART), so the preview shows what the textures ask for."""
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    table = EFT_NEUTRAL if material_mode == "ENC3" else EFT_PART
    gl, sp, sv, dv = table.get(part, table["Upper"])
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (700, 0)
    bs = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bs.location = (400, 0)
    nt.links.new(bs.outputs[0], out.inputs[0])
    td = nt.nodes.new("ShaderNodeTexImage")
    td.image = imd
    td.location = (-700, 300)
    td.label = "_MainTex (RGB colour, A specular)"
    tn = nt.nodes.new("ShaderNodeTexImage")
    tn.image = imn
    tn.location = (-700, -300)
    tn.label = "_BumpMap"
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nm.location = (100, -300)
    if normal_style == "DIRECTX":
        # Blender previews OpenGL maps: flip green back for the preview only (the PNG stays
        # DirectX): c * (1, -1, 1) + (0, 1, 0)
        fl = nt.nodes.new("ShaderNodeVectorMath")
        fl.operation = "MULTIPLY_ADD"
        fl.location = (-100, -400)
        fl.label = "DirectX -> OpenGL (preview)"
        fl.inputs[1].default_value = (1.0, -1.0, 1.0)
        fl.inputs[2].default_value = (0.0, 1.0, 0.0)
        nt.links.new(tn.outputs["Color"], fl.inputs[0])
        nt.links.new(fl.outputs[0], nm.inputs["Color"])
    else:
        nt.links.new(tn.outputs["Color"], nm.inputs["Color"])
    nt.links.new(nm.outputs["Normal"], bs.inputs["Normal"])
    tg = nt.nodes.new("ShaderNodeTexImage")
    tg.image = img
    tg.location = (-700, 0)
    tg.label = "_SpecMap (gloss)"
    for im in (imn, img):
        try:
            im.colorspace_settings.name = "Non-Color"
        except TypeError:
            pass
    spec_level = "Specular IOR Level" if "Specular IOR Level" in bs.inputs else "Specular"
    if cutout:
        # p0/Cutout/Bumped Diffuse: a pixel is drawn when _MainTex alpha >= _Cutoff, back faces
        # are culled, no specular
        td.image.alpha_mode = "STRAIGHT"
        td.label = "_MainTex (RGB colour, A cut-out)"
        tg.label = "_SpecMap (gloss - not used by EFT's cut-out shader)"
        nt.links.new(td.outputs["Color"], bs.inputs["Base Color"])
        gt = _math(nt, "GREATER_THAN", td.outputs["Alpha"], CUTOFF - 1e-4, (100, 250),
                   f"_Cutoff {CUTOFF}")
        nt.links.new(gt.outputs[0], bs.inputs["Alpha"])
        if spec_level in bs.inputs:
            bs.inputs[spec_level].default_value = 0.0
        bs.inputs["Roughness"].default_value = 1.0
        for attr, val in (("blend_method", "CLIP"), ("surface_render_method", "DITHERED"),
                          ("use_backface_culling", True)):
            try:
                setattr(mat, attr, val)
            except (AttributeError, TypeError):
                pass
        return mat
    td.image.alpha_mode = "CHANNEL_PACKED"
    # F = (1 - N.V)^2 / 2  (Layer Weight "Facing" = 1 - |N.V|)
    lw = nt.nodes.new("ShaderNodeLayerWeight")
    lw.location = (-700, 550)
    nt.links.new(nm.outputs["Normal"], lw.inputs["Normal"])
    f2 = _math(nt, "POWER", lw.outputs["Facing"], 2.0, (-500, 550))
    F = _math(nt, "MULTIPLY", f2.outputs[0], 0.5, (-350, 550), "F = (1 - N.V)^2 / 2").outputs[0]
    # colour x (_DefVals.x + _DefVals.y F), the factor gamma -> linear
    dvf = _math(nt, "MULTIPLY_ADD", F, dv[1], (-200, 450), f"_DefVals {dv[0]:g}, {dv[1]:g}")
    dvf.inputs[2].default_value = dv[0]
    dvl = _math(nt, "POWER", dvf.outputs[0], 2.2, (-50, 450), "gamma -> linear")
    mix = nt.nodes.new("ShaderNodeVectorMath")
    mix.operation = "MULTIPLY"
    mix.location = (150, 350)
    nt.links.new(td.outputs["Color"], mix.inputs[0])
    nt.links.new(dvl.outputs[0], mix.inputs[1])
    nt.links.new(mix.outputs[0], bs.inputs["Base Color"])
    # roughness = 1 - _SpecMap x _Specularness
    sm = _math(nt, "MULTIPLY", tg.outputs["Color"], sp, (-400, 0), f"_Specularness {sp:g}", True)
    rg = _math(nt, "SUBTRACT", 1.0, sm.outputs[0], (-200, 0), "roughness = 1 - smoothness")
    nt.links.new(rg.outputs[0], bs.inputs["Roughness"])
    # specular = _MainTex.a x _Glossness x (_SpecVals.x + _SpecVals.y F) / 2 -> F0 -> IOR:
    # F0 = ((n - 1) / (n + 1))^2  <=>  n = (1 + sqrt F0) / (1 - sqrt F0)
    if "IOR" in bs.inputs:
        svf = _math(nt, "MULTIPLY_ADD", F, sv[1], (-200, 250), f"_SpecVals {sv[0]:g}, {sv[1]:g}")
        svf.inputs[2].default_value = sv[0]
        k = _math(nt, "MULTIPLY", svf.outputs[0], gl / 2.0, (-50, 250), f"x _Glossness {gl:g} / 2")
        s0 = _math(nt, "MULTIPLY", k.outputs[0], td.outputs["Alpha"], (100, 250), "specular")
        cl = _math(nt, "MINIMUM", s0.outputs[0], 0.95, (100, 200))
        sq = _math(nt, "SQRT", cl.outputs[0], None, (100, 150))
        ad = _math(nt, "ADD", 1.0, sq.outputs[0], (250, 200))
        sb = _math(nt, "SUBTRACT", 1.0, sq.outputs[0], (250, 150))
        dvn = _math(nt, "DIVIDE", ad.outputs[0], sb.outputs[0], (250, 100), "IOR from F0")
        nt.links.new(dvn.outputs[0], bs.inputs["IOR"])
        if spec_level == "Specular IOR Level":
            bs.inputs[spec_level].default_value = 0.5
    mat["cod2eft_eft_part"] = part or ""
    if material_mode == "ENC3":
        mat["cod2eft_enc"] = 3
    elif "cod2eft_enc" in mat:
        del mat["cod2eft_enc"]
    return mat


def fbx_colour_links(objs):
    """Blender's FBX exporter only writes a colour texture that is linked straight into Base
    Color; the EFT preview has the _DefVals factor in between.  Links _MainTex straight in for
    the export (so the FBX names the same textures as before 2.4.3); returns the undo."""
    undo = []
    seen = set()
    for o in objs:
        for m in getattr(o.data, "materials", []) or []:
            if not m or m.name in seen or "cod2eft_eft_part" not in m or not m.node_tree:
                continue
            seen.add(m.name)
            nt = m.node_tree
            bs = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
            td = next((n for n in nt.nodes if n.type == "TEX_IMAGE" and
                       n.label.startswith("_MainTex")), None)
            if not bs or not td:
                continue
            sock = bs.inputs["Base Color"]
            old = sock.links[0].from_socket if sock.links else None
            if old is not None and old.node == td:
                continue
            nt.links.new(td.outputs["Color"], sock)
            undo.append((nt, old, sock))

    def restore():
        for nt, old, sock in undo:
            if old is not None:
                nt.links.new(old, sock)
    return restore


def convert_textures(objs, out_dir, basename, size=2048, spec_scale=SPEC_SCALE, ao_strength=1.0,
                     log=print, ao_in_spec=True, normal_style="OPENGL", uv_layout="ISLANDS",
                     metal_keep=METAL_KEEP, material_mode="ENC2", class_overrides=None,
                     colour_gain=1.0, colour_sat=1.0, gloss_match=1.0):
    log(f"Texture options: {size} px, normals {normal_style}, specular x{spec_scale:g}, "
        f"metal colour kept {metal_keep:g}, "
        f"AO {ao_strength:g} into colour" + (" and specular" if ao_in_spec and ao_strength > 0
                                              else "") +
        (", only the used parts of each texture" if uv_layout == "ISLANDS"
         else ", whole textures"))
    if colour_gain != 1.0 or colour_sat != 1.0:
        log(f"Colour adjusted: brightness x{colour_gain:g}, saturation x{colour_sat:g}")
    if material_mode == "ENC3":
        log("Materials: enc=3 - each COD material's class sets its gloss curve, baked into the "
            "textures for EFT's neutral values" +
            (f" ({len(class_overrides)} class override(s))" if class_overrides else "") +
            (f", gloss match {gloss_match:g}" if gloss_match != 1.0 else ""))
    res = []
    for o in objs:
        log(f"Textures for {o.name}:")
        r = convert_part(o, out_dir, basename, size, spec_scale, ao_strength, log,
                         ao_in_spec, normal_style, uv_layout, metal_keep, material_mode,
                         class_overrides, colour_gain, colour_sat, gloss_match)
        if r:
            res.append(r)
            for c in r.get("classes", []):
                log(f"  class {c['material']}: {c['class']} ({c['why']})" +
                    ("" if c["class"] == "cutout" else
                     f", gloss median {c['gloss']:.2f} -> smoothness {c['smooth']:.2f}") +
                    (f", {c['clipped']:.0%} of its specular cut at _d.a = 1"
                     if c["clipped"] >= 0.01 else ""))
            if r["files"]:
                log(f"  -> {r['materials']} material(s) combined into "
                    f"{', '.join(os.path.basename(f) for f in r['files'][::3])}")
                dens = {m: v for m, v in (r.get("density") or {}).items() if not v[4]}
                if dens:
                    ds = sorted(v[0] for v in dens.values())
                    low = min(dens, key=lambda m: dens[m][0])
                    pieces = sum(v[3] for v in dens.values())
                    fill = ", ".join(f"{g} {100 * f:.0f}%" for g, f in r.get("fill", {}).items())
                    log(f"  texel density on the model: typical {ds[len(ds) // 2]:.0f} px/m, "
                        f"lowest {dens[low][0]:.0f} px/m ({low}); {pieces} texture piece(s), "
                        f"atlas used: {fill}")
            for t in r.get("tiles", []):
                k = t["images"]
                log(f"  WARNING UV tiles: {t['material']} has faces outside the 0..1 UV square "
                    f"({tile_text(t['tiles'])}; {'?' if k is None else k} image(s) listed). "
                    "They are read as a repeating texture - check this material in the "
                    "preview (docs/UV_TILES.md)")
            for nt in r["notes"]:
                log("  NOTE " + nt)
    tiles = [f"{r['part']}: {t['material']}" for r in res for t in r.get("tiles", [])]
    try:
        bpy.context.scene["cod2eft_uv_tiles"] = json.dumps(tiles)
    except (AttributeError, TypeError):
        pass
    return res
