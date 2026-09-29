"""
COD2EFT Porter
==============

Pipeline (each step is also its own button):

  1. FIT    - Finds the imported COD armature(s) (FBX or Cast import both work) and the EFT
              armature ("Base Human*" bones). Computes the rotation / offset (and unit scale) that
              lines the COD skeleton up with EFT (least-squares over torso joints - replaces the
              manual "empty" step), builds a clean fit rig with the COD bone names, and poses it:
              spine/neck/feet copy the EFT directions, and arms/legs/fingers are aimed and
              stretched so shoulders, elbows, wrists, knuckles, knees and ankles land exactly on
              the EFT joints. The COD meshes are switched to that rig (same vertex-group names).
              -> You can inspect / tweak the pose here (pose mode on "COD2EFT_FitRig").
  2. CONVERT - Bakes the pose into the meshes, converts COD weights to EFT bone names
              (explicit map + nearest-mapped-ancestor fallback for helper / cloth bones),
              re-distributes forearm and thigh twist weights using the falloff measured from
              EFT's own meshes, limits to 4 influences, normalises, sorts the parts into
              Head / Upper / Lower, joins each part, and parents it to the EFT armature.

Nothing on the EFT armature is modified, so the result exports exactly like your template.

Customising:
  * cod2eft_bonemap.json  (next to this file, optional) - {"cod_bone_name": "EFT bone"} overrides.
    EFT bone can be written as "LForearm2" or "Base HumanLForearm2".
  * cod2eft_pose_tweaks.json - written by "Save Pose Tweaks"; re-applied automatically on
    every later FIT (your original "template" idea, but stored as corrections on top of the
    automatic fit so it carries over to every model).
"""

import bpy
import json
import re
import os
import math
import numpy as np
from mathutils import Matrix, Quaternion

ADDON_DIR = os.path.dirname(os.path.abspath(__file__))

# Add-on version - goes up with every update (keep bl_info in addon_init.py the same;
# build_addon.py refuses to build when they differ).  Shown at the top of the panel, in
# Preferences > Add-ons, and on every report / batch log.
VERSION = (2, 6, 0)
VERSION_STR = ".".join(str(v) for v in VERSION)
VERSION_RE = re.compile(r"^VERSION = \((\d+), (\d+), (\d+)\)", re.M)


def file_version(folder):
    """VERSION written in <folder>/cod2eft_porter.py (without importing it), or None."""
    try:
        with open(os.path.join(folder, "cod2eft_porter.py"), "r", encoding="utf-8") as fh:
            m = VERSION_RE.search(fh.read(4000))
        return tuple(int(x) for x in m.groups()) if m else None
    except (OSError, TypeError):
        return None


ADDON_PKG = ""          # set by the add-on UI module when running as an installed add-on


def data_dir():
    """Folder holding the user's cod2eft_bonemap.json / cod2eft_pose_tweaks.json.
    Order: env COD2EFT_DATA_DIR (batch run) > add-on preference > installer marker >
    Blender config folder."""
    d = os.environ.get("COD2EFT_DATA_DIR", "")
    if d and os.path.isdir(d):
        return d
    if ADDON_PKG:
        try:
            pd = bpy.path.abspath(bpy.context.preferences.addons[ADDON_PKG].preferences.data_dir)
            if pd and os.path.isdir(pd):
                return pd
        except Exception:
            pass
    marker = os.path.join(ADDON_DIR, "cod2eft_home.txt")
    try:
        with open(marker, "r", encoding="utf-8") as fh:
            d = fh.read().strip()
        if d and os.path.isdir(d):
            return d
    except OSError:
        pass
    try:
        return bpy.utils.user_resource("CONFIG", path="cod2eft", create=True)
    except Exception:
        return ADDON_DIR


def bonemap_file():
    return os.path.join(data_dir(), "cod2eft_bonemap.json")


def tweaks_file():
    return os.path.join(data_dir(), "cod2eft_pose_tweaks.json")


# ---------------------------------------------------------------------------------------------
# Importing (FBX, or Cast with the user's add-on or the bundled copy)
# ---------------------------------------------------------------------------------------------
def ensure_cast(log=print):
    """Make bpy.ops.import_scene.cast available: user's enabled/installed Cast add-on first,
    else the bundled copy in vendor/cod2eft_cast (MIT, DTZxPorter)."""
    def ok():
        try:
            bpy.ops.import_scene.cast.get_rna_type()
            return True
        except Exception:
            return False
    if ok():
        return True
    import addon_utils
    import sys
    for mod in addon_utils.modules():
        n = mod.__name__
        if "cast" in n.lower() and "cod2eft" not in n:
            try:
                addon_utils.enable(n, default_set=False)
                if ok():
                    log(f"Using installed Cast add-on '{n}'")
                    return True
            except Exception:
                continue
    vend = os.path.join(ADDON_DIR, "vendor")
    if vend not in sys.path:
        sys.path.insert(0, vend)
    try:
        import cod2eft_cast
        cod2eft_cast.register()
    except ValueError:
        pass                                   # already registered
    except Exception as ex:
        log(f"Could not load bundled Cast importer: {ex}")
    if ok():
        log("Using bundled Cast importer")
        return True
    return False


# Windows can't open paths of 260+ characters (MAX_PATH) unless long paths are switched on, and
# COD exports get there (Park 24_1: a 269-character decal) - the importer then leaves those
# textures empty.  The images are stored relative to the model file, so the model is imported
# through a short link to its folder (a directory junction in the temp folder), and the link is
# removed right after; the images get their real paths back (long ones are packed first).
LONG_PATH = 248
IS_WIN = os.name == "nt"


def _longest_path(folder):
    top = ("\\\\?\\" + os.path.abspath(folder)) if IS_WIN else folder
    n = 0
    for d, _dirs, files in os.walk(top):
        for f in files:
            n = max(n, len(os.path.join(d, f)) - (len(top) - len(folder)))
    return n


def _make_link(folder, link):
    if IS_WIN:
        try:
            import _winapi
            _winapi.CreateJunction(folder, link)
        except Exception:
            import subprocess
            subprocess.run(["cmd", "/c", "mklink", "/J", link, folder], check=True,
                           capture_output=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    else:
        os.symlink(folder, link, target_is_directory=True)


def _drop_link(link):
    """Removes the link only - never what it points to (no rmtree)."""
    try:
        os.rmdir(link)                  # a junction on Windows
    except OSError:
        os.unlink(link)                 # a symlink


def _short_link(folder, log=print):
    """A short path that leads to `folder`, or None (not needed, or couldn't be made)."""
    import hashlib
    import tempfile
    root = os.path.join(tempfile.gettempdir(), "cod2eft_j")
    link = os.path.join(root, hashlib.sha1(os.path.normcase(folder).encode("utf-8")).hexdigest()[:10])
    if len(link) >= len(folder):        # e.g. C:\COD\kleo: the link would make the paths longer
        log(f"  (long texture paths: the folder is already shorter than a temp link, importing directly)")
        return None
    try:
        os.makedirs(root, exist_ok=True)
        if os.path.lexists(link):
            _drop_link(link)            # left over from an interrupted import
        _make_link(folder, link)
        if os.path.isdir(link):
            return link
    except Exception as e:
        log(f"  (long texture paths: couldn't make a short link to the folder: {e})")
    return None


def import_model(path, log=print):
    """Import one COD .fbx/.cast; returns the new objects."""
    path = os.path.abspath(path)
    folder = os.path.dirname(path)
    before = set(bpy.data.objects)
    images = set(bpy.data.images)
    link = None
    if IS_WIN or os.environ.get("COD2EFT_TEST_LINK"):
        try:
            longest = _longest_path(folder)
        except OSError:
            longest = 0
        if longest >= LONG_PATH:
            link = _short_link(folder, log)
            if link:
                log(f"  Long texture paths (up to {longest} characters): importing through {link}")
    src = os.path.join(link, os.path.basename(path)) if link else path
    try:
        if path.lower().endswith(".fbx"):
            bpy.ops.import_scene.fbx(filepath=src)
        else:
            if not ensure_cast(log):
                raise RuntimeError("No Cast importer available (install "
                                   "https://github.com/dtzxporter/cast or use the .fbx)")
            bpy.ops.import_scene.cast(filepath=src)
    finally:
        if link:
            try:
                _relink_images([i for i in bpy.data.images if i not in images], link, folder, log)
            finally:                    # never leave the junction behind in the temp folder
                try:
                    _drop_link(link)
                except OSError as e:
                    log(f"  (couldn't remove the short link {link}: {e})")
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        o["cod2eft_file"] = path                       # textures are found next to it
    return new


def _relink_images(imgs, link, folder, log=print):
    """Images loaded through the short link: point them back at the real file, without reloading.
    Long ones are packed into the .blend first (Blender couldn't reload them from the real path)."""
    pre = os.path.normcase(os.path.abspath(link)) + os.sep
    n = 0
    for im in imgs:
        if not im.filepath_raw:
            continue
        fp = os.path.abspath(bpy.path.abspath(im.filepath_raw))
        if not os.path.normcase(fp).startswith(pre):
            continue
        real = os.path.join(folder, fp[len(pre):])
        try:
            if len(real) >= LONG_PATH and not im.packed_file:
                im.pack()
                n += 1
            im.filepath_raw = real
        except Exception as e:
            log(f"  ({im.name}: {e})")
    if n:
        log(f"  {n} image(s) with long paths packed into the .blend")


EFT_PREFIX = "Base Human"
RIG_NAME = "COD2EFT_FitRig"
SIDES = (("le", "L"), ("ri", "R"))


def E(short):
    """Short EFT name -> full bone name ('LPalm' -> 'Base HumanLPalm')."""
    return short if short.startswith(EFT_PREFIX) else EFT_PREFIX + short


# ---------------------------------------------------------------------------------------------
# Weight map: COD bone -> EFT bone.  Anything not listed falls back to its nearest ancestor that
# IS listed (helper / cloth / gear bones such as bone_xxxx, j_radio, j_cosmetic_*, face bones).
# ---------------------------------------------------------------------------------------------
def default_weight_map():
    """COD bone -> {EFT bone: share}.  A plain string means {bone: 1.0}."""
    m = {
        "j_mainroot": "Pelvis", "j_spinelower": "Spine1", "j_spineupper": "Spine2",
        "j_spine4": "Spine3", "j_proc_spine4": "Spine3",
        "j_neck": "Neck", "j_head": "Head",
        # T9 (Cold War) upper-neck helper between neck and head -> blended
        "j_neck2": {"Neck": 0.5, "Head": 0.5},
    }
    for c, e in SIDES:
        m.update({
            f"j_clavicle_{c}": e + "Collarbone",
            f"j_proc_lift_clavicle_{c}": e + "Collarbone",
            f"j_proc_rot_clavicle_{c}": e + "Collarbone",
            f"j_proc_target_clavicle_{c}": e + "Collarbone",
            f"j_shoulderraise_{c}": e + "Collarbone",          # T9 deltoid/trap raise helper
            f"j_shoulder_{c}": e + "Upperarm", f"j_shouldertwist_{c}": e + "Upperarm",
            f"j_elbow_{c}": e + "Forearm1", f"j_wristfronttwist1_{c}": e + "Forearm3",
            f"j_wrist_{c}": e + "Palm", f"j_proc_wristup_{c}": e + "Palm",
            f"j_hip_{c}": e + "Thigh1", f"j_hiptwist_{c}": e + "Thigh1",
            f"j_knee_{c}": e + "Calf", f"j_ankle_{c}_twist": e + "Calf",
            f"j_ankle_{c}": e + "Foot", f"j_ball_{c}": e + "Toe",
            # Joint-volume correctives that sit ON a joint and follow it about half-way
            # (T9 bulge/cap/rear, IW8/S4 dual-quaternion helpers, T9 half-rotation hip tag):
            # split between the two EFT bones either side of that joint.
            f"j_elbow_bulge_{c}": {e + "Upperarm": 0.5, e + "Forearm1": 0.5},
            f"j_elbowdq_{c}": {e + "Upperarm": 0.5, e + "Forearm1": 0.5},
            f"j_knee_bulge_{c}": {e + "Thigh1": 0.5, e + "Calf": 0.5},
            f"j_knee_cap_{c}": {e + "Thigh1": 0.5, e + "Calf": 0.5},
            f"j_knee_rear_{c}": {e + "Thigh1": 0.5, e + "Calf": 0.5},
            f"j_kneedq_{c}": {e + "Thigh1": 0.5, e + "Calf": 0.5},
            f"tag_hiphalfrot_{c}": {"Pelvis": 0.5, e + "Thigh1": 0.5},
        })
        for f in ("index", "mid", "ring", "pinky"):
            m[f"j_meta{f}_{c}_1"] = e + "Palm"
        for f, d in (("thumb", 1), ("index", 2), ("mid", 3), ("ring", 4), ("pinky", 5)):
            for k in (1, 2, 3):
                m[f"j_{f}_{c}_{k}"] = f"{e}Digit{d}{k}"
    return {k: _norm_target(v) for k, v in m.items()}


def _norm_target(v):
    """'LForearm2' | 'Base HumanLForearm2' | {bone: share} -> {full EFT name: share (sum 1)}"""
    if isinstance(v, str):
        return {E(v): 1.0}
    tot = float(sum(v.values())) or 1.0
    return {E(k): float(w) / tot for k, w in v.items()}


def load_weight_map(log):
    m = default_weight_map()
    if os.path.isfile(bonemap_file()):
        try:
            with open(bonemap_file(), "r", encoding="utf-8") as fh:
                user = json.load(fh)
            user = {k: _norm_target(v) for k, v in user.items() if not k.startswith("_")}
            m.update(user)
            log(f"Loaded {len(user)} bone-map override(s) from {os.path.basename(bonemap_file())}")
        except Exception as ex:  # keep going with defaults, but say so
            log(f"WARNING: could not read {bonemap_file()}: {ex} - using built-in map")
    return m


# ---------------------------------------------------------------------------------------------
# Skeleton family detection (report only - the conversion itself is name based and universal)
# ---------------------------------------------------------------------------------------------
FAMILY_SIGNATURES = [
    # (label, bones that must all exist, bones that must NOT exist) - first match wins.
    # Derived from the 33 test characters; conversion does not depend on this (report only).
    ("first-person viewmodel (skipped)", ["tag_view"], []),
    ("T9 rig - Black Ops Cold War / Warzone 1 (BO5)", ["j_wristtwist1_le"], []),
    ("T9 rig - Black Ops Cold War / Warzone 1 (BO5)", ["j_neck2"], []),
    ("T9 rig - Black Ops Cold War / Warzone 1 (BO5)", ["tag_hiphalfrot_le"], []),
    ("T9 rig - Black Ops Cold War / Warzone 1 (BO5)", ["j_knee_bulge_le"], []),
    ("IW8 rig - MW2019 / Vanguard / early MW2 (Warzone 1-2)", ["j_kneedq_le"], []),
    ("IW8 rig - MW2019 / Vanguard / early MW2 (Warzone 1-2)", ["j_elbowdq_le"], []),
    ("MW4 beta rig (no shoulder/hip twist)", ["j_proc_wristfronttwist1_le"], ["j_shouldertwist_le"]),
    ("IW9/JUP rig - MW2 (2022) / MW3 (2023)", ["j_proc_shoulder_le", "j_hip_proc_le"], []),
    ("T10/SAT rig - Black Ops 6 / Black Ops 7", ["j_shouldertwist_le", "j_wristfronttwist1_le"],
     ["j_proc_shoulder_le"]),
    ("Warzone 2 era rig", ["j_shouldertwist_le", "j_wristfronttwist1_le"], []),
    ("COD character (game not identifiable from this partial skeleton)", ["j_head"], []),
]


def detect_family(bone_names):
    names = set(bone_names)
    for label, need, forbid in FAMILY_SIGNATURES:
        if all(n in names for n in need) and not any(n in names for n in forbid):
            return label
    return "unknown COD skeleton"


import re as _re
PLACEHOLDER_MAT = _re.compile(r"^(lambert\d+|initialShadingGroup|default_material)(\.\d+)?$", _re.I)


def is_head_skeleton(arm):
    """Head models carry spine4/neck/head (+face) but no legs; everything in them is Head."""
    b = arm.data.bones
    return "j_head" in b and "j_hip_le" not in b and "j_hip_ri" not in b


def is_viewmodel(arm):
    return "tag_view" in arm.data.bones


# ---------------------------------------------------------------------------------------------
# Pose-fit definitions.  (cod_bone, eft_bone, cod_aim, eft_aim, secondary, mode, skin landmark)
#   primary axis   = joint -> aim joint (COD and EFT)
#   secondary axis = vector b-a used to fix the roll (bend plane / palm direction / left-right)
#   aim target     = the EFT child joint, moved by the body-volume correction of `skin`
#   ('reach' falls back to 'aim' when "Snap limb joints" is off)
# Bones not listed keep their parent's motion (twist, helper and cloth bones).
# ---------------------------------------------------------------------------------------------
def fit_table():
    """Per driven COD bone: EFT bone, aim joints (COD / EFT), secondary (roll) direction pair,
    mode, and the body-volume landmark that corrects the aim target (see SKIN_SPEC).
    Modes:  'reach' aim + stretch so the COD child joint lands on the target
            'aim'   aim at the target, no stretch (spine: keeps COD torso proportions)
            'level' keep the bind-pose orientation relative to the ground, turn to EFT heading
                    (feet: soles stay flat on the floor; head: stays upright)
            None    copy the EFT segment direction
    'aim' / 'level' / skin corrections are used when "Match body volume" is on; otherwise the
    old joint-on-joint behaviour ('aim'/'level' -> None)."""
    LR_HIP = ("j_hip_ri", "j_hip_le", "RThigh1", "LThigh1")
    LR_CLAV = ("j_clavicle_ri", "j_clavicle_le", "RCollarbone", "LCollarbone")
    rows = [
        ("j_mainroot", "Pelvis", "j_spinelower", "Spine1", LR_HIP, None, None),
        ("j_spinelower", "Spine1", "j_spineupper", "Spine2", LR_HIP, "aim", "spine2"),
        ("j_spineupper", "Spine2", "j_spine4", "Spine3", LR_CLAV, "aim", "spine3"),
        ("j_spine4", "Spine3", "j_neck", "Neck", LR_CLAV, "aim", None),
        ("j_neck", "Neck", "j_head", "Head", LR_CLAV, "aim", "face"),
        ("j_head", "Head", None, None, None, "level", None),
    ]
    for c, e in SIDES:
        UP = ("j_spine4", "j_neck", "Spine3", "Neck")
        BEND_ARM = (f"j_elbow_{c}", f"j_wrist_{c}", e + "Forearm1", e + "Palm")
        HAND_LAT = (f"j_pinky_{c}_1", f"j_index_{c}_1", e + "Digit51", e + "Digit21")
        HAND_FWD = (f"j_wrist_{c}", f"j_mid_{c}_1", e + "Palm", e + "Digit31")
        FOOT = (f"j_ankle_{c}", f"j_ball_{c}", e + "Foot", e + "Toe")
        rows += [
            (f"j_clavicle_{c}", e + "Collarbone", f"j_shoulder_{c}", e + "Upperarm", UP, "reach",
             None),
            (f"j_shoulder_{c}", e + "Upperarm", f"j_elbow_{c}", e + "Forearm1", BEND_ARM, "reach",
             None),
            (f"j_elbow_{c}", e + "Forearm1", f"j_wrist_{c}", e + "Palm", HAND_LAT, "reach", None),
            (f"j_wrist_{c}", e + "Palm", f"j_mid_{c}_1", e + "Digit31", HAND_LAT, "reach", None),
            (f"j_hip_{c}", e + "Thigh1", f"j_knee_{c}", e + "Calf", FOOT, "reach", "knee_" + e),
            (f"j_knee_{c}", e + "Calf", f"j_ankle_{c}", e + "Foot", FOOT, "reach", "shin_" + e),
            (f"j_ankle_{c}", e + "Foot", f"j_ball_{c}", e + "Toe", LR_HIP, "level", None),
            (f"j_thumb_{c}_1", e + "Digit11", f"j_thumb_{c}_2", e + "Digit12", HAND_FWD, "reach",
             None),
            (f"j_thumb_{c}_2", e + "Digit12", f"j_thumb_{c}_3", e + "Digit13", HAND_FWD, "reach",
             None),
        ]
        for f, d in (("index", 2), ("mid", 3), ("ring", 4), ("pinky", 5)):
            rows += [
                (f"j_{f}_{c}_1", f"{e}Digit{d}1", f"j_{f}_{c}_2", f"{e}Digit{d}2", HAND_LAT,
                 "reach", None),
                (f"j_{f}_{c}_2", f"{e}Digit{d}2", f"j_{f}_{c}_3", f"{e}Digit{d}3", HAND_LAT,
                 "reach", None),
            ]
        # fingertips: the distal segment is aimed + stretched (limited) so its tip reaches EFT's
        # fingertip ("<bone>.tip" = virtual joints, see cod_fingertips / EFT_TIP_OFFSET)
        for f, d in FINGERS:
            lat = HAND_FWD if f == "thumb" else HAND_LAT
            rows.append((f"j_{f}_{c}_3", f"{e}Digit{d}3", f"j_{f}_{c}_3.tip",
                         f"{e}Digit{d}3.tip", lat, "tip", None))
    out = {}
    for cb, eb, ca, ea, sec, st, skin in rows:
        out[cb] = dict(eft=E(eb), cod_aim=ca,
                       eft_aim=(E(ea[:-4]) + ".tip" if ea and ea.endswith(".tip") else
                                E(ea) if ea else None),
                       sec=(sec[0], sec[1], E(sec[2]), E(sec[3])) if sec else None,
                       stretch=st, skin=skin)
    return out


# Landmarks used for the global rotation/scale/offset (torso only - limbs are posed afterwards).
ALIGN_LANDMARKS = [
    ("j_mainroot", "Pelvis"), ("j_spinelower", "Spine1"), ("j_spineupper", "Spine2"),
    ("j_spine4", "Spine3"), ("j_neck", "Neck"), ("j_head", "Head"),
    ("j_clavicle_le", "LCollarbone"), ("j_clavicle_ri", "RCollarbone"),
    ("j_shoulder_le", "LUpperarm"), ("j_shoulder_ri", "RUpperarm"),
    ("j_hip_le", "LThigh1"), ("j_hip_ri", "RThigh1"),
]

# Twist falloff measured from EFT's own meshes in "EFT BASIC [Template].blend":
#   forearm = Tshirt_bear_Voin_lod0, t = 0 at Forearm1 head .. 1 at Palm head (both sides averaged)
#   thigh   = Pants_wild_bomber_LOD0, t = 0 at Thigh1 head .. 1 at Calf head
TWIST_PROFILES = {
    "forearm": dict(
        bones=("Forearm1", "Forearm2", "Forearm3"), seg=("Forearm1", "Palm"),
        t=[0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0],
        w=[[0.998, 0.002, 0.0], [0.979, 0.02, 0.001], [0.893, 0.097, 0.011], [0.736, 0.225, 0.039],
           [0.551, 0.33, 0.119], [0.375, 0.367, 0.258], [0.225, 0.366, 0.409], [0.06, 0.182, 0.758],
           [0.011, 0.086, 0.903], [0.0, 0.02, 0.98], [0.0, 0.001, 0.999]]),
    "thigh": dict(
        bones=("Thigh1", "Thigh2"), seg=("Thigh1", "Calf"),
        t=[-0.2, -0.1, 0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2],
        w=[[1.0, 0.0], [0.993, 0.007], [0.978, 0.022], [0.954, 0.046], [0.915, 0.085],
           [0.818, 0.182], [0.766, 0.234], [0.689, 0.311], [0.623, 0.377], [0.616, 0.384],
           [0.633, 0.367], [0.701, 0.299], [0.799, 0.201], [0.894, 0.106], [0.946, 0.054]]),
}

# ---------------------------------------------------------------------------------------------
# Body-volume ("skin") matching
#
# COD and EFT put their joints in different places INSIDE the body: EFT's spine joints sit near
# the back (chest centre ~8 cm in front of Spine3), COD's sit mid-body (~1 cm); EFT's knee joint
# is in front of the knee centre, COD's behind it.  Snapping joint onto joint therefore leaves
# the COD chest ~6 cm behind EFT's and the COD knees ~4 cm in front.  Instead the COD skeleton is
# posed so the centre of the COD body cross-section at each landmark lands on the centre of
# EFT's cross-section at the same landmark (measured from EFT's own meshes below); the mesh then
# sits around EFT's joints exactly the way EFT's own clothing does, so it bends like EFT meshes.
#
# Section = where the mesh surface crosses the plane across the body axis at the landmark (within
# `radius`, and within `midline` of the centre line for the torso so arms/holsters don't count).
# Centre = midpoint of the 2nd/98th percentile along forward (and sideways for legs).
# ---------------------------------------------------------------------------------------------
FWD = np.array([0.0, -1.0, 0.0])        # EFT characters face -Y

# key: (cod point, cod axis, eft point, eft axis, radius, midline, use_lateral, own_side_only)
#   point = (joint_a, joint_b, t) -> a + t * (b - a)
def _skin_spec():
    sp = {
        "pelvis": (("j_mainroot", None, 0), ("j_mainroot", "j_spinelower"),
                   ("Pelvis", None, 0), ("Pelvis", "Spine1"), 0.35, 0.10, False, False),
        "spine2": (("j_spineupper", None, 0), ("j_spinelower", "j_spine4"),
                   ("Spine2", None, 0), ("Spine1", "Spine3"), 0.35, 0.10, False, False),
        "spine3": (("j_spine4", None, 0), ("j_spineupper", "j_neck"),
                   ("Spine3", None, 0), ("Spine2", "Neck"), 0.35, 0.10, False, False),
    }
    for c, e in SIDES:
        sp["knee_" + e] = ((f"j_knee_{c}", None, 0), (f"j_hip_{c}", f"j_ankle_{c}"),
                           (e + "Calf", None, 0), (e + "Thigh1", e + "Foot"), 0.12, None, True,
                           False)
        # lower shin, a quarter of the way up from the ankle (the ankle itself is too close to
        # the foot to give a clean cross-section)
        sp["shin_" + e] = ((f"j_ankle_{c}", f"j_knee_{c}", 0.25), (f"j_knee_{c}", f"j_ankle_{c}"),
                           (e + "Foot", e + "Calf", 0.25), (e + "Calf", e + "Foot"), 0.12, None,
                           True, False)
        # upper thigh, 40% of the way down to the knee; only this leg's side of the body counts
        # (EFT's pants are baggy - a round radius would reach the crotch / the other leg)
        sp["thigh_" + e] = ((f"j_hip_{c}", f"j_knee_{c}", 0.4), (f"j_hip_{c}", f"j_knee_{c}"),
                            (e + "Thigh1", e + "Calf", 0.4), (e + "Thigh1", e + "Calf"), 0.16,
                            None, True, True)
        # upper arm, 30% of the way from the shoulder to the elbow
        sp["upperarm_" + e] = ((f"j_shoulder_{c}", f"j_elbow_{c}", 0.3),
                               (f"j_shoulder_{c}", f"j_elbow_{c}"),
                               (e + "Upperarm", e + "Forearm1", 0.3), (e + "Upperarm", e + "Forearm1"),
                               0.085, None, True, False)
    return sp


SKIN_SPEC = _skin_spec()
PAIRED_SKIN = ("knee_", "shin_", "thigh_", "upperarm_")

# Measured with skin_sections() on EFT's own meshes in "EFT BASIC [Template].blend"
# (Bear_head_0, Tshirt_bear_Voin_lod0, Pants_wild_bomber_LOD0), left/right averaged.
# (forward m, sideways m along the section's lat axis) of the section centre relative to the
# landmark point.
EFT_SKIN = {
    "pelvis": (0.0236, 0.0000),
    "spine2": (0.0668, 0.0000),
    "spine3": (0.0833, 0.0000),
    "knee_L": (-0.0236, -0.0096),
    "knee_R": (-0.0236, 0.0096),
    "shin_L": (0.0051, -0.0042),
    "shin_R": (0.0051, 0.0042),
    "thigh_L": (0.0017, 0.0037),
    "thigh_R": (0.0017, -0.0037),
    "upperarm_L": (-0.0035, 0.0009),
    "upperarm_R": (-0.0035, -0.0009),
}
# Typical COD values (median over the 31 test characters that have a body), (forward m, sideways
# m along the section's lat axis; right side = mirror image of the left).  A measured COD value further than SKIN_CLAMP from this is limited to it (a backpack or
# chest rig can shift a section centre).
COD_SKIN_PRIOR = {
    "pelvis": (0.0039, 0.0000),
    "spine2": (0.0267, 0.0000),
    "spine3": (0.0151, 0.0000),
    "knee_L": (0.0101, 0.0112),
    "knee_R": (0.0101, -0.0112),
    "shin_L": (0.0101, -0.0053),
    "shin_R": (0.0101, 0.0053),
    "thigh_L": (0.0142, -0.0010),
    "thigh_R": (0.0142, 0.0010),
    "upperarm_L": (-0.0020, 0.0053),
    "upperarm_R": (-0.0020, -0.0053),
}
SKIN_CLAMP = 0.03
# most the pelvis section may sit further behind the waist (spine2) section than on EFT's body (m)
PELVIS_WAIST_SLACK = float(os.environ.get("COD2EFT_PELVIS_SLACK", "0.01"))
# EFT ground: bottom of EFT's shoe soles (m)
EFT_FLOOR_Z = -0.0025


def _pt(J, spec_pt, names):
    a, b, t = spec_pt
    a, b = names(a), (names(b) if b else None)
    if a not in J or (b and b not in J):
        return None
    return J[a] if not b else J[a] + t * (J[b] - J[a])


def mesh_arrays(objs, depsgraph=None):
    """World-space vertex positions and triangle vertex indices of several meshes (the base
    mesh, or the deformed one when a depsgraph is given)."""
    P, T, off = [], [], 0
    for o in objs:
        ev = o.evaluated_get(depsgraph) if depsgraph else None
        me = ev.to_mesh() if ev else o.data
        me.calc_loop_triangles()
        co = np.empty(len(me.vertices) * 3)
        me.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3)
        tri = np.empty(len(me.loop_triangles) * 3, dtype=np.int64)
        me.loop_triangles.foreach_get("vertices", tri)
        if ev:
            ev.to_mesh_clear()
        mw = np.array(o.matrix_world)
        P.append(co @ mw[:3, :3].T + mw[:3, 3])
        T.append(tri.reshape(-1, 3) + off)
        off += len(co)
    if not P:
        return np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64)
    return np.vstack(P), np.vstack(T)


def plane_cut(P, T, c, axis):
    """Points where the mesh surface crosses the plane through c normal to axis."""
    d = (P - c) @ axis
    pts = []
    for i, j in ((0, 1), (1, 2), (2, 0)):
        a, b = T[:, i], T[:, j]
        m = (d[a] * d[b]) < 0
        if m.any():
            a, b = a[m], b[m]
            t = (d[a] / (d[a] - d[b]))[:, None]
            pts.append(P[a] + t * (P[b] - P[a]))
    return np.vstack(pts) if pts else np.zeros((0, 3))


def section_frame(J, key, eft_side):
    """(point, axis, forward, sideways) of landmark `key` from joints J, or None."""
    cpt, cax, ept, eax = SKIN_SPEC[key][:4]
    names = E if eft_side else (lambda n: n)
    pt, ax = (ept, eax) if eft_side else (cpt, cax)
    c = _pt(J, pt, names)
    a, b = names(ax[0]), names(ax[1])
    if c is None or a not in J or b not in J:
        return None
    axis = J[b] - J[a]
    if np.linalg.norm(axis) < 1e-6:
        return None
    axis = axis / np.linalg.norm(axis)
    fw = FWD - axis * (FWD @ axis)
    if np.linalg.norm(fw) < 0.3:
        return None
    fw /= np.linalg.norm(fw)
    return c, axis, fw, np.cross(axis, fw)


def skin_sections(P, T, J, eft_side):
    """Cross-section centres for every SKIN_SPEC landmark: the mesh surface is cut by the plane
    across the body axis at the landmark.  P/T = vertices/triangles (EFT space, model facing
    -Y), J = joint positions, eft_side=True uses EFT bone names.  Returns {key: dict(fwd=m,
    lat=m, fw=unit, lat_axis=unit, n=points)} for the landmarks found."""
    out = {}
    for key, spec in SKIN_SPEC.items():
        radius, midline, use_lat, own_side = spec[4:8]
        fr = section_frame(J, key, eft_side)
        if fr is None:
            continue
        c, axis, fw, lat = fr
        Q = plane_cut(P, T, c, axis) - c
        if not len(Q):
            continue
        x, y = Q @ lat, Q @ fw
        s = np.linalg.norm(Q, axis=1) < radius
        if midline:
            s &= np.abs(x) < midline
        if own_side:
            s &= (Q[:, 0] + c[0]) * np.sign(c[0]) > 0.005
        if s.sum() < 8:
            continue
        x, y = x[s], y[s]
        out[key] = dict(fwd=float((np.percentile(y, 98) + np.percentile(y, 2)) / 2),
                        lat=float((np.percentile(x, 98) + np.percentile(x, 2)) / 2)
                        if use_lat else 0.0,
                        fw=fw, lat_axis=lat, n=int(s.sum()))
    # left/right average (a holster on one thigh moves both by half, not one by all).
    # The right side's frame is the mirror image of the left's with the sideways axis flipped, so
    # a mirror-symmetric pair has fwd_R = fwd_L and lat_R = -lat_L.
    for base in PAIRED_SKIN:
        L, R = out.get(base + "L"), out.get(base + "R")
        if L and R:
            f = (L["fwd"] + R["fwd"]) / 2
            m = (L["lat"] - R["lat"]) / 2
            L["fwd"] = R["fwd"] = f
            L["lat"], R["lat"] = m, -m
    return out


def cod_sole(P, J):
    """Ground plane the COD model stands on in its bind pose, from the lowest heel and toe points
    of both feet (all characters stand flat in bind).  Returns (up normal, point on plane) or
    None."""
    pts = []
    for c, _ in SIDES:
        a = J.get(f"j_ankle_{c}")
        if a is None:
            return None
        d = P[:, :2] - a[:2]
        s = (np.linalg.norm(d, axis=1) < 0.25) & (P[:, 2] < a[2])
        if s.sum() < 30:
            return None
        Q = P[s]
        y = Q @ FWD
        lo, hi = np.percentile(y, 33), np.percentile(y, 67)
        for part in (Q[y <= lo], Q[y >= hi]):             # heel third, toe third
            k = max(3, len(part) // 100)
            pts.append(part[np.argsort(part[:, 2])[:k]].mean(0))
    pts = np.array(pts)
    ctr = pts.mean(0)
    n = np.linalg.svd(pts - ctr)[2][2]
    if n[2] < 0:
        n = -n
    if n[2] < math.cos(math.radians(12)):        # not a plausible floor - don't trust it
        return None
    return n, ctr


# Tip of the nose of EFT's head (Bear_head_0, most forward point on the centre line) - the COD
# head is placed so its nose tip lands here (the head itself stays upright).
EFT_NOSE = np.array([-0.001, -0.135, 1.684])
# COD nose tip relative to the eyeball centres (forward m, down m), median of the 21 test heads,
# used when the nose can't be found (mask, visor) - measured range 4.3..7.2 fwd, 2.4..4.5 down
COD_NOSE_FROM_EYES = (0.052, 0.037)
# Centre of EFT's eyeballs (sphere fit to the eye caps of Bear_Head_0 from EFT's own bundle,
# SPT 4.1; both eyes: x +-0.032, y -0.0884 / -0.0890, z 1.7140 / 1.7144).  COD's j_eyeball_*
# bones sit at their eyeball centres (checked on 15 test heads: within 2 mm), so with "eyes" as
# the face landmark the COD eye centres are brought here - what matters for hats / glasses.
EFT_EYES = np.array([0.0, -0.0887, 1.7142])
# EFT fingertips: centre of the tip end (last 30 % of each distal phalanx) of the hands of EFT's
# own third-person top (Tshirt_bear_turtleneck, bear_body bundle, SPT 4.1), relative to the
# Digit*3 joint, world axes.  The first-person hands (Hands_BEAR) agree within 2 - 5 mm.
EFT_TIP_OFFSET = {
    "L1": (-0.0118, -0.0252, -0.0039), "L2": (-0.0149, -0.0095, -0.0206),
    "L3": (-0.0151, -0.0083, -0.0202), "L4": (-0.0139, -0.0117, -0.0176),
    "L5": (-0.0134, -0.0045, -0.0174), "R1": (0.0112, -0.0261, -0.0042),
    "R2": (0.0162, -0.0106, -0.0197), "R3": (0.0171, -0.0088, -0.0206),
    "R4": (0.0160, -0.0122, -0.0180), "R5": (0.0143, -0.0062, -0.0157)}
FINGERS = (("thumb", 1), ("index", 2), ("mid", 3), ("ring", 4), ("pinky", 5))
TIP_RATIO = (0.8, 1.25)            # most a fingertip segment is shortened / lengthened
# Finger roots (the middle finger's is placed by the palm) moved onto EFT's knuckle joints, at
# most KNUCKLE_MAX.  Without this the first finger segments were rotated and stretched up to
# x1.9 to reach EFT's middle joints (COD knuckle rows sit 1 - 3.6 cm off EFT's after the palm fit);
# with it the worst stretch on the 33 test characters is x1.57, the hand surface matches EFT's
# third-person hands as well as before (mean 0.43 cm), and renders show no palm shearing.
KNUCKLE_BONES = {f"j_{f}_{c}_1" for f in ("thumb", "index", "ring", "pinky") for c in ("le", "ri")}
KNUCKLE_MAX = 0.03
# "Limited" head height: most the spine + neck are stretched / shortened to bring the eyes to
# EFT's eye height (the rest of the gap is reported)
HEAD_HEIGHT_LIMIT = 0.06
# Largest forward lean of the neck (neck joint -> head joint) used to bring the face forward, in
# degrees from vertical (EFT's own neck leans 19.5 deg)
NECK_MAX_LEAN = 35.0


def cod_nose(P, J):
    """Nose tip of the COD face: most forward surface point on the centre line 1-7 cm below the
    eyeball bones.  Returns (point, how) or (None, reason)."""
    if "j_eyeball_le" not in J or "j_eyeball_ri" not in J:
        return None, "no eyeball bones"
    e = (J["j_eyeball_le"] + J["j_eyeball_ri"]) / 2
    s = ((np.abs(P[:, 0] - e[0]) < 0.015) & (P[:, 2] < e[2] - 0.01) & (P[:, 2] > e[2] - 0.07) &
         (np.abs(P[:, 1] - e[1]) < 0.12))
    if s.sum():
        Q = P[s]
        n = Q[np.argmax(Q @ FWD)]
        fwd, down = (n - e) @ FWD, e[2] - n[2]
        if 0.03 <= fwd <= 0.08 and 0.015 <= down <= 0.06:
            return n, "nose tip"
    return e + COD_NOSE_FROM_EYES[0] * FWD - np.array([0, 0, COD_NOSE_FROM_EYES[1]]), \
        "eyes + typical nose offset (no clear nose - mask/visor?)"


def cod_eyes(J):
    """Midpoint of the COD eyeball centres (j_eyeball_le / _ri), or None."""
    if "j_eyeball_le" in J and "j_eyeball_ri" in J:
        return (J["j_eyeball_le"] + J["j_eyeball_ri"]) / 2
    return None


def cod_fingertips(meshes, J):
    """{distal COD bone: fingertip point} - centre of the last 30 % (along the middle ->
    distal joint direction) of the vertices weighted mainly (> 0.5) to the distal bone."""
    pts = {}
    for c, _ in SIDES:
        for fn, _ in FINGERS:
            b3, b2 = f"j_{fn}_{c}_3", f"j_{fn}_{c}_2"
            if b3 in J and b2 in J:
                pts[b3] = []
    if not pts:
        return {}
    for o in meshes:
        idx = {g.index: g.name for g in o.vertex_groups if g.name in pts}
        if not idx:
            continue
        co = np.empty(len(o.data.vertices) * 3)
        o.data.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3)
        mw = np.array(o.matrix_world)
        co = co @ mw[:3, :3].T + mw[:3, 3]
        for v in o.data.vertices:
            if not v.groups:
                continue
            g = max(v.groups, key=lambda g: g.weight)
            if g.weight > 0.5 and g.group in idx:
                pts[idx[g.group]].append(co[v.index])
    out = {}
    for b3, P in pts.items():
        if len(P) < 6:
            continue
        P = np.array(P)
        a = J[b3]
        ax = a - J[b3[:-1] + "2"]
        if np.linalg.norm(ax) < 1e-6:
            continue
        ax = ax / np.linalg.norm(ax)
        t = (P - a) @ ax
        if t.max() < 0.005:
            continue
        out[b3] = P[t > 0.7 * t.max()].mean(0)
    return out


def rot_between(a, b):
    """Smallest rotation taking direction a to direction b."""
    a = a / np.linalg.norm(a)
    b = b / np.linalg.norm(b)
    v = np.cross(a, b)
    c = float(a @ b)
    if np.linalg.norm(v) < 1e-9:
        return np.eye(3) if c > 0 else -np.eye(3)
    K = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + K + K @ K * (1.0 / (1.0 + c))


PART_HEAD = {E(n) for n in ("Neck", "Head")}
PART_LOWER = {E(n) for n in ("Pelvis", "LThigh1", "LThigh2", "LCalf", "LFoot", "LToe",
                             "RThigh1", "RThigh2", "RCalf", "RFoot", "RToe")}


# ---------------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------------
class Log:
    def __init__(self, quiet=False):
        self.lines = []
        self.quiet = quiet

    def __call__(self, msg):
        self.lines.append(str(msg))
        if not self.quiet:
            print("[COD2EFT] " + str(msg))

    def to_text(self, name="COD2EFT_Report"):
        txt = bpy.data.texts.get(name) or bpy.data.texts.new(name)
        txt.write(f"---- COD2EFT v{VERSION_STR} ----\n" + "\n".join(self.lines) + "\n\n")


def np3(v):
    return np.array([v[0], v[1], v[2]], dtype=float)


def umeyama(src, dst, with_scale=True):
    """Least-squares similarity dst ~= s*R*src + t. Returns (s, R, t, mirrored_flag)."""
    src = np.asarray(src, float)
    dst = np.asarray(dst, float)
    mu_s, mu_d = src.mean(0), dst.mean(0)
    xs, xd = src - mu_s, dst - mu_d
    cov = xd.T @ xs / len(src)
    U, S, Vt = np.linalg.svd(cov)
    D = np.eye(3)
    mirrored = np.linalg.det(U) * np.linalg.det(Vt) < 0
    if mirrored:
        D[2, 2] = -1
    R = U @ D @ Vt
    var_s = (xs ** 2).sum() / len(src)
    s = float((S * np.diag(D)).sum() / var_s) if with_scale else 1.0
    t = mu_d - s * R @ mu_s
    return s, R, t, mirrored


UP_PAIRS_COD = [("j_mainroot", "j_neck"), ("j_spine4", "j_head")]
UP_PAIRS_EFT = [(E("Pelvis"), E("Neck")), (E("Spine3"), E("Head"))]
LEFT_PAIRS_COD = [("j_hip_ri", "j_hip_le"), ("j_clavicle_ri", "j_clavicle_le"),
                  ("j_shoulder_ri", "j_shoulder_le")]
LEFT_PAIRS_EFT = [(E("RThigh1"), E("LThigh1")), (E("RCollarbone"), E("LCollarbone")),
                  (E("RUpperarm"), E("LUpperarm"))]


def align_to_eft(CJ, EJ, pairs, fit_scale):
    """COD -> EFT similarity.  Rotation comes from a named frame (up = pelvis->neck,
    left = _ri->_le), so it can never mirror or turn the model backwards even when the landmark
    joints are nearly flat (Cold War rigs).  Scale/offset are least-squares over the landmarks;
    scale is snapped to a power of ten (cm vs m) unless fit_scale."""
    up_c, up_e, lf_c, lf_e = np.zeros(3), np.zeros(3), np.zeros(3), np.zeros(3)
    for (ca, cb), (ea, eb) in zip(UP_PAIRS_COD, UP_PAIRS_EFT):
        if ca in CJ and cb in CJ and ea in EJ and eb in EJ:
            up_c += CJ[cb] - CJ[ca]; up_e += EJ[eb] - EJ[ea]
    for (ca, cb), (ea, eb) in zip(LEFT_PAIRS_COD, LEFT_PAIRS_EFT):
        if ca in CJ and cb in CJ and ea in EJ and eb in EJ:
            lf_c += CJ[cb] - CJ[ca]; lf_e += EJ[eb] - EJ[ea]
    src = np.array([CJ[c] for c, _ in pairs])
    dst = np.array([EJ[e] for _, e in pairs])
    if np.linalg.norm(up_c) > 0 and np.linalg.norm(lf_c) > 0:
        R = frame(up_e, lf_e) @ frame(up_c, lf_c).T
    else:                                    # not enough named directions: plain point fit
        _, R, _, _ = umeyama(src, dst)
    mu_s, mu_d = src.mean(0), dst.mean(0)
    xs, xd = src - mu_s, dst - mu_d
    s = float(((xs @ R.T) * xd).sum() / max((xs ** 2).sum(), 1e-12))
    if not fit_scale:
        s = 10.0 ** round(math.log10(s))
    t = mu_d - s * R @ mu_s
    return s, R, t


CORE_SHARED = ("j_mainroot", "j_spinelower", "j_spineupper", "j_spine4", "j_neck", "j_head",
               "j_clavicle_le", "j_clavicle_ri", "j_shoulder_le", "j_shoulder_ri",
               "j_hip_le", "j_hip_ri", "j_knee_le", "j_knee_ri")


def robust_rigid(src_j, dst_j, shared):
    """Rigid (+unit scale) transform src->dst from bones present in both skeletons.
    Starts from core joints, then adds every shared bone that agrees and refits; bones whose
    rest position differs between the files (visors, helmets, props) are left out.
    Returns (4x4, used, dropped, max_err_in_dst_units) or None."""
    core = [n for n in CORE_SHARED if n in shared]
    if len(core) < 3:
        core = list(shared)
    if len(core) < 3:
        return None
    A = np.array([src_j[n] for n in core])
    if np.linalg.matrix_rank(A - A.mean(0), tol=1e-3 * max(np.abs(A).max(), 1e-9)) < 2:
        return None
    s, R, t, _ = umeyama(A, np.array([dst_j[n] for n in core]))
    s = 10.0 ** round(math.log10(s)) if s > 0 else 1.0      # same skeleton: only unit changes
    _, R, t, _ = umeyama(A * s, np.array([dst_j[n] for n in core]), with_scale=False)
    scale_ref = np.linalg.norm(np.ptp(np.array([dst_j[n] for n in core]), axis=0)) or 1.0
    tol = 0.02 * scale_ref                                  # 2% of the core span (~1-3 cm)
    used = core
    for _ in range(3):
        err_all = {n: np.linalg.norm(s * R @ src_j[n] + t - dst_j[n]) for n in shared}
        good = [n for n in shared if err_all[n] <= tol] or core
        A = np.array([src_j[n] for n in good]) * s
        if np.linalg.matrix_rank(A - A.mean(0), tol=1e-3 * max(np.abs(A).max(), 1e-9)) < 2:
            break
        _, R, t, _ = umeyama(A, np.array([dst_j[n] for n in good]), with_scale=False)
        used = good
    err_all = {n: np.linalg.norm(s * R @ src_j[n] + t - dst_j[n]) for n in shared}
    dropped = sorted((n for n in shared if n not in used), key=lambda n: -err_all[n])
    return sim_to_matrix(s, R, t), used, dropped, max(err_all[n] for n in used)


def sim_to_matrix(s, R, t):
    M = np.eye(4)
    M[:3, :3] = s * R
    M[:3, 3] = t
    return M


def frame(primary, secondary, fallback=None):
    """Orthonormal right-handed frame as 3x3 columns (x, y, z) with y = primary."""
    y = primary / np.linalg.norm(primary)
    for s in (secondary, fallback, np.array([0.0, 0.0, 1.0]), np.array([1.0, 0.0, 0.0])):
        if s is None:
            continue
        x = s - (s @ y) * y
        n = np.linalg.norm(x)
        if n > 0.15 * np.linalg.norm(s):
            x = x / n
            z = np.cross(x, y)
            return np.column_stack([x, y, z])
    raise RuntimeError("degenerate frame")


def mat4(Rm=None, t=None):
    M = np.eye(4)
    if Rm is not None:
        M[:3, :3] = Rm
    if t is not None:
        M[:3, 3] = t
    return M


def to_bl(M):
    return Matrix([list(r) for r in M])


def from_bl(M):
    return np.array([[M[i][j] for j in range(4)] for i in range(4)], float)


def find_eft_armature(context=None):
    cands = [o for o in bpy.data.objects if o.type == "ARMATURE" and E("Pelvis") in o.data.bones
             and o.name in bpy.context.scene.objects and o.name != "COD2EFT_Adjust"]   # (hand-adjust copy)
    if context is not None and context.scene.cod2eft.eft_armature in cands:
        return context.scene.cod2eft.eft_armature
    return cands[0] if cands else None


def is_cod_armature(o):
    return (o.type == "ARMATURE" and o.name != RIG_NAME and E("Pelvis") not in o.data.bones
            and any(b.name.startswith(("j_", "tag_origin")) for b in o.data.bones))


def meshes_of(arm):
    out = []
    for o in bpy.context.scene.objects:
        if o.type != "MESH":
            continue
        if any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers) or \
           (o.parent == arm and o.vertex_groups):
            out.append(o)
    return out


def bone_depth(bones):
    depth = {}

    def d(b):
        if b.name in depth:
            return depth[b.name]
        depth[b.name] = 0 if b.parent is None else d(b.parent) + 1
        return depth[b.name]

    for b in bones:
        d(b)
    return depth


def eft_joints(eft):
    W = eft.matrix_world
    return {b.name: np3(W @ b.head_local) for b in eft.data.bones}


def world_joints(arm):
    W = arm.matrix_world
    return {b.name: np3(W @ b.head_local) for b in arm.data.bones}


def fix_winding(o):
    """Some COD exports (MW2019/Vanguard/WZ1 era, MW2, Cold War base bodies) store triangles
    wound opposite to their own vertex normals - Blender shades them black and Unity would
    back-face-cull them (inside-out).  If most faces disagree with the stored normals, flip
    the winding and re-apply the original normals per corner.  Returns True if flipped."""
    me = o.data
    nl, np_ = len(me.loops), len(me.polygons)
    if nl == 0 or np_ == 0:
        return False
    cn = np.empty(nl * 3)
    me.corner_normals.foreach_get("vector", cn)
    cn = cn.reshape(-1, 3)
    pn = np.empty(np_ * 3)
    me.polygons.foreach_get("normal", pn)
    pn = pn.reshape(-1, 3)
    tot = np.empty(np_, dtype=np.int64)
    me.polygons.foreach_get("loop_total", tot)
    lp = np.repeat(np.arange(np_), tot)
    agree = ((cn * pn[lp]).sum(1) > 0).mean()
    o["cod2eft_winding_agree"] = float(agree)
    if agree >= 0.5 or not me.has_custom_normals:
        return False
    nv = len(me.vertices)
    lv = np.empty(nl, dtype=np.int64)
    me.loops.foreach_get("vertex_index", lv)
    key0 = lp * nv + lv
    me.flip_normals()
    lv1 = np.empty(nl, dtype=np.int64)
    me.loops.foreach_get("vertex_index", lv1)
    tot1 = np.empty(np_, dtype=np.int64)
    me.polygons.foreach_get("loop_total", tot1)
    key1 = np.repeat(np.arange(np_), tot1) * nv + lv1
    order0 = np.argsort(key0)
    idx = order0[np.searchsorted(key0[order0], key1)]
    me.normals_split_custom_set([tuple(v) for v in cn[idx]])
    me.update()
    return True


def ensure_object_mode():
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")


# ---------------------------------------------------------------------------------------------
# STEP 1 - FIT
# ---------------------------------------------------------------------------------------------
def log_body_match(meshes, EJ, log):
    """After the fit: COD cross-section centres at EFT's landmarks vs EFT's own, and where the
    soles ended up relative to EFT's floor."""
    bpy.context.view_layer.update()
    P, T = mesh_arrays(meshes, bpy.context.evaluated_depsgraph_get())
    got = skin_sections(P, T, EJ, eft_side=True)
    res = {"body": {k: round((got[k]["fwd"] - EFT_SKIN[k][0]) * 100, 1)
                    for k in SKIN_SPEC if k in got and k in EFT_SKIN}}
    if res["body"]:
        log("Body match after fit (COD - EFT section centre, forward cm): " +
            ", ".join(f"{k} {v:+.1f}" for k, v in res["body"].items()))
    sol = cod_sole(P, {f"j_ankle_{c}": EJ[E(e + "Foot")] for c, e in SIDES})
    if sol is not None:
        n_up, p0 = sol
        res["soles"] = round((p0[2] - EFT_FLOOR_Z) * 100, 1)
        res["sole_tilt"] = round(math.degrees(math.acos(min(1, n_up[2]))), 1)
        log(f"Soles after fit: {res['soles']:+.1f} cm from EFT floor, tilt "
            f"{res['sole_tilt']:.1f} deg")
    return res


def run_fit(cod_arms, eft, match_lengths=True, fit_scale=False, apply_tweaks=True, log=None,
            basename="", match_body=True, match_height=False, neck_max_lean=NECK_MAX_LEAN,
            head_forward=0.5, face_landmark="eyes", head_height=None, match_fingertips=True,
            viewmodel=False):
    """head_height: "off" (keep the COD height), "limited" (spine + neck stretched at most
    +-HEAD_HEIGHT_LIMIT so the face / eyes reach EFT's height) or "full" (0.75 - 1.35);
    None = "full" if match_height else "limited".  face_landmark: "eyes" (eyeball centres, falls
    back to the nose) or "nose"."""
    log = log or Log()
    if head_height is None:
        head_height = "full" if match_height else "limited"
    if not match_body:
        head_height = "off"
    H_LIM = {"off": None, "limited": (1.0 - HEAD_HEIGHT_LIMIT, 1.0 + HEAD_HEIGHT_LIMIT),
             "full": (0.75, 1.35)}[head_height]
    ensure_object_mode()
    vms = [] if viewmodel else [a for a in cod_arms if is_viewmodel(a)]
    for a in vms:
        log(f"Skipping '{a.name}': first-person viewmodel skeleton (has tag_view)")
        a.hide_set(True)
        for o in meshes_of(a):
            o.hide_set(True)
    cod_arms = [a for a in cod_arms if a not in vms]
    if not cod_arms:
        raise RuntimeError("No COD armature found (expected bones named j_* / tag_origin).")
    for a in cod_arms:
        log(f"Skeleton '{a.name}': {len(a.data.bones)} bones "
            f"({'head model' if is_head_skeleton(a) else 'body / body part'})")
    all_names = set()
    for a in cod_arms:
        all_names.update(a.data.bones.keys())
    log(f"Detected skeleton family: {detect_family(all_names)}")
    if bpy.data.objects.get(RIG_NAME):
        raise RuntimeError(f"'{RIG_NAME}' already exists - run Convert or Reset first.")
    EJ = eft_joints(eft)

    # --- primary armature = most alignment landmarks (the body) ---
    def n_land(a):
        return sum(1 for c, _ in ALIGN_LANDMARKS if c in a.data.bones)
    cod_arms = sorted(cod_arms, key=n_land, reverse=True)
    prim = cod_arms[0]
    PJ = world_joints(prim)
    pairs = [(c, E(e)) for c, e in ALIGN_LANDMARKS if c in PJ and E(e) in EJ]
    src = np.array([PJ[c] for c, _ in pairs]) if pairs else np.zeros((0, 3))
    dst = np.array([EJ[e] for _, e in pairs]) if pairs else np.zeros((0, 3))
    # 3 non-collinear joints fix rotation + offset (e.g. a head on its own: spine4/neck/head)
    if len(pairs) < 3 or np.linalg.matrix_rank(src - src.mean(0), tol=1e-3 * np.abs(src).max()) < 2:
        raise RuntimeError(f"Only {len(pairs)} usable alignment landmarks on '{prim.name}' - "
                           "need at least spine4/neck/head (select the COD body armature).")
    if len(pairs) < 6:
        log(f"NOTE: only {len(pairs)} landmarks ({', '.join(c for c, _ in pairs)}) - "
            "alignment is from those joints alone")
    s, R, t = align_to_eft(PJ, EJ, pairs, fit_scale)
    S_prim = sim_to_matrix(s, R, t)
    res = np.linalg.norm((src @ (s * R).T + t) - dst, axis=1)
    log(f"Alignment of '{prim.name}': scale {s:.5f}, landmark error mean {res.mean()*100:.2f} cm, "
        f"max {res.max()*100:.2f} cm ({pairs[int(res.argmax())][0]})")
    arm_S = {prim.name: S_prim}

    # --- other COD armatures (head model / Cold War parts, often exported in their own space) --
    s_prim = float(np.cbrt(abs(np.linalg.det(S_prim[:3, :3]))))    # file units -> metres
    for a in cod_arms[1:]:
        AJ = world_joints(a)
        shared = [n for n in AJ if n in PJ and not n.startswith("tag_")]
        res = robust_rigid(AJ, PJ, shared)
        if res is not None:
            M, used, dropped, err = res
            arm_S[a.name] = S_prim @ M
            log(f"'{a.name}' aligned to '{prim.name}' via {len(used)} shared bones "
                f"(max error {err * s_prim * 100:.2f} cm"
                + (f"; ignored {len(dropped)} bone(s) posed differently in the two files: "
                   f"{', '.join(dropped[:6])}" if dropped else "") + ")")
        else:
            pairs2 = [(c, E(e)) for c, e in ALIGN_LANDMARKS if c in AJ and E(e) in EJ]
            src2 = np.array([AJ[c] for c, _ in pairs2]) if pairs2 else np.zeros((0, 3))
            if len(pairs2) < 3 or np.linalg.matrix_rank(src2 - src2.mean(0),
                                                         tol=1e-3 * np.abs(src2).max()) < 2:
                log(f"WARNING: '{a.name}' shares too few bones - skipped")
                continue
            s2, R2, t2 = align_to_eft(AJ, EJ, pairs2, fit_scale)
            arm_S[a.name] = sim_to_matrix(s2, R2, t2)
            log(f"'{a.name}' aligned directly to EFT via {len(pairs2)} landmarks")

    # --- joints of the union skeleton in EFT space ---
    J, parent = {}, {}
    for a in cod_arms:
        if a.name not in arm_S:
            continue
        S = arm_S[a.name]
        for b in a.data.bones:
            p = S @ np.append(np3(a.matrix_world @ b.head_local), 1.0)
            if b.name not in J:
                J[b.name] = p[:3]
                parent[b.name] = b.parent.name if b.parent else None
            elif parent[b.name] is None and b.parent is not None:
                parent[b.name] = b.parent.name

    # --- bake alignment into the meshes, unparent, identity transforms ---
    meshes = []
    n_flip = 0
    for a in cod_arms:
        if a.name not in arm_S:
            continue
        S = to_bl(arm_S[a.name])
        for o in meshes_of(a):
            mats = [m.name for m in o.data.materials if m]
            if mats and all(PLACEHOLDER_MAT.match(m) for m in mats):
                log(f"Removed '{o.name}' ({len(o.data.vertices)} verts): only Maya default "
                    f"material '{mats[0]}' - placeholder/helper geometry in the export")
                bpy.data.objects.remove(o, do_unlink=True)
                continue
            if o.data.users > 1:
                o.data = o.data.copy()
            o["cod2eft_src"] = a.name
            o["cod2eft_role"] = "head" if is_head_skeleton(a) else "body"
            if fix_winding(o):
                n_flip += 1
            ag = o.get("cod2eft_winding_agree", 1.0)
            if 0.3 < ag < 0.7:
                log(f"  NOTE {o.name}: only {ag:.0%} of faces agree with their normals (double-"
                    "sided or mixed geometry?) - check its shading after conversion")
            Mw = S @ o.matrix_world
            o.parent = None
            o.data.transform(Mw, shape_keys=True)
            if Mw.determinant() < 0:
                o.data.flip_normals()
            o.matrix_world = Matrix.Identity(4)
            meshes.append(o)
    log(f"{len(meshes)} COD mesh object(s) moved into EFT space")
    if n_flip:
        log(f"Fixed inside-out triangle winding on {n_flip} mesh(es) (faces wound against their "
            "own normals in the export - would render inside-out in Unity)")

    # --- rest frames of the fit rig ---
    FIT = fit_table()
    # fingertips = virtual joints (".tip") that the distal finger segments aim at
    JX, EJX = dict(J), dict(EJ)
    if match_body and match_fingertips:
        tips = cod_fingertips(meshes, J)
        for b3, p in tips.items():
            c = b3.split("_")[2]
            d = dict(FINGERS)[b3.split("_")[1]]
            e = dict(SIDES)[c]
            eb = E(f"{e}Digit{d}3")
            if eb in EJ:
                JX[b3 + ".tip"] = p
                EJX[eb + ".tip"] = EJ[eb] + np.array(EFT_TIP_OFFSET[f"{e}{d}"])
        if tips:
            log(f"Fingertips: {len(tips)} COD fingertip(s) found - the last finger segments are "
                f"aimed at EFT's fingertips (length change limited to x{TIP_RATIO[0]}-"
                f"x{TIP_RATIO[1]})")

    def jn(n):
        return JX.get(n)

    def eff_mode(f):
        m = f["stretch"]
        if m == "tip":
            if not (match_body and match_fingertips):
                return "skip"
            return "tip" if match_lengths else "aim"
        if not match_body:                      # joint-on-joint behaviour
            if m == "aim":
                return None
            if m == "level":
                return None if f["cod_aim"] else "skip"
            return m if match_lengths else None
        if m == "reach" and not match_lengths:
            return "aim"
        return m

    order = sorted(J.keys(), key=lambda n: _depth(n, parent))
    rest = {}
    fitinfo = {}
    for n in order:
        f = FIT.get(n)
        usable = False
        mode = eff_mode(f) if f else "skip"
        if f and mode == "level" and not f["cod_aim"]:
            # orientation-only bone (head): no aim joints needed
            p = parent.get(n)
            Fc = rest[p][0] if p in rest else np.eye(3)
            fitinfo[n] = dict(Fc=Fc, mode="level", eft=f["eft"], eft_aim=None, cod_aim=None,
                              skin=None, lc=0.1)
            rest[n] = (Fc, max(0.02, rest[p][1] * 0.5) if p in rest else 0.1)
            continue
        if f and mode != "skip":
            need_c = [n, f["cod_aim"], f["sec"][0], f["sec"][1]]
            need_e = [f["eft"], f["eft_aim"], f["sec"][2], f["sec"][3]]
            usable = all(jn(x) is not None for x in need_c) and all(x in EJX for x in need_e)
        if usable:
            pc = jn(f["cod_aim"]) - jn(n)
            sc = jn(f["sec"][1]) - jn(f["sec"][0])
            pe = EJX[f["eft_aim"]] - EJX[f["eft"]]
            se = EJX[f["sec"][3]] - EJX[f["sec"][2]]
            if np.linalg.norm(pc) < 1e-5 or np.linalg.norm(pe) < 1e-5:
                usable = False
        if usable:
            Fc = frame(pc, sc)
            fitinfo[n] = dict(Fc=Fc, pe=pe, se=se, lc=np.linalg.norm(pc), le=np.linalg.norm(pe),
                              mode=mode, eft=f["eft"], eft_aim=f["eft_aim"],
                              cod_aim=f["cod_aim"], skin=f["skin"] if match_body else None)
            rest[n] = (Fc, np.linalg.norm(pc))
        else:
            p = parent.get(n)
            if p and p in rest:
                rest[n] = (rest[p][0], max(0.02, rest[p][1] * 0.25))
            else:
                rest[n] = (np.eye(3), 0.05)
    log(f"Fit rig: {len(J)} bones, {len(fitinfo)} driven by EFT directions")

    # --- body-volume landmarks (see SKIN_SPEC) and the ground the model stands on ---
    oJ, oE, R_level, ankle_h, sole, nose_c = {}, {}, np.eye(3), {}, None, None
    EFT_FACE, face_what = EFT_NOSE, "nose tip"
    if match_body:
        P, T = mesh_arrays(meshes)
        # (first-person arms: no body to measure - the arm pieces are cut, so their sections
        # would mislead the shoulder placement)
        body_sections = {} if viewmodel else skin_sections(P, T, J, eft_side=False)
        clamped = []
        for key, sec in body_sections.items():
            fr = section_frame(EJ, key, True)
            if fr is None or key not in EFT_SKIN:
                continue
            fwd, lat = sec["fwd"], sec["lat"]
            if key in COD_SKIN_PRIOR:
                pf, pl = COD_SKIN_PRIOR[key]
                f2 = min(max(fwd, pf - SKIN_CLAMP), pf + SKIN_CLAMP)
                l2 = min(max(lat, pl - SKIN_CLAMP), pl + SKIN_CLAMP) if SKIN_SPEC[key][6] else lat
                if abs(f2 - fwd) > 1e-6 or abs(l2 - lat) > 1e-6:
                    clamped.append(f"{key} ({(fwd - f2) * 100:+.1f} cm)")
                fwd, lat = f2, l2
            oJ[key] = fwd * sec["fw"] + lat * sec["lat_axis"]
            ef, el = EFT_SKIN[key]
            oE[key] = ef * fr[2] + el * fr[3]
        # 2.5.2: a pelvis section far BEHIND the waist's is body shape (wide hips / glutes: MW4
        # valeria's pelvis sits 12.3 cm behind her waist section, EFT's 4.3 cm, other test
        # characters 0.6 - 3.2 cm), not where the pelvis is.  Matched as it was, it pushed the
        # whole body forward and left the waist 4 cm behind EFT's: the swayback / twisted
        # waist.  So the pelvis section may sit at most EFT's gap + PELVIS_WAIST_SLACK behind the
        # waist section.
        if "pelvis" in oJ and "spine2" in oJ and "pelvis" in body_sections and \
                "spine2" in body_sections:
            gap_e = EFT_SKIN["spine2"][0] - EFT_SKIN["pelvis"][0]
            pf = float(oJ["pelvis"] @ body_sections["pelvis"]["fw"])
            sf = float(oJ["spine2"] @ body_sections["spine2"]["fw"])
            lo = sf - gap_e - PELVIS_WAIST_SLACK
            if pf < lo:
                oJ["pelvis"] = oJ["pelvis"] + (lo - pf) * body_sections["pelvis"]["fw"]
                clamped.append(f"pelvis vs waist ({(pf - lo) * 100:+.1f} cm, hip shape)")
        if oJ:
            log("Body volume: COD section centre vs joint (fwd cm) -> EFT: " + ", ".join(
                f"{k} {body_sections[k]['fwd'] * 100:+.1f}->{EFT_SKIN[k][0] * 100:+.1f}"
                for k in SKIN_SPEC if k in oJ))
        if clamped:
            log("  limited unusual section centres (gear on the torso/legs?): " +
                ", ".join(clamped))
        missing = [k for k in SKIN_SPEC if k not in oJ]
        if missing and len(missing) < len(SKIN_SPEC):
            log(f"  no usable cross-section at: {', '.join(missing)} (joint position used)")
        eyes_c = cod_eyes(J) if face_landmark == "eyes" else None
        if eyes_c is not None:
            nose_c, EFT_FACE = eyes_c, EFT_EYES
            face_what = "eye centres"
            log("Face: COD eye centres used to line the head up with EFT's eyes (front/back"
                + (", height" if H_LIM else "") + ")")
        else:
            nose_c, how = cod_nose(P, J)
            EFT_FACE, face_what = EFT_NOSE, "nose tip"
            if nose_c is not None:
                log(f"Face: COD {how} used to line the head up with EFT's face (front/back"
                    + (", height" if H_LIM else "") + ")")
            elif "j_head" in J:
                log(f"Face: head placed by joints ({how})")
        sole = cod_sole(P, J)
        if sole is not None:
            n_up, p0 = sole
            R_level = rot_between(n_up, np.array([0.0, 0.0, 1.0]))
            for c, _ in SIDES:
                ankle_h[c] = float((J[f"j_ankle_{c}"] - p0) @ n_up)
            log(f"Ground: COD soles define a floor tilted {math.degrees(math.acos(min(1, n_up[2]))):.1f} "
                f"deg after alignment (levelled); ankle joints {np.mean(list(ankle_h.values())) * 100:.1f} cm "
                f"above the soles")
        elif any(f"j_ankle_{c}" in J for c, _ in SIDES):
            log("Ground: could not find both soles - feet keep their bind orientation, ankles on "
                "the EFT ankle joints")

    # --- create the rig ---
    rig_data = bpy.data.armatures.new(RIG_NAME)
    rig = bpy.data.objects.new(RIG_NAME, rig_data)
    bpy.context.scene.collection.objects.link(rig)
    rig.show_in_front = True
    for o in bpy.context.selected_objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    ebs = rig_data.edit_bones
    for n in order:
        eb = ebs.new(n)
        Fr, L = rest[n]
        eb.head = (0, 0, 0)
        eb.tail = (0, max(L, 0.005), 0)
        eb.matrix = to_bl(mat4(Fr, J[n]))
    for n in order:
        p = parent.get(n)
        if p and p in ebs:
            ebs[n].parent = ebs[p]
            ebs[n].use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    for b in rig_data.bones:
        b.inherit_scale = "NONE" if b.name in fitinfo else "FULL"
        b.use_deform = True

    # --- desired pose (world == armature space, rig at identity) ---
    Z = np.array([0.0, 0.0, 1.0])

    def horiz(v):
        v = v - (v @ Z) * Z
        n = np.linalg.norm(v)
        return v / n if n > 1e-9 else None

    def aim_target(fi, R, h_new=None, mode=None, length_scale=1.0):
        tgt = EJX[fi["eft_aim"]].copy()
        key = fi["skin"]
        if key == "face" and nose_c is not None and fi["cod_aim"] in J and h_new is not None:
            # Where the head joint must be for the (level) head's nose to meet EFT's nose.
            # Only front/back and sideways are matched: the neck keeps its COD length, so a
            # shorter or taller character keeps its height.  EFT carries its head further
            # forward over the neck than COD does, so the neck leans forward - at most
            # NECK_MAX_LEAN from vertical; whatever is left is reported.
            want = EFT_FACE - R_level @ (nose_c - J[fi["cod_aim"]])
            L = fi["lc"] * length_scale
            w = (want - h_new)[:2]
            wmax = L * math.sin(math.radians(neck_max_lean))
            if np.linalg.norm(w) > wmax:
                w = w * wmax / np.linalg.norm(w)
            return h_new + np.array([w[0], w[1], math.sqrt(max(L * L - w @ w, 0.0))])
        if key and key in oJ:
            tgt = tgt + oE[key] - R @ oJ[key]
        if key and key.startswith("shin_") and sole is not None:
            c = "le" if key.endswith("L") else "ri"
            if c in ankle_h:
                tgt[2] = EFT_FLOOR_Z + ankle_h[c]          # soles on EFT's floor
        return tgt

    # Segments whose START is moved so a cross-section just below it matches EFT's (the end is
    # pinned by the next joint's target): the upper arm moves the shoulder, through the
    # clavicle's reach target.  ("self" = translate the joint itself; tried for the hips - COD hip
    # joints sit ~3 cm wider than EFT's - but moving them squeezes the groin, which is weighted
    # to the pelvis, so the upper-thigh section is only measured and reported.)
    ROOT_SKIN = {}
    if match_body:
        for c, e in SIDES:
            ROOT_SKIN[f"j_shoulder_{c}"] = ("upperarm_" + e, f"j_clavicle_{c}")
    ROOT_MAX = 0.04
    knuckle_fit = bool(match_body and match_fingertips)
    knuckle_moves = {}
    SPINE_EFT = {E("Spine1"), E("Spine2"), E("Spine3"), E("Neck")}

    def solve(extra):
        """One pass over the skeleton.  extra = corrections found by the previous pass."""
        D, desired, seg = {}, {}, {}
        knuckle_moves.clear()
        root_shift = None
        spine_ratio = [None]
        for n in order:
            p = parent.get(n)
            Dp = D.get(p, np.eye(4)) if p else np.eye(4)
            Rest = from_bl(rig_data.bones[n].matrix_local)
            if n not in fitinfo:
                D[n] = Dp
                desired[n] = Dp @ Rest
                continue
            fi = fitinfo[n]
            h = J[n]
            h_new = (Dp @ np.append(h, 1.0))[:3]
            if ROOT_SKIN.get(n, (None, None))[1] == "self":
                h_new = h_new + extra["root"].get(n, 0.0)
            if knuckle_fit and n in KNUCKLE_BONES and fi["eft"] in EJ:
                # finger root moved onto EFT's knuckle (at most KNUCKLE_MAX)
                dk = EJ[fi["eft"]] - h_new
                if np.linalg.norm(dk) > KNUCKLE_MAX:
                    dk = dk * KNUCKLE_MAX / np.linalg.norm(dk)
                h_new = h_new + dk
                knuckle_moves[n] = float(np.linalg.norm(dk))
            mode = fi["mode"]
            spine_stretch = None
            if H_LIM and mode == "aim" and fi["eft"] in SPINE_EFT:
                # Head height: the spine AND neck are stretched by ONE factor (per-segment
                # stretching would squash one segment and stretch the next - COD and EFT space
                # their spine joints differently) so the face / eyes end up at EFT's height.
                # "limited" allows +-HEAD_HEIGHT_LIMIT, "full" 0.75 - 1.35.  Without a face, the
                # head joint goes to EFT's head joint height.
                if spine_ratio[0] is None and extra.get("spine") is not None:
                    spine_ratio[0] = extra["spine"]
                if spine_ratio[0] is None and "j_head" in J:
                    up = R_level @ (J["j_head"] - h)
                    if nose_c is not None:
                        above = R_level @ (nose_c - J["j_head"])
                        want_z = EFT_FACE[2] - above[2]
                    else:
                        want_z = EJ[E("Head")][2]
                    if up[2] > 1e-3:
                        spine_ratio[0] = float(np.clip((want_z - h_new[2]) / up[2], *H_LIM))
                spine_stretch = spine_ratio[0]
            Sm = np.eye(3)
            ratio = 1.0
            if mode == "level":
                R = R_level
                if fi["cod_aim"]:                   # foot: also turn to EFT's heading
                    hc = horiz(R_level @ (J[fi["cod_aim"]] - h))
                    he = horiz(EJ[fi["eft_aim"]] - EJ[fi["eft"]])
                    if hc is not None and he is not None:
                        R = rot_between(hc, he) @ R_level
            elif mode in ("reach", "aim", "tip"):
                R = np.eye(3)
                for _ in range(4 if fi["skin"] in oJ else 1):
                    tgt = aim_target(fi, R, h_new, mode,
                                     spine_stretch if spine_stretch is not None else 1.0)
                    if n == "j_spine4":
                        tgt = tgt + extra["neck"]
                    ch = fi["cod_aim"]
                    if ROOT_SKIN.get(ch, (None, None))[1] == n:
                        tgt = tgt + extra["root"].get(ch, 0.0)
                    pe = tgt - h_new
                    Fe = frame(pe, fi["se"], fallback=fi["Fc"][:, 0])
                    R = Fe @ fi["Fc"].T
                if mode in ("reach", "tip"):
                    ratio = np.linalg.norm(pe) / fi["lc"]
                    if fi["skin"] == "face":
                        ratio = min(max(ratio, 0.8), 1.5)
                    if mode == "tip":
                        ratio = min(max(ratio, TIP_RATIO[0]), TIP_RATIO[1])
                elif spine_stretch is not None:
                    ratio = spine_stretch
                if ratio != 1.0:
                    y = Rest[:3, 1]
                    Sm = np.eye(3) + (ratio - 1.0) * np.outer(y, y)
            else:
                # direction only (pelvis, or joint-on-joint mode)
                Fe = frame(fi["pe"], fi["se"], fallback=fi["Fc"][:, 0])
                R = Fe @ fi["Fc"].T
                if "pelvis" in oJ and fi["eft"] == E("Pelvis") and root_shift is None:
                    # move the body forward/back so the hip section centre matches EFT's
                    fw = section_frame(EJ, "pelvis", True)[2]
                    tgt = EJ[E("Pelvis")] + oE["pelvis"] - R @ oJ["pelvis"]
                    root_shift = ((tgt - h_new) @ fw) * fw
                    h_new = h_new + root_shift
            fi["ratio"] = ratio
            Dj = mat4(t=h_new) @ mat4(R @ Sm) @ mat4(t=-h)
            D[n] = Dj
            desired[n] = Dj @ Rest
            seg[n] = (R, Sm, h_new)
        # corrections for the next pass
        nxt = {"root": dict(extra["root"]), "neck": extra["neck"].copy(), "gap0": extra["gap0"],
               "spine": spine_ratio[0]}
        for n, (key, how) in ROOT_SKIN.items():
            if n not in seg or key not in oJ:
                continue
            R, Sm, h_new = seg[n]
            fr_c = section_frame(J, key, False)
            fr_e = section_frame(EJ, key, True)
            if fr_c is None or fr_e is None:
                continue
            t = SKIN_SPEC[key][0][2]
            got = h_new + (R @ Sm) @ (fr_c[0] - J[n]) + R @ oJ[key]
            want = fr_e[0] + oE[key]
            d = want - got
            ax = R @ fr_c[1]
            d = d - (d @ ax) * ax                   # across the limb only
            tot = nxt["root"].get(n, np.zeros(3)) + d / (1.0 - t)
            if np.linalg.norm(tot) > ROOT_MAX:
                tot = tot * ROOT_MAX / np.linalg.norm(tot)
            nxt["root"][n] = tot
        face_gap = None
        if nose_c is not None and "j_head" in D:
            nn = (D["j_head"] @ np.append(nose_c, 1.0))[:3]
            face_gap = EFT_FACE - nn
            if H_LIM and spine_ratio[0] is not None and "j_spinelower" in J:
                # refine the stretch factor from the height the face actually reached
                up_z = (R_level @ (J["j_head"] - J["j_spinelower"]))[2]
                if up_z > 1e-3:
                    nxt["spine"] = float(np.clip(spine_ratio[0] + face_gap[2] / up_z, *H_LIM))
            if head_forward > 0 and "j_spine4" in seg and extra["gap0"] is None:
                # the gap left with the neck alone; "Head forward" = the share of it that the
                # upper spine leaning forward takes up
                g = face_gap.copy()
                g[2] = 0.0                              # only front/back and sideways
                nxt["gap0"] = g
                nxt["neck"] = head_forward * g
        return D, desired, root_shift, nxt, face_gap

    face_gap = None
    extra = {"root": {}, "neck": np.zeros(3), "gap0": None, "spine": None}
    for _ in range(6 if match_body else 1):
        applied = extra
        D, desired, root_shift, extra, face_gap = solve(applied)
    extra = applied                              # the corrections the final pose was built with
    if root_shift is not None:
        log(f"Body moved {(root_shift @ FWD) * 100:+.1f} cm forward to match the hip section")
    moved = {n: v for n, v in extra["root"].items() if np.linalg.norm(v) > 1e-4}
    if moved:
        log("Shoulders moved to match the upper-arm sections: " + ", ".join(
            f"{n} {np.linalg.norm(v) * 100:.1f} cm" for n, v in sorted(moved.items())))
    if H_LIM and extra.get("spine"):
        log(f"Head height ({head_height}): spine + neck x{extra['spine']:.3f} (limit "
            f"{H_LIM[0]:.2f}-{H_LIM[1]:.2f}) to bring the {face_what} to EFT's height")
    if np.linalg.norm(extra["neck"]) > 1e-4:
        log(f"Upper spine leaned: neck base moved {extra['neck'] @ FWD * 100:+.1f} cm forward "
            f"(Head forward {head_forward:.2f})")
    if face_gap is not None:
        d = -face_gap * 100
        log(f"Face after fit: COD {face_what} {d @ FWD:+.1f} cm forward, {d[2]:+.1f} cm up, "
            f"{d[0]:+.1f} cm sideways of EFT's")

    if knuckle_moves:
        km = list(knuckle_moves.values())
        capped = max(km) > KNUCKLE_MAX - 1e-6
        log(f"Knuckles: {len(km)} finger roots moved onto EFT's knuckles (mean "
            f"{np.mean(km) * 100:.1f} cm, max {max(km) * 100:.1f} cm"
            + (f", some limited to {KNUCKLE_MAX * 100:.0f} cm" if capped else "") + ")")
    tip_err = []
    for n, fi in fitinfo.items():
        if fi.get("mode") == "tip" and n in D:
            got = (D[n] @ np.append(JX[fi["cod_aim"]], 1.0))[:3]
            tip_err.append(np.linalg.norm(got - EJX[fi["eft_aim"]]))
    if tip_err:
        log(f"Fingertips after fit: {np.mean(tip_err) * 100:.1f} cm from EFT's on average, "
            f"max {max(tip_err) * 100:.1f} cm ({len(tip_err)} fingers)")

    # --- apply pose level by level, then verify ---
    bpy.ops.object.mode_set(mode="POSE")
    for pb in rig.pose.bones:
        pb.rotation_mode = "QUATERNION"
        pb.matrix_basis = Matrix.Identity(4)
    depth = {n: _depth(n, parent) for n in order}
    for lvl in range(max(depth.values()) + 1):
        for n in order:
            if depth[n] == lvl and n in fitinfo:
                rig.pose.bones[n].matrix = to_bl(desired[n])
        bpy.context.view_layer.update()
    worst, worst_n = 0.0, None
    for n in order:
        err = np.abs(from_bl(rig.pose.bones[n].matrix) - desired[n]).max()
        if err > worst:
            worst, worst_n = err, n
    log(f"Pose verification: max matrix error {worst:.2e} ({worst_n})")
    if worst > 1e-3:
        log("WARNING: pose could not be reproduced exactly - check the report")

    # store the automatic pose so user tweaks can be saved as differences
    rig["cod2eft_auto_basis"] = json.dumps(
        {pb.name: [list(r) for r in pb.matrix_basis] for pb in rig.pose.bones})
    if apply_tweaks and os.path.isfile(tweaks_file()):
        with open(tweaks_file(), "r", encoding="utf-8") as fh:
            tw = json.load(fh)
        n_tw = 0
        for name, q in tw.get("bones", {}).items():
            pb = rig.pose.bones.get(name)
            if pb:
                pb.rotation_quaternion = pb.rotation_quaternion @ Quaternion(q)
                n_tw += 1
        bpy.context.view_layer.update()
        log(f"Applied saved pose tweaks to {n_tw} bone(s) from {os.path.basename(tweaks_file())}")
    bpy.ops.object.mode_set(mode="OBJECT")

    # --- report: stretches, and where the joints ended up ---
    rs = sorted(((n, fi.get("ratio", 1.0)) for n, fi in fitinfo.items()),
                key=lambda kv: -abs(math.log(kv[1])))[:6]
    log("Largest segment stretches: " + ", ".join(f"{n} x{r:.3f}" for n, r in rs))
    rows = {}
    for n, fi in fitinfo.items():
        if fi.get("cod_aim") and fi["cod_aim"] in rig.pose.bones and not fi.get("skin"):
            got = np3(rig.pose.bones[fi["cod_aim"]].head)
            rows[(fi["cod_aim"], fi["eft_aim"])] = np.linalg.norm(got - EJ[fi["eft_aim"]])
    worst = sorted(rows.items(), key=lambda kv: -kv[1])[:6]
    log("Largest joint offsets (COD joint -> EFT joint, bones placed by joint): " +
        ", ".join(f"{c}->{e.replace(EFT_PREFIX, '')} {d*100:.1f}cm" for (c, e), d in worst))

    # --- point meshes at the rig ---
    for o in meshes:
        mods = [m for m in o.modifiers if m.type == "ARMATURE"]
        if mods:
            mods[0].object = rig
            for m in mods[1:]:
                o.modifiers.remove(m)
        else:
            m = o.modifiers.new("Armature", "ARMATURE")
            m.object = rig
        o["cod2eft_mesh"] = True

    summary = {"name": basename or _basename(prim), "match_body": bool(match_body)}
    if face_gap is not None:
        g = -face_gap * 100
        summary["face"] = [round(float(g @ FWD), 1), round(float(g[2]), 1)]
        summary["face_what"] = face_what
    if tip_err:
        summary["tips"] = [round(float(np.mean(tip_err)) * 100, 1),
                           round(float(max(tip_err)) * 100, 1)]
    if match_body and oJ:
        try:
            summary.update(log_body_match(meshes, EJ, log))
        except Exception as ex:                        # report only - never fail the fit
            log(f"(body-match check skipped: {ex})")
    bpy.context.scene["cod2eft_last_fit"] = json.dumps(summary)

    for a in cod_arms:
        a.hide_set(True)
        a.hide_render = True
        a["cod2eft_source_armature"] = True
    rig["cod2eft_sources"] = json.dumps([a.name for a in cod_arms] +
                                        [e.name for e in _fbx_root_empties()])
    rig["cod2eft_parent"] = json.dumps(parent)
    rig["cod2eft_basename"] = basename or _basename(prim)
    return rig, meshes, log


def _depth(n, parent):
    d = 0
    p = parent.get(n)
    guard = 0
    while p is not None and guard < 512:
        d += 1
        p = parent.get(p)
        guard += 1
    return d


def _fbx_root_empties():
    """Saluki/Greyhound FBX imports leave an empty named after the file (body_*/head_*_LODn)
    next to the 'Joints' armature, with nothing parented to it."""
    import re
    return [o for o in bpy.context.scene.objects
            if o.type == "EMPTY" and o.parent is None and not o.children
            and re.match(r"^(body|head)_.*_lod\d+(\.\d+)?$", o.name, re.I)]


def _basename(arm):
    # Cast import: armature is named after the file.  FBX (Saluki/Greyhound) import: armature is
    # "Joints" and the file name sits on a separate empty -> fall back to the .blend name.
    name = arm.name
    if name.startswith("Joints"):
        emp = _fbx_root_empties()
        body = [e for e in emp if e.name.lower().startswith("body_")] or emp
        name = body[0].name if body else (
            bpy.path.display_name_from_filepath(bpy.data.filepath) or "COD")
    for pre in ("body_", "head_"):
        if name.startswith(pre):
            name = name[len(pre):]
    for suf in ("_LOD0", "_lod0"):
        name = name.replace(suf, "")
    return name


# ---------------------------------------------------------------------------------------------
# STEP 2 - CONVERT
# ---------------------------------------------------------------------------------------------
def bake_armature(o, rig, log):
    """Apply the rig deformation to mesh o (keeps custom normals; supports shape keys)."""
    mod = next(m for m in o.modifiers if m.type == "ARMATURE" and m.object == rig)
    others = [(m, m.show_viewport) for m in o.modifiers if m != mod]
    for m, _ in others:
        m.show_viewport = False
    me = o.data
    if me.shape_keys and len(me.shape_keys.key_blocks) > 0:
        kb = me.shape_keys.key_blocks
        n = len(me.vertices)
        coords = {}
        old_show, old_idx = o.show_only_shape_key, o.active_shape_key_index
        o.show_only_shape_key = True
        dg = bpy.context.evaluated_depsgraph_get()
        for i, k in enumerate(kb):
            o.active_shape_key_index = i
            dg.update()
            ev = o.evaluated_get(dg)
            em = ev.to_mesh()
            buf = np.empty(n * 3)
            em.vertices.foreach_get("co", buf)
            coords[k.name] = buf
            ev.to_mesh_clear()
        o.show_only_shape_key, o.active_shape_key_index = old_show, old_idx
        o.modifiers.remove(mod)
        for k in kb:
            k.data.foreach_set("co", coords[k.name])
        me.vertices.foreach_set("co", coords[kb[0].name])
        me.update()
        log(f"  {o.name}: baked with {len(kb)} shape key(s)")
    else:
        with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o],
                                       selected_editable_objects=[o]):
            bpy.ops.object.modifier_apply(modifier=mod.name)
    for m, v in others:
        m.show_viewport = v


def interp_profile(prof, t):
    tt = np.clip(t, prof["t"][0], prof["t"][-1])
    W = np.array(prof["w"])
    return np.stack([np.interp(tt, prof["t"], W[:, k]) for k in range(W.shape[1])], axis=-1)


def remap_weights(o, rig, eft, wmap, parent, max_influences, log):
    me = o.data
    vg_names = [vg.name for vg in o.vertex_groups]
    eft_bones = [b.name for b in eft.data.bones if b.use_deform]
    eft_set = set(eft_bones)
    EJ = eft_joints(eft)
    Winv = eft.matrix_world.inverted()

    # resolve each COD group -> EFT bone
    resolve, fallback_used, unresolved = {}, {}, []
    for g in vg_names:
        if g in eft_set:
            resolve[g] = {g: 1.0}
            continue
        cur, hops = g, 0
        while cur is not None and cur not in wmap and hops < 256:
            cur = parent.get(cur)
            hops += 1
        tgt = wmap.get(cur) if cur is not None else None
        if tgt and all(b in eft_set for b in tgt):
            resolve[g] = tgt
            if hops:
                fallback_used[g] = (cur, tgt)
        else:
            unresolved.append(g)

    n = len(me.vertices)
    idx_of = {b: i for i, b in enumerate(eft_bones)}
    W = np.zeros((n, len(eft_bones)), dtype=np.float64)
    orphan = {}
    for v in me.vertices:
        for g in v.groups:
            if g.weight <= 0.0:
                continue
            name = vg_names[g.group]
            tgt = resolve.get(name)
            if tgt is None:
                orphan[name] = orphan.get(name, 0) + 1
                continue
            for b, share in tgt.items():
                W[v.index, idx_of[b]] += g.weight * share

    # groups with no bone anywhere -> nearest EFT joint to the weighted vertices' centre
    if orphan:
        co = np.empty(n * 3)
        me.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3)
        for name in orphan:
            vg = o.vertex_groups[name]
            ids, ws = [], []
            for v in me.vertices:
                for g in v.groups:
                    if g.group == vg.index and g.weight > 0:
                        ids.append(v.index)
                        ws.append(g.weight)
            c = co[ids].mean(0)
            best = min(eft_bones, key=lambda b: np.linalg.norm(EJ[b] - c))
            W[ids, idx_of[best]] += np.array(ws)
            log(f"  {o.name}: weighted group '{name}' has no mapped bone/parent -> nearest EFT "
                f"joint {best.replace(EFT_PREFIX, '')} ({len(ids)} verts) - add it to "
                f"cod2eft_bonemap.json to control this")

    # twist redistribution (forearm 1/2/3, thigh 1/2) using EFT's own falloff
    co = np.empty(n * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    co = co @ np.array(o.matrix_world)[:3, :3].T + np.array(o.matrix_world)[:3, 3]
    co = co @ np.array(Winv)[:3, :3].T + np.array(Winv)[:3, 3]
    EJl = {b.name: np3(b.head_local) for b in eft.data.bones}
    for key, prof in TWIST_PROFILES.items():
        for side in ("L", "R"):
            bones = [E(side + b) for b in prof["bones"]]
            if not all(b in idx_of for b in bones):
                continue
            cols = [idx_of[b] for b in bones]
            tot = W[:, cols].sum(1)
            sel = tot > 0
            if not sel.any():
                continue
            a, b = EJl[E(side + prof["seg"][0])], EJl[E(side + prof["seg"][1])]
            d = b - a
            t = ((co[sel] - a) @ d) / (d @ d)
            frac = interp_profile(prof, t)
            frac /= frac.sum(1, keepdims=True)
            W[np.ix_(sel.nonzero()[0], cols)] = frac * tot[sel, None]

    # limit influences + normalise
    if max_influences > 0:
        keep = np.argsort(-W, axis=1)[:, :max_influences]
        mask = np.zeros_like(W, dtype=bool)
        np.put_along_axis(mask, keep, True, axis=1)
        W[~mask] = 0.0
    W[W < 1e-4] = 0.0
    s = W.sum(1)
    zero = s <= 0
    if zero.any():
        log(f"  WARNING {o.name}: {int(zero.sum())} vertices had no weight -> assigned to Pelvis")
        W[zero, idx_of[E('Pelvis')]] = 1.0
        s = W.sum(1)
    W /= s[:, None]

    # rebuild groups: all EFT deform bones, in armature order (= WTT skin preset order)
    o.vertex_groups.clear()
    for b in eft_bones:
        o.vertex_groups.new(name=b)
    for j, b in enumerate(eft_bones):
        col = W[:, j]
        nz = np.nonzero(col)[0]
        if len(nz) == 0:
            continue
        vg = o.vertex_groups[b]
        # group equal weights to cut API calls
        vals, inv = np.unique(np.round(col[nz], 5), return_inverse=True)
        for k, val in enumerate(vals):
            vg.add(nz[inv == k].tolist(), float(val), "REPLACE")

    return W, fallback_used


def _tgt_label(tgt):
    if len(tgt) == 1:
        return next(iter(tgt)).replace(EFT_PREFIX, "")
    return " + ".join(f"{b.replace(EFT_PREFIX, '')} {w:.0%}" for b, w in tgt.items())


def _region_masses(W, eft):
    """Per-vertex weight mass in (Head bone, Upper body incl. neck, Pelvis, Legs) for BODY files.
    Neck counts as Upper (EFT T-shirts are weighted to the neck; only the Head bone makes
    something headwear).  Pelvis is kept separate: it goes with whatever the rest of its mesh
    island is (belt -> pants, jacket/hoodie hem -> top)."""
    names = [b.name for b in eft.data.bones if b.use_deform]
    hi = [i for i, b in enumerate(names) if b == E("Head")]
    pi = [i for i, b in enumerate(names) if b == E("Pelvis")]
    gi = [i for i, b in enumerate(names) if b in PART_LOWER and b != E("Pelvis")]
    H = W[:, hi].sum(1)
    P = W[:, pi].sum(1)
    G = W[:, gi].sum(1)
    U = W.sum(1) - H - P - G
    return np.stack([H, U, P, G], axis=1)


REGIONS = ("Head", "Upper", "Lower")


def _islands(me):
    nv = len(me.vertices)
    ev = np.empty(len(me.edges) * 2, dtype=np.int64)
    me.edges.foreach_get("vertices", ev)
    ev = ev.reshape(-1, 2)
    parent = list(range(nv))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    for a, b in ev.tolist():
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    return np.array([find(i) for i in range(nv)])


def split_by_region(o, W, eft, log, min_split_share=0.2):
    """Assign faces to Head / Upper / Lower.  Whole mesh islands (a pouch, a kneepad, a boot)
    go to the region holding most of their weight; an island that really spans regions
    (a one-piece suit, a coat) is cut per face.  Returns [(region, object), ...]."""
    import bmesh
    me = o.data
    R = _region_masses(W, eft)
    isl = _islands(me)
    np_ = len(me.polygons)
    tot = np.empty(np_, dtype=np.int64)
    me.polygons.foreach_get("loop_total", tot)
    lv = np.empty(len(me.loops), dtype=np.int64)
    me.loops.foreach_get("vertex_index", lv)
    lp = np.repeat(np.arange(np_), tot)
    face_R = np.zeros((np_, R.shape[1]))
    np.add.at(face_R, lp, R[lv])
    face_isl = isl[lv[np.cumsum(tot) - tot]]
    labels = np.zeros(np_, dtype=np.int64)
    n_cut = 0
    for iid in np.unique(face_isl):
        fm = face_isl == iid
        H, U, P, G = face_R[fm].sum(0)
        tot_m = (H + U + P + G) or 1.0
        if H >= max(U + P, G + P):
            lab = 0
        elif U > G + P:
            lab = 1                      # top (pelvis/hem goes with it)
        else:
            lab = 2                      # pants / belt / boots
        labels[fm] = lab
        # a single island that really holds both torso and legs (one-piece suit, long coat)
        if fm.sum() >= 50 and U / tot_m >= min_split_share and G / tot_m >= min_split_share:
            f = face_R[fm]
            if lab == 1:
                sub = np.where(f[:, 3] > f[:, 1] + f[:, 2], 2, 1)
            else:
                sub = np.where(f[:, 1] > f[:, 3] + f[:, 2], 1, 2)
            sub = np.where(f[:, 0] > f[:, 1:].sum(1), 0, sub)
            labels[fm] = sub
            n_cut += 1
    present = [r for r in range(3) if (labels == r).any()]
    if len(present) <= 1:
        return [(REGIONS[present[0] if present else 1], o)]
    out = []
    for r in present:
        if r == present[-1]:
            ob = o
        else:
            ob = o.copy()
            ob.data = o.data.copy()
            for c in o.users_collection:
                c.objects.link(ob)
        bm = bmesh.new()
        bm.from_mesh(ob.data)
        bm.faces.ensure_lookup_table()
        kill = [f for f in bm.faces if labels[f.index] != r]
        bmesh.ops.delete(bm, geom=kill, context="FACES_ONLY")
        loose = [v for v in bm.verts if not v.link_faces]
        bmesh.ops.delete(bm, geom=loose, context="VERTS")
        bm.to_mesh(ob.data)
        bm.free()
        ob.data.update()
        out.append((REGIONS[r], ob))
    log(f"  {o.name}: split into " + ", ".join(REGIONS[r] for r in present) +
        (f" ({n_cut} island(s) cut across regions)" if n_cut else ""))
    return out


def run_convert(rig, eft, max_influences=4, join_parts=True, log=None):
    log = log or Log()
    ensure_object_mode()
    parent = json.loads(rig.get("cod2eft_parent", "{}"))
    base = rig.get("cod2eft_basename", "COD")
    wmap = load_weight_map(log)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH" and
              any(m.type == "ARMATURE" and m.object == rig for m in o.modifiers)]
    if not meshes:
        raise RuntimeError("No meshes use the fit rig - run Fit first.")
    log(f"Converting {len(meshes)} mesh object(s) to EFT weights")
    parts = {"Head": [], "Upper": [], "Lower": []}
    all_fallback = {}
    mat_src = {}
    for o in meshes:
        f = o.get("cod2eft_file")
        if f:
            for m in o.data.materials:
                if m is not None:
                    mat_src.setdefault(re.sub(r"\.\d{3}$", "", m.name), f)
    for o in meshes:
        bake_armature(o, rig, log)
        W, fb = remap_weights(o, rig, eft, wmap, parent, max_influences, log)
        all_fallback.update(fb)
        if o.get("cod2eft_role") == "head":
            parts["Head"].append(o)
            continue
        for region, ob in split_by_region(o, W, eft, log):
            parts[region].append(ob)
    if all_fallback:
        byt = {}
        for g, (anc, tgt) in all_fallback.items():
            byt.setdefault(_tgt_label(tgt), []).append(g)
        parts_txt = []
        for k, v in sorted(byt.items()):
            named = sorted(x for x in v if not x.startswith("bone_"))
            shown = named[:10]
            more = len(v) - len(shown)
            parts_txt.append(f"{k} <- {', '.join(shown)}" + (f" (+{more} more)" if more else ""))
        log("Helper bones merged via nearest mapped parent: " + "; ".join(parts_txt))

    # parent to EFT armature
    results = []
    for part, objs in parts.items():
        if not objs:
            continue
        coll_name = f"COD2EFT_{part}"
        coll = bpy.data.collections.get(coll_name) or bpy.data.collections.new(coll_name)
        if coll.name not in bpy.context.scene.collection.children:
            bpy.context.scene.collection.children.link(coll)
        for o in objs:
            for c in list(o.users_collection):
                c.objects.unlink(o)
            coll.objects.link(o)
        log(f"{part}: " + ", ".join(sorted(o.name for o in objs)))
        if join_parts and len(objs) > 1:
            with bpy.context.temp_override(active_object=objs[0], object=objs[0],
                                           selected_objects=objs, selected_editable_objects=objs):
                bpy.ops.object.join()
            objs = [objs[0]]
        for i, o in enumerate(objs):
            o.name = f"{base}_{part}" if len(objs) == 1 else f"{base}_{part}_{i:02d}"
            o.data.name = o.name
            o.parent = eft
            o.matrix_parent_inverse = eft.matrix_world.inverted()
            for m in [m for m in o.modifiers if m.type == "ARMATURE"]:
                o.modifiers.remove(m)
            m = o.modifiers.new("Armature", "ARMATURE")
            m.object = eft
            for k in ("cod2eft_mesh", "cod2eft_src", "cod2eft_winding_agree", "cod2eft_role"):
                if k in o:
                    del o[k]
            # separate sub-meshes (Join off) need their own part name - it names their textures
            o["cod2eft_part"] = part if len(objs) == 1 else f"{part}_{i:02d}"
            used = {re.sub(r"\.\d{3}$", "", m.name) for m in o.data.materials if m}
            o["cod2eft_mat_src"] = json.dumps({k: v for k, v in mat_src.items() if k in used})
            if "cod2eft_file" in o:
                del o["cod2eft_file"]
            results.append(o)

    # clean up rig + source armatures (+ their now-empty parent empties)
    srcs = json.loads(rig.get("cod2eft_sources", "[]"))
    kill = [rig] + [bpy.data.objects[n] for n in srcs if n in bpy.data.objects]
    for a in list(kill[1:]):
        p = a.parent
        while p is not None and p.type == "EMPTY" and all(c in kill for c in p.children):
            kill.append(p)
            p = p.parent
    rig_data = rig.data
    for o in kill:
        leftovers = [c for c in o.children if c not in kill]
        for c in leftovers:
            mw = c.matrix_world.copy()
            c.parent = None
            c.matrix_world = mw
        bpy.data.objects.remove(o, do_unlink=True)
    if rig_data.users == 0:
        bpy.data.armatures.remove(rig_data)
    log(f"Done: {', '.join(o.name for o in results)} parented to '{eft.name}'")
    try:
        from . import cod2eft_tools as TL
    except ImportError:
        import cod2eft_tools as TL
    try:
        lines, dists = TL.outside_report(eft, results)
        if lines:
            log("Sticks out of EFT's own body (EFT vests/rigs/helmets may clip there - use "
                "'Check gear clipping' to see where):")
            for ln in lines:
                log("  " + ln)
            last = json.loads(bpy.context.scene.get("cod2eft_last_fit", "{}"))
            last["outside"] = {o.name.rsplit("_", 1)[-1]: round(float(np.mean(
                d[~np.isnan(d)] > 0.03)) * 100) for o, d in dists.items() if np.any(~np.isnan(d))}
            bpy.context.scene["cod2eft_last_fit"] = json.dumps(last)
    except Exception as ex:                            # report only
        log(f"(gear clipping check skipped: {ex})")
    return results, log


def make_fp_hands(eft, parts, fp_file=None, log=None, source="AUTO", max_influences=4,
                  match_lengths=True, match_fingertips=True, fit_scale=False):
    """'<name>_Hands' for EFT's first-person hands.  source "AUTO": from the character's COD
    first-person arms model (fp_file - fitted to EFT's arm, knuckle and fingertip joints on its
    own) when there is one, else from the arms of the converted Upper part; "THIRD": always
    from Upper.  Returns the Hands object or None; never raises."""
    log = log or Log()
    try:
        from . import cod2eft_tools as TL
    except ImportError:
        import cod2eft_tools as TL
    ups = [o for o in parts if str(o.get("cod2eft_part", "")).startswith("Upper")]
    base = None
    if ups:
        part = str(ups[0]["cod2eft_part"])
        base = ups[0].name[:-(len(part) + 1)] if ups[0].name.endswith("_" + part) else \
            ups[0].name.rsplit("_", 1)[0]
    elif parts:
        part = str(parts[0].get("cod2eft_part", ""))
        base = parts[0].name[:-(len(part) + 1)] if part and \
            parts[0].name.endswith("_" + part) else parts[0].name
    if source == "AUTO" and fp_file and os.path.isfile(fp_file) and base:
        log(f"First-person hands: fitting the COD first-person arms "
            f"{os.path.basename(fp_file)} to EFT's arms")
        # mark what exists now (object references can't be compared safely after deletes)
        pre_names = set()
        for o in bpy.data.objects:
            try:
                o["_c2e_pre"] = True
            except (TypeError, AttributeError):            # linked from a library
                pre_names.add(o.name)

        def is_new(o):
            return "_c2e_pre" not in o and o.name not in pre_names
        colls_before = {c.name for c in bpy.data.collections}
        last_fit = bpy.context.scene.get("cod2eft_last_fit")
        sub = Log(quiet=True)
        made = []
        try:
            import_model(fp_file, sub)
            arms = [o for o in bpy.data.objects if is_new(o) and is_cod_armature(o)]
            if not arms:
                raise RuntimeError("no COD skeleton in the file")
            rig, _, _ = run_fit(arms, eft, match_lengths=match_lengths, fit_scale=fit_scale,
                                apply_tweaks=False, log=sub, basename=base + "_FPsrc",
                                head_height="off", head_forward=0.0,
                                match_fingertips=match_fingertips, viewmodel=True)
            made, _ = run_convert(rig, eft, max_influences=max_influences, join_parts=True,
                                  log=sub)
            h = TL.build_fp_hands(eft, made, base, sub)
            for ln in sub.lines:
                if ln.startswith(("Alignment", "Knuckles", "Fingertips after", "WARNING",
                                  "First-person hands")):
                    log("  " + ln)
            if h is None:
                raise RuntimeError("no arm faces in it")
            return h
        except Exception as ex:
            log(f"  WARNING: the first-person arms could not be used ({ex}) - taking the arms "
                "of Upper instead")
        finally:
            # drop the first-person model's own converted parts and anything left of the import
            for o in [o for o in bpy.data.objects if is_new(o)]:
                if o.get("cod2eft_part") == "Hands":
                    continue
                me = o.data if o.type == "MESH" else None
                bpy.data.objects.remove(o, do_unlink=True)
                if me is not None and me.users == 0:
                    bpy.data.meshes.remove(me)
            for o in bpy.data.objects:
                if "_c2e_pre" in o:
                    del o["_c2e_pre"]
            if last_fit is not None:
                bpy.context.scene["cod2eft_last_fit"] = last_fit
            for c in [c for c in bpy.data.collections if c.name not in colls_before or
                      c.name in ("COD2EFT_Head", "COD2EFT_Lower")]:
                if not c.objects and not c.children and c.name != "COD2EFT_Hands":
                    bpy.data.collections.remove(c)
    elif source == "AUTO" and base:
        log("First-person hands: no COD first-person arms model found next to this character - "
            "taking the arms of Upper")
    try:
        return TL.build_fp_hands(eft, ups, base, log) if ups else \
            (log("First-person hands: no Upper part found") or None)
    except Exception as ex:
        log(f"WARNING: first-person hands failed: {ex}")
        return None


def export_fbx(eft, objs, path, log):
    ensure_object_mode()
    try:
        from . import cod2eft_tools as TL
    except ImportError:
        import cod2eft_tools as TL
    if TL.adjust_rig() is not None:
        n = TL.adjust_apply()
        log(f"Hand adjustments in progress were applied to {n} mesh(es) before export")
    if TL.test_pose_active(eft):
        TL.clear_test_pose(eft)
        log("Test pose cleared before export (the FBX is written in the rest pose)")
    n = TL.clear_outside_colors(objs)
    if n:
        log(f"Removed the gear-clipping colours from {n} mesh(es) before export")
    for o in bpy.context.scene.objects:
        o.select_set(False)
    eft.hide_set(False)
    eft.select_set(True)
    for o in objs:
        o.hide_set(False)
        o.select_set(True)
    bpy.context.view_layer.objects.active = eft
    # Blender's defaults, written out: bpy.ops re-uses the LAST values of an operator's properties
    # in a session, so an FBX exported by hand with other settings would otherwise leak into this
    # one.  They match how 'EFT BASIC [Template].fbx' was written and the Park 24_1 FBX that worked
    # in game (2.4.4): UnitScaleFactor 100 (cm), -Z forward / Y up, all scales 1, leaf bones on,
    # every armature bone written as a cluster.
    try:
        from . import cod2eft_textures as TX
    except ImportError:
        import cod2eft_textures as TX
    restore = TX.fbx_colour_links(objs)
    try:
        bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"},
                                 global_scale=1.0, apply_unit_scale=True,
                                 apply_scale_options="FBX_SCALE_NONE", axis_forward="-Z", axis_up="Y",
                                 use_space_transform=True, bake_space_transform=False,
                                 use_mesh_modifiers=True, mesh_smooth_type="OFF", use_triangles=False,
                                 use_tspace=False, use_custom_props=False, colors_type="SRGB",
                                 add_leaf_bones=True, primary_bone_axis="Y", secondary_bone_axis="X",
                                 use_armature_deform_only=False, armature_nodetype="NULL",
                                 bake_anim=True, path_mode="AUTO", embed_textures=False)
    finally:
        restore()
    log(f"Exported {path}")


# ---------------------------------------------------------------------------------------------
# tweaks
# ---------------------------------------------------------------------------------------------
def save_tweaks(rig, log):
    auto = json.loads(rig.get("cod2eft_auto_basis", "{}"))
    out = {}
    for pb in rig.pose.bones:
        if pb.name not in auto:
            continue
        qa = Matrix(auto[pb.name]).to_quaternion()
        qc = pb.matrix_basis.to_quaternion()
        dq = qa.inverted() @ qc
        if dq.angle > math.radians(0.05):
            out[pb.name] = list(dq)
    with open(tweaks_file(), "w", encoding="utf-8") as fh:
        json.dump({"_note": "rotation tweaks on top of the automatic COD2EFT fit "
                            "(bone-local quaternions, w x y z)", "bones": out}, fh, indent=1)
    log(f"Saved {len(out)} tweaked bone(s) to {tweaks_file()}")
    return len(out)
