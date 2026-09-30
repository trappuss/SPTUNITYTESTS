"""
COD2EFT - Blender UI (N-panel "COD2EFT", add-on preferences, operators).

Everything the .bat tools do is available here:
  * Add EFT Template      - append your EFT BASIC template (armature + reference meshes)
  * Import COD Model      - .fbx / .cast (bundled Cast importer if you don't have one), picks the
                            right files of a character (body + head, Cold War parts) and skips
                            first-person / alternative-head files
  * Import + Convert      - the above, then Fit + Convert in one go
  * Batch Convert         - files or a whole folder tree; runs in a background Blender so your
                            open scene is untouched; writes <name>_EFT.blend / .fbx / report
  * Fit / tweak / Convert / Export - the step-by-step tools

Panel layout (sub-panels, in the order of the everyday flow): status at the top, then
1. Convert a Character, 2. Export for Unity, and collapsed: Extra Parts, Batch Convert,
Settings (Fit / Parts & Weights / Textures / Texture Options), Check the Fit, Adjust by Hand,
Step by Step.
"""
import os
import sys
import json
import queue
import threading
import subprocess

import bpy
from bpy.props import (FloatProperty, EnumProperty, StringProperty, BoolProperty, IntProperty, PointerProperty,
                       CollectionProperty)
from bpy_extras.io_utils import ImportHelper

from . import cod2eft_porter as C
from . import cod2eft_tools as TL
from . import cod2eft_textures as TX
from . import cod2eft_files as F

PKG = __package__
BATCH_SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cod2eft_batch.py")


def prefs(context=None):
    context = context or bpy.context
    try:
        return context.preferences.addons[PKG].preferences
    except KeyError:
        return None


def template_path(context=None):
    p = prefs(context)
    return bpy.path.abspath(p.template_path) if p and p.template_path else ""


# ---------------------------------------------------------------------------------------------
# preferences + scene settings
# ---------------------------------------------------------------------------------------------
class COD2EFT_Prefs(bpy.types.AddonPreferences):
    bl_idname = PKG

    template_path: StringProperty(
        name="EFT template .blend", subtype="FILE_PATH",
        description="Your EFT BASIC [Template].blend (EFT armature + reference meshes). Used by "
                    "'Add EFT Template' and by Batch Convert")
    data_dir: StringProperty(
        name="Settings folder", subtype="DIR_PATH",
        description="Where cod2eft_bonemap.json and cod2eft_pose_tweaks.json live "
                    "(empty = Blender's config folder)")

    def draw(self, context):
        L = self.layout
        L.prop(self, "template_path")
        L.prop(self, "data_dir")
        L.label(text=f"Settings folder in use: {C.data_dir()}", icon="FILE_FOLDER")
        home = live_home()
        L.label(text=f"Running COD2EFT v{C.VERSION_STR} - " +
                (f"code loaded live from {home}" if home else "installed copy"), icon="INFO")


def _poll_eft(self, obj):
    return obj.type == "ARMATURE" and C.E("Pelvis") in obj.data.bones


CLASS_ITEMS = [("AUTO", "Auto", "Use the class the classifier found")] + \
    [(c.upper(), TX.CLASS_LABEL[c].capitalize(), f"Treat this COD material as {TX.CLASS_LABEL[c]}")
     for c in TX.CLASSES if c != "cutout"]


class COD2EFT_MatClass(bpy.types.PropertyGroup):
    """One COD material of the last enc=3 texture run: its class, and an override (saved in the
    .blend; Batch passes the overrides as a JSON file)."""
    auto: StringProperty(name="Found", default="")
    why: StringProperty(name="Why", default="")
    cls: EnumProperty(name="Class", items=CLASS_ITEMS, default="AUTO",
                      description="Material class for enc=3 (sets its gloss curve). Auto = what "
                                  "the classifier found; press Convert Textures to apply")


class COD2EFT_Settings(bpy.types.PropertyGroup):
    preset: EnumProperty(name="Preset", items=lambda self, context: _preset_items(self, context),
                         description="Saved settings presets (settings folder, cod2eft_presets.json)")
    eft_armature: PointerProperty(
        name="EFT Armature", type=bpy.types.Object, poll=_poll_eft,
        description="The EFT armature to fit onto (empty = the one found in the scene)")
    output_name: StringProperty(
        name="Name", default="",
        description="Base name for the converted objects (empty = from the COD file)")
    match_body: BoolProperty(
        name="Match body volume", default=True,
        description="Place the COD body where EFT's body is, not just joint on joint: COD and EFT "
                    "put their joints in different places inside the body (EFT's spine sits near "
                    "the back, its knee joint near the front), so the chest, knees and hips are "
                    "lined up by their cross-sections, the soles are put flat on EFT's floor, "
                    "and the neck leans so the face lines up front-to-back with EFT's face. "
                    "Off = old joint-on-joint fit")
    match_height: BoolProperty(name="Match height (old)", default=False)   # replaced by head_height
    head_height: EnumProperty(
        name="Head height", default="LIMITED",
        items=[("OFF", "Keep COD height", "The character keeps its own height: a shorter "
                "character's head stays lower than EFT's head bone, so EFT hats / helmets float "
                "above it"),
               ("LIMITED", "Limited (+-6%)", "Spine + neck stretched or shortened by one factor, "
                "at most 6 %, so the eyes reach EFT's eye height (hats, glasses). Covers most "
                "characters fully; the rest of the gap is in the report"),
               ("FULL", "Full (0.75-1.35)", "Spine + neck stretched as far as needed (up to "
                "x0.75 - x1.35)")],
        description="Bring the head to EFT's height (EFT's hats, helmets and glasses are placed "
                    "for EFT's head)")
    face_landmark: EnumProperty(
        name="Line up by", default="EYES",
        items=[("EYES", "Eyes", "The COD eyeball centres are brought to EFT's (best for hats, "
                "glasses, face covers). Falls back to the nose when the head has no eye bones"),
               ("NOSE", "Nose tip", "The nose tip is brought to EFT's (older behaviour)")],
        description="Which part of the face is lined up with EFT's head")
    fp_hands: BoolProperty(
        name="First-person hands", default=False,
        description="Also make '<name>_Hands' for EFT's first-person hands (the hands bundle a "
                    "top needs): the arms of Upper from the shoulder to the fingertips, weighted "
                    "only to the 40 bones EFT's own first-person hands use. Hidden in the "
                    "viewport (it overlaps Upper) and written to the FBX with the rest")
    fp_source: EnumProperty(
        name="Hands from", default="AUTO",
        items=[("AUTO", "COD first-person arms", "The character's own COD first-person arms "
                "model (vm_arms / viewarms / vm_... in the same folder), fitted to EFT's arms, "
                "knuckles and fingertips - made for first person, more detail. Without one, "
                "the arms of Upper"),
               ("THIRD", "Third-person arms", "Always the arms of the converted Upper part")],
        description="What First-person hands are made from")
    match_fingertips: BoolProperty(
        name="Match fingers", default=True,
        description="Move the finger roots onto EFT's knuckles (at most 3 cm) and aim the last "
                    "finger segments (lengthened / shortened at most x0.8 - x1.25) so the "
                    "fingertips land on EFT's - measured from EFT's own hands - so the grip on "
                    "weapons matches. Off = only the knuckle-to-middle-joint lengths are matched")
    neck_lean: FloatProperty(
        name="Neck lean limit", default=35.0, min=10.0, max=70.0, subtype="NONE",
        description="Most the neck may lean forward (degrees from vertical) to bring the face in "
                    "line with EFT's. EFT's own neck leans 19.5 degrees")
    head_forward: FloatProperty(
        name="Head forward", default=0.5, min=0.0, max=1.0,
        description="Share of the face gap left over after the neck lean limit that the upper "
                    "spine takes up by leaning forward. 0 = off, 1 = close the gap front/back")
    match_neck: BoolProperty(
        name="Match neck", default=True,
        description="Place the neck so its cross-section lines up with EFT's neck (hair left "
                    "out). Off = only the neck joint is placed, as before 2.6.7 - the neck can "
                    "then sit 1 - 3 cm behind EFT's and the head look pushed forward")
    match_lengths: BoolProperty(
        name="Snap limb joints to EFT", default=True,
        description="Aim and stretch COD clavicle/arm/leg/finger/neck segments so shoulders, "
                    "elbows, wrists, knees, ankles, knuckles and the head land exactly on their "
                    "targets (EFT's joints, or where 'Match body volume' puts them). "
                    "Off = aim only, keep COD segment lengths")
    fit_scale: BoolProperty(
        name="Best-fit scale", default=False,
        description="Scale the COD model to best match the EFT torso. Off = keep real size "
                    "(only unit differences like cm/m are removed)")
    apply_tweaks: BoolProperty(
        name="Apply saved pose tweaks", default=True,
        description="Re-apply the rotations stored with 'Save Pose Tweaks' on every Fit and "
                    "Batch Convert")
    max_influences: IntProperty(name="Max bones / vertex", default=4, min=0, max=8,
                                description="0 = unlimited. EFT meshes use 4")
    join_parts: BoolProperty(
        name="Join into Head/Upper/Lower", default=True,
        description="Join the converted meshes into one object per part (<name>_Head / _Upper "
                    "/ _Lower), the object names the Unity tools expect")
    split_materials: BoolProperty(
        name="Separate by COD material", default=False,
        description="After converting, split every part into one object per original COD "
                    "material (vests, hats, pouches ... become separate objects). They share the "
                    "part's texture atlas; join the pieces you want together and press "
                    "Convert Textures to give them their own texture set")
    prefer_cast: BoolProperty(
        name="Prefer .cast", default=False,
        description="When an export has both .fbx and .cast, use the .cast")
    export_fbx: BoolProperty(name="Also write .fbx", default=True,
                             description="Batch: write <name>_EFT.fbx next to the .blend")
    output_dir: StringProperty(
        name="Output", subtype="DIR_PATH", default="",
        description="Batch output folder (empty = 'EFT_Converted' next to what you picked)")
    # show_steps / show_settings / show_tex_options: the panel's old fold-outs, now sub-panels
    # (2.6.3); kept so saved .blend files and scripts that set them still load
    show_steps: BoolProperty(name="Step by step", default=False)
    show_settings: BoolProperty(name="Settings", default=False)
    convert_textures: BoolProperty(
        name="Convert textures", default=True,
        description="After converting, turn each part's COD textures into one EFT texture set "
                    "(colour + specular, normal map, gloss) in an atlas, written as PNG files "
                    "(see the README for the Unity material settings)")
    texture_size: EnumProperty(
        name="Texture size", default="2048",
        items=[("1024", "1024", ""), ("2048", "2048", ""), ("4096", "4096", "")],
        description="Atlas size per part (EFT's own clothes use 1024 - 2048)")
    show_tex_options: BoolProperty(name="Texture options", default=False)
    tex_normals: EnumProperty(
        name="Normal maps", default="OPENGL",
        items=[("OPENGL", "OpenGL",
                "What EFT's own normal maps and its character shader use (checked on the "
                "template's textures) - Unity's 'Normal map' import expects this"),
               ("DIRECTX", "DirectX",
                "Green channel flipped (green down)")],
        description="Normal map convention of the written _n.png")
    tex_spec: FloatProperty(
        name="Specular strength", default=TX.SPEC_SCALE, min=0.0, max=4.0, step=10,
        description="Multiplier on the COD specular reflectance written to the colour map's "
                    "alpha (EFT's _MainTex.a). 1 = COD's own value; raise it for shinier, lower "
                    "it for duller")
    tex_metal: FloatProperty(
        name="Metal colour kept", default=TX.METAL_KEEP, min=0.0, max=1.0, subtype="FACTOR",
        description="How much of a metal part's colour stays in the colour map. COD's metal "
                    "colour is its specular colour; EFT's own gear keeps metal fairly bright "
                    "(0.7 matches EFT's textures on average). 0 = black metal")
    tex_ao: FloatProperty(
        name="AO strength", default=1.0, min=0.0, max=1.0, subtype="FACTOR",
        description="How much of COD's ambient occlusion is multiplied into the colour (EFT's "
                    "character shader has no AO slot, so this is the only way to keep it). "
                    "0 = none")
    tex_ao_spec: BoolProperty(
        name="AO into specular too", default=True,
        description="Also multiply the occlusion into the specular, so creases and gaps don't "
                    "shine")
    tex_colour_gain: FloatProperty(
        name="Colour brightness", default=1.0, min=0.25, max=2.0, step=5,
        description="Multiplier on the colour map. 1 = COD's own colours. Measured on the test "
                    "characters: COD cloth atlases have median brightness 0.12 - 0.27, vanilla "
                    "EFT cloth 0.13 - 0.35 - no systematic gap, so the default is 1")
    tex_colour_sat: FloatProperty(
        name="Colour saturation", default=1.0, min=0.0, max=2.0, step=5,
        description="0 = grey, 1 = COD's own colours, above 1 = more saturated")
    tex_gloss_match: FloatProperty(
        name="Gloss match", default=1.0, min=0.0, max=1.0, subtype="FACTOR",
        description="enc=3 only: how far each material's gloss moves onto vanilla EFT's range "
                    "for its class. 1 = the full fitted curve, 0 = COD's own gloss")
    tex_material_mode: EnumProperty(
        name="Materials", default="ENC2",
        items=[("ENC2", "enc=2 (per-part values in Unity)",
                "COD's specular and gloss as they are; the Unity tools calibrate one set of "
                "values per part (the behaviour before 2.6.0)"),
               ("ENC3", "enc=3 (baked, per COD material)",
                "Each COD material is classified (cloth, skin, leather/rubber/plastic, metal, "
                "glass, hair) and its gloss remapped onto vanilla EFT's range for its class; "
                "Unity uses EFT's neutral clothing values. Needs EFT Tools 1.7.0+")],
        description="How the material look is written into the textures (the PNG tag tells "
                    "Unity which)")
    mat_classes: CollectionProperty(type=COD2EFT_MatClass)
    show_classes: BoolProperty(name="Material classes", default=False,
                               description="Show the class found for each COD material")
    tex_layout: EnumProperty(
        name="Texture layout", default="ISLANDS",
        items=[("ISLANDS", "Used parts only (sharper)",
                "Only the areas of each COD texture that this part's UVs use go into the atlas, "
                "sized for an even texel density on the model"),
               ("WHOLE", "Whole textures",
                "Each COD texture goes into the atlas whole (simpler layout, lower detail)")],
        description="How the COD textures are laid out in the EFT atlas")
    test_pose: EnumProperty(
        name="Test pose",
        items=[(k, v[0], "Pose the EFT armature to check how the converted mesh bends")
               for k, v in TL.TEST_POSES.items()])


# ---------------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------------
def _cod_arms_from_context(context):
    sel = [o for o in context.selected_objects if C.is_cod_armature(o)]
    for o in context.selected_objects:
        if o.type == "MESH":
            for m in o.modifiers:
                if m.type == "ARMATURE" and m.object and C.is_cod_armature(m.object):
                    sel.append(m.object)
    if not sel:
        sel = [o for o in context.scene.objects if C.is_cod_armature(o)]
    out = []
    for a in sel:
        if a not in out:
            out.append(a)
    return out


def _append_template(context, report):
    tp = template_path(context)
    if not tp or not os.path.isfile(tp):
        report({"ERROR"}, "Set the EFT template .blend first (panel > Setup, or add-on "
                          "preferences)")
        return None
    with bpy.data.libraries.load(tp, link=False) as (src, dst):
        dst.objects = list(src.objects)
    coll = bpy.data.collections.new("EFT_Template")
    context.scene.collection.children.link(coll)
    for o in dst.objects:
        if o is not None:
            coll.objects.link(o)
    eft = C.find_eft_armature()
    if eft is None:
        report({"ERROR"}, "The template has no EFT armature (bone 'Base HumanPelvis')")
    return eft


def _picked_paths(op):
    """Files chosen in the file browser, or - if none - the folder itself."""
    d = bpy.path.abspath(op.directory) if op.directory else os.path.dirname(op.filepath)
    names = [f.name for f in op.files if f.name]
    if names:
        return [os.path.join(d, n) for n in names]
    if op.filepath and os.path.isfile(bpy.path.abspath(op.filepath)):
        return [bpy.path.abspath(op.filepath)]
    return [d] if d else []


def _import_character(context, paths, log):
    """Import ONE character into the current scene.  Picking any file of a character (e.g. only
    the body) pulls in the rest of it (head, Cold War parts) from its folder; picking a folder
    takes the single character in it."""
    prefer_cast = context.scene.cod2eft.prefer_cast
    picked_files = [os.path.abspath(p) for p in paths if os.path.isfile(p)]
    if picked_files:
        roots = sorted({F.char_dir_of(f) for f in picked_files})
        files = F.discover(roots, prefer_cast=prefer_cast, log=log)
        chars, skipped = F.group(files, log=log)
        keys = {F.key_of(F.stem_of(f)) for f in picked_files}
        stems = {os.path.splitext(f)[0].lower() for f in picked_files}

        def picked(f):
            return os.path.splitext(os.path.abspath(f))[0].lower() in stems
        chars = [c for c in chars if c["key"] in keys or
                 any(picked(f) for f in c["bodies"] + ([c["head"]] if c["head"] else []))]
        # an explicitly picked alternative head (e.g. _high) wins over the default one
        for c in chars:
            alt = [f for f in picked_files if F.kind_of(F.stem_of(f)) == "head"
                   and F.key_of(F.stem_of(f)) == c["key"]]
            if alt:
                c["head"] = alt[0]
        skipped = [(f, w) for f, w in skipped if not picked(f)]
    else:
        files = F.discover(paths, prefer_cast=prefer_cast, log=log)
        chars, skipped = F.group(files, log=log)
    for f, why in skipped:
        log(f"skip {os.path.basename(f)}: {why}")
    if not chars:
        raise RuntimeError("No COD character model found in the selection")
    if len(chars) > 1:
        raise RuntimeError(f"The selection holds {len(chars)} characters "
                           f"({', '.join(c['key'] for c in chars[:4])}...) - use Batch Convert, "
                           "or pick one character's files")
    ch = chars[0]
    for f in ch["bodies"] + ([ch["head"]] if ch["head"] else []):
        log(f"Importing {os.path.basename(f)}")
        C.import_model(f, log)
    # remembered for First-person hands (made from it at Convert, if that is on)
    context.scene["cod2eft_fp_file"] = ch.get("fp") or ""
    if ch.get("fp"):
        log(f"First-person arms found: {os.path.basename(ch['fp'])} (used for First-person "
            "hands)")
    # importers leave the LAST file selected; Fit uses the selection when there is one, so
    # clear it - Fit then takes every COD skeleton of this character
    for o in context.selected_objects:
        o.select_set(False)
    return ch["key"]


# ---------------------------------------------------------------------------------------------
# operators - setup / import
# ---------------------------------------------------------------------------------------------
class COD2EFT_OT_append_template(bpy.types.Operator):
    bl_idname = "cod2eft.append_template"
    bl_label = "Add EFT Template"
    bl_description = "Append the EFT armature and reference meshes from your template .blend"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        if C.find_eft_armature():
            self.report({"INFO"}, "Scene already has an EFT armature")
            return {"FINISHED"}
        return {"FINISHED"} if _append_template(context, self.report) else {"CANCELLED"}


class _PickFiles(ImportHelper):
    filter_glob: StringProperty(default="*.fbx;*.cast", options={"HIDDEN"})
    files: CollectionProperty(type=bpy.types.OperatorFileListElement,
                              options={"HIDDEN", "SKIP_SAVE"})
    directory: StringProperty(subtype="DIR_PATH", options={"HIDDEN", "SKIP_SAVE"})


class COD2EFT_OT_import(bpy.types.Operator, _PickFiles):
    bl_idname = "cod2eft.import_cod"
    bl_label = "Import COD Model"
    bl_description = ("Import one COD character (.fbx or .cast). Select its files - or select "
                      "nothing and press the button to take everything in the current folder")
    bl_options = {"REGISTER", "UNDO"}
    convert: BoolProperty(name="Fit + Convert after import", default=False)

    def execute(self, context):
        st = context.scene.cod2eft
        log = C.Log()
        if not C.find_eft_armature():
            if not _append_template(context, self.report):
                return {"CANCELLED"}
            log("Added the EFT template to the scene")
        C.ensure_object_mode()
        try:
            key = _import_character(context, _picked_paths(self), log)
        except Exception as ex:
            log(f"ERROR: {ex}")
            log.to_text()
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}
        if not st.output_name.strip():
            st.output_name = key
        log.to_text()
        if self.convert:
            return bpy.ops.cod2eft.oneclick()
        self.report({"INFO"}, f"Imported '{key}' - now press 1. Fit (or Fit + Convert)")
        return {"FINISHED"}


# ---------------------------------------------------------------------------------------------
# operators - batch (background Blender, non-blocking)
# ---------------------------------------------------------------------------------------------
_BATCH = {"proc": None, "lines": None, "status": "", "out": "", "summary": []}


def _batch_cmd(st, tp, data_dir, paths):
    """Command line for the background Blender batch run + its output folder."""
    out = bpy.path.abspath(st.output_dir) if st.output_dir else ""
    if not out:
        base = paths[0] if os.path.isdir(paths[0]) else os.path.dirname(paths[0])
        out = os.path.join(base, "EFT_Converted")
    cmd = [bpy.app.binary_path, "--background", "--factory-startup",
           "--python", BATCH_SCRIPT, "--",
           "--template", tp, "--out", out, "--data-dir", data_dir,
           "--max-influences", str(st.max_influences)]
    if st.prefer_cast:
        cmd.append("--cast")
    if not st.match_body:
        cmd.append("--joints-only")
    cmd += ["--head-height", st.head_height.lower(), "--face-landmark", st.face_landmark.lower()]
    if not st.match_fingertips:
        cmd.append("--no-fingertips")
    cmd += ["--neck-lean", f"{st.neck_lean:g}", "--head-forward", f"{st.head_forward:g}"]
    if not st.match_neck:
        cmd.append("--no-neck-fit")
    if st.convert_textures:
        cmd += ["--texture-size", st.texture_size, "--normal-style", st.tex_normals.lower(),
                "--spec-strength", f"{st.tex_spec:g}", "--metal-colour", f"{st.tex_metal:g}",
                "--ao-strength", f"{st.tex_ao:g}",
                "--texture-layout", st.tex_layout.lower()]
        if not st.tex_ao_spec:
            cmd.append("--no-ao-spec")
        for flag, val in (("--colour-brightness", st.tex_colour_gain),
                          ("--colour-saturation", st.tex_colour_sat)):
            if abs(val - 1.0) > 1e-6:
                cmd += [flag, f"{val:g}"]
        if st.tex_material_mode == "ENC3":
            cmd += ["--material-mode", "enc3"]
            if abs(st.tex_gloss_match - 1.0) > 1e-6:
                cmd += ["--gloss-match", f"{st.tex_gloss_match:g}"]
            ov = _class_overrides(st)
            if ov:
                fn = os.path.join(data_dir, "cod2eft_class_overrides.json")
                try:
                    with open(fn, "w", encoding="utf-8") as fh:
                        json.dump(ov, fh, indent=1)
                    cmd += ["--class-overrides", fn]
                except OSError:
                    pass
    else:
        cmd.append("--no-textures")
    if not st.match_lengths:
        cmd.append("--no-lengths")
    if not st.apply_tweaks:
        cmd.append("--no-tweaks")
    if st.fit_scale:
        cmd.append("--fit-scale")
    if not st.join_parts:
        cmd.append("--no-join")
    if st.split_materials:
        cmd.append("--split-materials")
    if st.fp_hands:
        cmd.append("--fp-hands")
        if st.fp_source == "THIRD":
            cmd += ["--fp-hands-from", "third"]
    if st.export_fbx:
        cmd.append("--export-fbx")
    return cmd + list(paths), out


def _drain():
    """Move finished output lines from the reader thread into the status; True when done."""
    q = _BATCH["lines"]
    while True:
        try:
            line = q.get_nowait()
        except queue.Empty:
            return False
        if line is None:
            return True
        print(line)
        if line.startswith("[COD2EFT] PROGRESS"):
            _BATCH["status"] = line.replace("[COD2EFT] PROGRESS", "").strip()
        elif line.startswith("[COD2EFT] OK") or line.startswith("[COD2EFT] FAILED"):
            _BATCH["summary"].append(line.replace("[COD2EFT] ", ""))


def _start_batch(cmd, out):
    flags = 0x08000000 if sys.platform == "win32" else 0      # CREATE_NO_WINDOW
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", creationflags=flags)
    q = queue.Queue()

    def reader():
        for line in proc.stdout:
            q.put(line.rstrip("\n"))
        q.put(None)
    threading.Thread(target=reader, daemon=True).start()
    _BATCH.update(proc=proc, lines=q, status="starting...", out=out, summary=[])


class COD2EFT_OT_batch(bpy.types.Operator, _PickFiles):
    bl_idname = "cod2eft.batch"
    bl_label = "Batch Convert"
    bl_description = ("Convert files or a whole folder tree (every character found) in a "
                      "background Blender. Select files, or select nothing to use the folder")

    _timer = None

    def execute(self, context):
        if _BATCH["proc"] is not None:
            self.report({"ERROR"}, "A batch is already running")
            return {"CANCELLED"}
        st = context.scene.cod2eft
        tp = template_path(context)
        if not tp or not os.path.isfile(tp):
            self.report({"ERROR"}, "Set the EFT template .blend first (Setup)")
            return {"CANCELLED"}
        paths = _picked_paths(self)
        if not paths:
            self.report({"ERROR"}, "Nothing selected")
            return {"CANCELLED"}
        cmd, out = _batch_cmd(st, tp, C.data_dir(), paths)
        _start_batch(cmd, out)
        print("[COD2EFT] batch:", " ".join(f'"{c}"' if " " in c else c for c in cmd))
        wm = context.window_manager
        self._timer = wm.event_timer_add(0.5, window=context.window)
        wm.modal_handler_add(self)
        return {"RUNNING_MODAL"}

    def modal(self, context, event):
        if event.type == "ESC":
            _BATCH["proc"].terminate()
            _BATCH["status"] = "cancelled"
        if event.type != "TIMER":
            return {"PASS_THROUGH"}
        done = _drain()
        for win in context.window_manager.windows:
            for area in win.screen.areas:
                if area.type == "VIEW_3D":
                    area.tag_redraw()
        if done:
            rc = _BATCH["proc"].wait()
            context.window_manager.event_timer_remove(self._timer)
            ok = sum(1 for s in _BATCH["summary"] if s.startswith("OK"))
            bad = sum(1 for s in _BATCH["summary"] if s.startswith("FAILED"))
            _BATCH["status"] = (f"done: {ok} converted, {bad} failed" if rc in (0, 1) or ok
                                else f"stopped (exit code {rc}) - see System Console")
            _BATCH["proc"] = None
            self.report({"WARNING"} if bad or rc not in (0, 1) else {"INFO"},
                        f"COD2EFT batch {_BATCH['status']} -> {_BATCH['out']}")
            return {"FINISHED"}
        return {"PASS_THROUGH"}


class COD2EFT_OT_open_folder(bpy.types.Operator):
    bl_idname = "cod2eft.open_folder"
    bl_label = "Open Folder"
    bl_description = "Open the folder in the file browser"
    what: StringProperty(default="out")

    def execute(self, context):
        d = _BATCH["out"] if self.what == "out" else C.data_dir()
        if d and os.path.isdir(d):
            bpy.ops.wm.path_open(filepath=d)
            return {"FINISHED"}
        self.report({"ERROR"}, f"Folder not found: {d}")
        return {"CANCELLED"}


# ---------------------------------------------------------------------------------------------
# operators - step by step
# ---------------------------------------------------------------------------------------------
class COD2EFT_OT_fit(bpy.types.Operator):
    bl_idname = "cod2eft.fit"
    bl_label = "1. Fit COD to EFT"
    bl_description = "Align and auto-pose the COD model onto the EFT skeleton"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        st = context.scene.cod2eft
        eft = C.find_eft_armature(context)
        if not eft:
            self.report({"ERROR"}, "No EFT armature in the scene - press Add EFT Template")
            return {"CANCELLED"}
        log = C.Log()
        try:
            C.run_fit(_cod_arms_from_context(context), eft, st.match_lengths, st.fit_scale,
                      st.apply_tweaks, log, basename=st.output_name.strip(),
                      match_body=st.match_body, neck_max_lean=st.neck_lean,
                      head_forward=st.head_forward, head_height=st.head_height.lower(),
                      face_landmark=st.face_landmark.lower(),
                      match_fingertips=st.match_fingertips, match_neck=st.match_neck)
        except Exception as ex:
            log(f"ERROR: {ex}")
            log.to_text()
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}
        log.to_text()
        self.report({"INFO"}, "Fit done - see Text 'COD2EFT_Report'")
        return {"FINISHED"}


class COD2EFT_OT_convert(bpy.types.Operator):
    bl_idname = "cod2eft.convert"
    bl_label = "2. Convert to EFT"
    bl_description = "Bake the pose, convert weights to EFT bones, split Head/Upper/Lower and "\
                     "parent to the EFT armature"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        st = context.scene.cod2eft
        rig = bpy.data.objects.get(C.RIG_NAME)
        eft = C.find_eft_armature(context)
        if not rig or not eft:
            self.report({"ERROR"}, "Run Fit first (needs COD2EFT_FitRig and an EFT armature)")
            return {"CANCELLED"}
        log = C.Log()
        try:
            results, _ = C.run_convert(rig, eft, st.max_influences, st.join_parts, log)
        except Exception as ex:
            log(f"ERROR: {ex}")
            log.to_text()
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}
        msg = "Converted"
        if st.fp_hands:
            h = _make_fp_hands(context, eft, results, log)
            if h is not None:
                results.append(h)
        if st.convert_textures:
            try:
                out = _texture_dir(context, results)
                _convert_textures(st, results, out, _out_name(context, results), log)
                msg += f" - textures in {out}"
            except Exception as ex:
                log(f"WARNING: texture conversion failed: {ex}")
                msg += " (texture conversion failed - see the report)"
        if st.split_materials:
            results = TL.split_by_material(results, log)
            msg += f" - {len(results)} objects"
        log.to_text()
        self.report({"INFO"}, msg + " - see Text 'COD2EFT_Report'")
        return {"FINISHED"}


# settings that change what Import / Batch produce - the panel lists any that are not default
SETTINGS = (("prefer_cast", "Prefer .cast"), ("match_body", "Match body volume"),
            ("head_height", "Head height"), ("face_landmark", "Line up by"),
            ("neck_lean", "Neck lean limit"), ("match_fingertips", "Match fingers"),
            ("fp_hands", "First-person hands"), ("fp_source", "Hands from"),
            ("head_forward", "Head forward"), ("match_neck", "Match neck"),
            ("match_lengths", "Snap limb joints"),
            ("fit_scale", "Best-fit scale"), ("apply_tweaks", "Apply saved tweaks"),
            ("max_influences", "Max bones / vertex"), ("join_parts", "Join parts"),
            ("split_materials", "Separate by COD material"),
            ("convert_textures", "Convert textures"), ("texture_size", "Texture size"),
            ("tex_layout", "Texture layout"), ("tex_normals", "Normal maps"),
            ("tex_spec", "Specular strength"), ("tex_metal", "Metal colour kept"),
            ("tex_ao", "AO strength"), ("tex_material_mode", "Materials"),
            ("tex_ao_spec", "AO into specular"), ("tex_colour_gain", "Colour brightness"),
            ("tex_colour_sat", "Colour saturation"), ("tex_gloss_match", "Gloss match"))


def changed_settings(st):
    """[(label, value text)] of the SETTINGS that differ from their defaults."""
    out = []
    for name, label in SETTINGS:
        rna = st.bl_rna.properties[name]
        val = getattr(st, name)
        if rna.type == "FLOAT":
            if abs(val - rna.default) < 1e-6:
                continue
            out.append((label, f"{val:g}"))
        elif rna.type == "ENUM":
            if val == rna.default:
                continue
            out.append((label, rna.enum_items[val].name))
        else:
            if val == rna.default:
                continue
            out.append((label, ("on" if val else "off") if rna.type == "BOOLEAN" else str(val)))
    return out


_TWEAKS = {"key": None, "n": 0}


def saved_tweak_count():
    """Number of bones in cod2eft_pose_tweaks.json (0 if none), cached on the file's mtime."""
    f = C.tweaks_file()
    try:
        key = (f, os.path.getmtime(f))
    except OSError:
        return 0
    if _TWEAKS["key"] != key:
        try:
            with open(f, "r", encoding="utf-8") as fh:
                d = json.load(fh)
            n = len([k for k in d if not k.startswith("_")]) if isinstance(d, dict) else 0
            if isinstance(d, dict) and isinstance(d.get("bones"), dict):
                n = len(d["bones"])
        except (OSError, ValueError):
            n = 0
        _TWEAKS.update(key=key, n=n)
    return _TWEAKS["n"]


def live_home():
    """The COD2EFT folder the add-on code is loaded from (set up by Install_COD2EFT_Addon.bat),
    or None when running the installed copy."""
    return getattr(sys.modules.get(PKG), "LIVE_HOME", None)


_FOLDER_VER = {}


def folder_version(folder):
    """Version of the add-on files in the COD2EFT folder (re-read only when the file changes)."""
    if not folder:
        return None
    p = os.path.join(folder, "cod2eft_porter.py")
    try:
        mt = os.path.getmtime(p)
    except OSError:
        return None
    if _FOLDER_VER.get(p, (None,))[0] != mt:
        _FOLDER_VER[p] = (mt, C.file_version(folder))
    return _FOLDER_VER[p][1]


def _vstr(v):
    return ".".join(str(x) for x in v) if v else "?"


def _reload_addon():
    import addon_utils
    import importlib
    addon_utils.disable(PKG, default_set=False)
    for n in [m for m in sys.modules if m == PKG or m.startswith(PKG + ".")]:
        del sys.modules[n]
    importlib.invalidate_caches()
    addon_utils.enable(PKG, default_set=False)
    mod = sys.modules.get(PKG + ".cod2eft_porter")
    print(f"[COD2EFT] add-on reloaded from {live_home() or 'the installed copy'}: "
          f"v{getattr(mod, 'VERSION_STR', '?')}")
    return None


class COD2EFT_OT_reload(bpy.types.Operator):
    bl_idname = "cod2eft.reload"
    bl_label = "Reload COD2EFT"
    bl_description = ("Load the add-on files again from the COD2EFT folder - after an update - "
                      "without restarting Blender")

    def execute(self, context):
        if _BATCH["proc"] is not None:
            self.report({"ERROR"}, "Wait for the batch to finish first")
            return {"CANCELLED"}
        bpy.app.timers.register(_reload_addon, first_interval=0.05)
        return {"FINISHED"}


class COD2EFT_OT_split_materials(bpy.types.Operator):
    bl_idname = "cod2eft.split_materials"
    bl_label = "Separate by COD Material"
    bl_description = ("Split the selected converted parts (or all of them if none is selected) "
                      "into one object per original COD material - also after the textures were "
                      "converted to one atlas")
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        eft = C.find_eft_armature(context)
        objs = TL.converted_meshes(eft) if eft else []
        sel = [o for o in objs if o.select_get()]
        objs = sel or objs
        if not objs:
            self.report({"ERROR"}, "No converted parts found")
            return {"CANCELLED"}
        log = C.Log()
        n0 = len(objs)
        res = TL.split_by_material(objs, log)
        log.to_text()
        self.report({"INFO"}, f"{n0} part(s) -> {len(res)} object(s)")
        return {"FINISHED"}


def _make_fp_hands(context, eft, parts, log):
    st = context.scene.cod2eft
    return C.make_fp_hands(eft, parts, context.scene.get("cod2eft_fp_file") or None, log,
                           source=st.fp_source, max_influences=st.max_influences,
                           match_lengths=st.match_lengths, match_fingertips=st.match_fingertips,
                           fit_scale=st.fit_scale)


class COD2EFT_OT_fp_hands(bpy.types.Operator):
    bl_idname = "cod2eft.fp_hands"
    bl_label = "Make First-Person Hands"
    bl_description = ("Make (or remake) '<name>_Hands' for EFT's first-person hands from the arms "
                      "of the converted Upper part, weighted only to EFT's 40 hand bones. With "
                      "Convert textures on, it gets its own texture set")
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        st = context.scene.cod2eft
        eft = C.find_eft_armature(context)
        objs = TL.converted_meshes(eft) if eft else []
        objs = [o for o in objs if o.get("cod2eft_part") != "Hands"]
        if not objs:
            self.report({"ERROR"}, "Nothing converted yet")
            return {"CANCELLED"}
        log = C.Log()
        h = _make_fp_hands(context, eft, objs, log)
        if h is None:
            log.to_text()
            self.report({"WARNING"}, "No first-person hands made - see Text 'COD2EFT_Report'")
            return {"CANCELLED"}
        msg = f"Made {h.name} (hidden - it overlaps Upper)"
        if st.convert_textures:
            try:
                out = _texture_dir(context, [h])
                _convert_textures(st, [h], out, _out_name(context, objs), log)
                msg += f" - textures in {out}"
            except Exception as ex:
                log(f"WARNING: texture conversion failed: {ex}")
                msg += " (texture conversion failed - see the report)"
        log.to_text()
        self.report({"INFO"}, msg)
        return {"FINISHED"}


# ---------------------------------------------------------------------------------------------
# settings presets (2.6.5): named sets of the SETTINGS above, kept in the settings folder as
# cod2eft_presets.json (never touched by SYNC_TO_MY_PC.bat).  One of them can be the start-up
# preset: it is applied to scenes whose settings are all still at their defaults (a new file, the
# template), never over settings a .blend already has.
# ---------------------------------------------------------------------------------------------
def presets_file():
    return os.path.join(C.data_dir(), "cod2eft_presets.json")


def load_presets():
    try:
        with open(presets_file(), "r", encoding="utf-8") as fh:
            d = json.load(fh)
        if isinstance(d, dict) and isinstance(d.get("presets"), dict):
            d.setdefault("startup", "")
            return d
    except (OSError, ValueError):
        pass
    return {"presets": {}, "startup": ""}


def save_presets(d):
    with open(presets_file(), "w", encoding="utf-8") as fh:
        json.dump(d, fh, indent=1, sort_keys=True)


def settings_values(st):
    return {name: getattr(st, name) for name, _ in SETTINGS}


def apply_values(st, vals):
    """Sets the known settings from `vals`; returns the names it couldn't set (renamed / removed
    settings or enum values from an older version)."""
    bad = []
    for name, _ in SETTINGS:
        if name not in vals:
            continue
        try:
            setattr(st, name, vals[name])
        except (TypeError, ValueError, AttributeError):
            bad.append(name)
    return bad


_PRESET_ITEMS = []      # Blender needs the enum item strings kept alive by Python


def _preset_items(self, context):
    names = sorted(load_presets()["presets"])
    _PRESET_ITEMS[:] = [(n, n, "") for n in names] or [("", "(no presets saved)", "")]
    return _PRESET_ITEMS


class COD2EFT_OT_preset_save(bpy.types.Operator):
    bl_idname = "cod2eft.preset_save"
    bl_label = "Save Preset"
    bl_description = ("Save every Import / Batch setting under a name (settings folder, "
                      "cod2eft_presets.json). An existing preset of that name is replaced")
    name: StringProperty(name="Name", default="My settings")
    startup: BoolProperty(name="Use for new scenes", default=True,
                          description="Apply this preset automatically to new scenes / files whose "
                                      "settings are still at the defaults")

    def invoke(self, context, event):
        d = load_presets()
        if d["startup"]:
            self.name = d["startup"]
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        n = self.name.strip()
        if not n:
            self.report({"ERROR"}, "Give the preset a name")
            return {"CANCELLED"}
        d = load_presets()
        d["presets"][n] = settings_values(context.scene.cod2eft)
        if self.startup:
            d["startup"] = n
        elif d["startup"] == n:
            d["startup"] = ""
        save_presets(d)
        context.scene.cod2eft.preset = n
        self.report({"INFO"}, f"Saved preset '{n}'" + (" (used for new scenes)" if self.startup else ""))
        return {"FINISHED"}


class COD2EFT_OT_preset_load(bpy.types.Operator):
    bl_idname = "cod2eft.preset_load"
    bl_label = "Load"
    bl_description = "Set every Import / Batch setting from the chosen preset"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        st = context.scene.cod2eft
        vals = load_presets()["presets"].get(st.preset)
        if vals is None:
            self.report({"ERROR"}, "Choose a saved preset")
            return {"CANCELLED"}
        bad = apply_values(st, vals)
        self.report({"WARNING"} if bad else {"INFO"},
                    f"Loaded '{st.preset}'" + (f" - not applied (from an older version): {', '.join(bad)}" if bad else ""))
        return {"FINISHED"}


class COD2EFT_OT_preset_delete(bpy.types.Operator):
    bl_idname = "cod2eft.preset_delete"
    bl_label = "Delete Preset"
    bl_description = "Remove the chosen preset from cod2eft_presets.json"

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)

    def execute(self, context):
        d = load_presets()
        n = context.scene.cod2eft.preset
        if n not in d["presets"]:
            return {"CANCELLED"}
        del d["presets"][n]
        if d["startup"] == n:
            d["startup"] = ""
        save_presets(d)
        self.report({"INFO"}, f"Deleted preset '{n}'")
        return {"FINISHED"}


def apply_startup_preset(scene):
    """The start-up preset onto `scene` if its settings are all at their defaults. Returns its name."""
    st = getattr(scene, "cod2eft", None)
    if st is None or changed_settings(st):
        return ""
    d = load_presets()
    n = d.get("startup", "")
    if n and n in d["presets"]:
        apply_values(st, d["presets"][n])
        try:
            st.preset = n
        except TypeError:
            pass
        return n
    return ""


@bpy.app.handlers.persistent
def _on_load_post(*_args):
    for sc in bpy.data.scenes:
        apply_startup_preset(sc)


def _startup_timer():
    try:
        _on_load_post()
    except Exception:
        pass
    return None


class COD2EFT_OT_reset_settings(bpy.types.Operator):
    bl_idname = "cod2eft.reset_settings"
    bl_label = "Reset Settings"
    bl_description = "Put every Import / Batch setting back to its default"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        st = context.scene.cod2eft
        for name, _ in SETTINGS:
            st.property_unset(name)
        return {"FINISHED"}


def _tex_kwargs(st):
    return dict(size=int(st.texture_size), spec_scale=st.tex_spec, ao_strength=st.tex_ao,
                ao_in_spec=st.tex_ao_spec, normal_style=st.tex_normals, uv_layout=st.tex_layout,
                metal_keep=st.tex_metal, material_mode=st.tex_material_mode,
                colour_gain=st.tex_colour_gain, colour_sat=st.tex_colour_sat,
                gloss_match=st.tex_gloss_match,
                class_overrides=_class_overrides(st))


def _class_overrides(st):
    """{COD material name: class} of the class list's overrides."""
    return {it.name: it.cls.lower() for it in st.mat_classes if it.cls != "AUTO"}


def _convert_textures(st, objs, out, name, log):
    """TX.convert_textures with the panel's settings; refreshes the class list (overrides kept)."""
    res = TX.convert_textures(objs, out, name, log=log, **_tex_kwargs(st))
    rows = [c for r in res or [] for c in r.get("classes", []) if c["class"] != "cutout"]
    if rows:
        keep = {it.name: it.cls for it in st.mat_classes}
        seen = set()
        for c in rows:
            if c["material"] in seen:
                continue
            seen.add(c["material"])
            it = st.mat_classes.get(c["material"])
            if it is None:
                it = st.mat_classes.add()
                it.name = c["material"]
            it.auto, it.why = c["auto"], c["why"]
            it.cls = keep.get(c["material"], "AUTO")
    return res


def _draw_classes(layout, st):
    if st.tex_material_mode != "ENC3" or not len(st.mat_classes):
        return
    c = layout.column(align=True)
    c.prop(st, "show_classes", icon="TRIA_DOWN" if st.show_classes else "TRIA_RIGHT",
           emboss=False, text=f"Material classes ({len(st.mat_classes)})")
    if st.show_classes:
        for it in st.mat_classes:
            r = c.row(align=True)
            r.label(text=f"{it.name[:28]}: {it.auto}", icon="MATERIAL")
            r.prop(it, "cls", text="")
        c.label(text="Change a class, then Convert Textures", icon="INFO")


def _out_name(context, objs):
    n = context.scene.cod2eft.output_name.strip()
    if n:
        return n
    return objs[0].name.rsplit("_", 1)[0] if objs else "COD"


def _texture_dir(context, objs):
    """Where texture PNGs go: the Output folder if set, else next to the saved .blend, else an
    EFT_Converted folder next to the COD model."""
    st = context.scene.cod2eft
    if st.output_dir:
        return bpy.path.abspath(st.output_dir)
    if bpy.data.filepath:
        return os.path.join(os.path.dirname(bpy.data.filepath), "textures")
    for o in objs:
        src = json.loads(o.get("cod2eft_mat_src", "{}"))
        for f in src.values():
            return os.path.join(os.path.dirname(os.path.dirname(f)), "EFT_Converted")
    return os.path.join(os.path.expanduser("~"), "COD2EFT_textures")


class COD2EFT_OT_textures(bpy.types.Operator):
    bl_idname = "cod2eft.textures"
    bl_label = "Convert Textures"
    bl_description = ("(Re)make the EFT texture atlas of the converted parts - e.g. at another "
                      "size. Works while the original COD materials are still in this session")
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        st = context.scene.cod2eft
        eft = C.find_eft_armature(context)
        objs = TL.converted_meshes(eft) if eft else []
        if not objs:
            self.report({"ERROR"}, "Nothing converted yet")
            return {"CANCELLED"}
        log = C.Log()
        out = _texture_dir(context, objs)
        res = _convert_textures(st, objs, out, _out_name(context, objs), log)
        log.to_text()
        if not res:
            self.report({"WARNING"}, "No textures converted - see Text 'COD2EFT_Report'")
            return {"CANCELLED"}
        self.report({"INFO"}, f"Textures written to {out}")
        return {"FINISHED"}


class COD2EFT_OT_oneclick(bpy.types.Operator):
    bl_idname = "cod2eft.oneclick"
    bl_label = "Fit + Convert"
    bl_description = "Fit, then Convert"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        r = bpy.ops.cod2eft.fit()
        if "FINISHED" not in r:
            return {"CANCELLED"}
        return bpy.ops.cod2eft.convert()


class COD2EFT_OT_save_tweaks(bpy.types.Operator):
    bl_idname = "cod2eft.save_tweaks"
    bl_label = "Save Pose Tweaks"
    bl_description = ("Store your manual rotations on COD2EFT_FitRig (relative to the automatic "
                      "fit) so every future Fit - and Batch Convert - applies them")

    def execute(self, context):
        rig = bpy.data.objects.get(C.RIG_NAME)
        if not rig:
            self.report({"ERROR"}, "No COD2EFT_FitRig in the scene - run 1. Fit first")
            return {"CANCELLED"}
        n = C.save_tweaks(rig, C.Log())
        self.report({"INFO"}, f"Saved {n} tweaked bone(s) to {C.tweaks_file()}")
        return {"FINISHED"}


class COD2EFT_OT_clear_tweaks(bpy.types.Operator):
    bl_idname = "cod2eft.clear_tweaks"
    bl_label = "Delete Saved Tweaks"
    bl_description = "Delete cod2eft_pose_tweaks.json"

    def execute(self, context):
        if os.path.isfile(C.tweaks_file()):
            os.remove(C.tweaks_file())
        self.report({"INFO"}, "Saved tweaks removed")
        return {"FINISHED"}


class COD2EFT_OT_export(bpy.types.Operator):
    bl_idname = "cod2eft.export"
    bl_label = "Export FBX for Unity"
    bl_description = "Export the EFT armature + converted parts with your template's FBX settings"
    filepath: StringProperty(subtype="FILE_PATH")
    filter_glob: StringProperty(default="*.fbx", options={"HIDDEN"})

    def invoke(self, context, event):
        name = context.scene.cod2eft.output_name.strip() or "export"
        self.filepath = os.path.join(bpy.path.abspath("//") or os.path.expanduser("~"),
                                     f"{name}_EFT.fbx")
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        eft = C.find_eft_armature(context)
        objs = TL.converted_meshes(eft) if eft else []
        if not eft or not objs:
            self.report({"ERROR"}, "Nothing converted to export")
            return {"CANCELLED"}
        C.export_fbx(eft, objs, bpy.path.abspath(self.filepath), C.Log())
        self.report({"INFO"}, f"Exported {self.filepath}")
        return {"FINISHED"}


# ---------------------------------------------------------------------------------------------
# operators - checking
# ---------------------------------------------------------------------------------------------
class COD2EFT_OT_compare(bpy.types.Operator):
    bl_idname = "cod2eft.compare"
    bl_label = "Compare to EFT"
    bl_description = ("Colour the converted COD parts green and EFT's own meshes from the template "
                      "magenta (viewport Solid shading, object colours). Where magenta shows "
                      "through, EFT's body is outside the COD mesh. Press again to switch off")

    def execute(self, context):
        if TL.compare_is_on(context.scene):
            TL.compare_off(context.scene)
            self.report({"INFO"}, "Compare off")
            return {"FINISHED"}
        eft = C.find_eft_armature(context)
        if not eft:
            self.report({"ERROR"}, "No EFT armature in the scene")
            return {"CANCELLED"}
        n_cod, n_eft = TL.compare_on(context.scene, eft)
        if not n_eft:
            self.report({"WARNING"}, "No EFT reference meshes on the armature (template head / "
                                     "shirt / pants) - nothing to compare against")
        else:
            self.report({"INFO"}, f"{n_cod} COD part(s) green, {n_eft} EFT mesh(es) magenta")
        return {"FINISHED"}


class COD2EFT_OT_test_pose(bpy.types.Operator):
    bl_idname = "cod2eft.test_pose"
    bl_label = "Test Pose"
    bl_description = ("Put the EFT armature in the chosen test pose to see how the converted "
                      "mesh bends (or back to rest). Export always writes the rest pose")
    rest: BoolProperty(default=False, options={"SKIP_SAVE"})

    def execute(self, context):
        eft = C.find_eft_armature(context)
        if not eft:
            self.report({"ERROR"}, "No EFT armature in the scene")
            return {"CANCELLED"}
        C.ensure_object_mode()
        if self.rest:
            TL.clear_test_pose(eft)
            return {"FINISHED"}
        missing = TL.apply_test_pose(eft, context.scene.cod2eft.test_pose)
        if missing:
            self.report({"WARNING"}, "Bones not found: " + ", ".join(missing))
        return {"FINISHED"}


class COD2EFT_OT_check_clipping(bpy.types.Operator):
    bl_idname = "cod2eft.check_clipping"
    bl_label = "Check Gear Clipping"
    bl_description = ("Colour the converted parts by how far they stick out of EFT's own body "
                      "(white = on/inside, yellow -> red = up to 8 cm or more outside). EFT vests, "
                      "rigs, backpacks and helmets are made for EFT's body, so red areas are "
                      "where they may clip. Removed automatically when exporting")

    def execute(self, context):
        eft = C.find_eft_armature(context)
        if not eft:
            self.report({"ERROR"}, "No EFT armature in the scene")
            return {"CANCELLED"}
        if TL.test_pose_active(eft):
            TL.clear_test_pose(eft)
        lines, dists = TL.outside_report(eft)
        if not dists:
            self.report({"ERROR"}, "Needs converted parts and EFT reference meshes (template)")
            return {"CANCELLED"}
        TL.write_outside_colors(dists)
        TL.show_outside_colors()
        log = C.Log()
        log("Gear clipping check (distance outside EFT's own body):")
        for ln in lines:
            log("  " + ln)
        log.to_text()
        self.report({"INFO"}, "; ".join(lines))
        return {"FINISHED"}


class COD2EFT_OT_clear_clipping(bpy.types.Operator):
    bl_idname = "cod2eft.clear_clipping"
    bl_label = "Clear"
    bl_description = "Remove the gear-clipping colours"

    def execute(self, context):
        eft = C.find_eft_armature(context)
        n = TL.clear_outside_colors(TL.converted_meshes(eft) if eft else [])
        for sp in TL._shading_spaces():
            if sp.shading.color_type == "VERTEX":
                sp.shading.color_type = "MATERIAL"
        self.report({"INFO"}, f"Cleared {n} mesh(es)")
        return {"FINISHED"}


# ---------------------------------------------------------------------------------------------
# operators - adjust by hand (non-destructive until applied)
# ---------------------------------------------------------------------------------------------
class COD2EFT_OT_adjust_start(bpy.types.Operator):
    bl_idname = "cod2eft.adjust_start"
    bl_label = "Start Adjusting"
    bl_description = ("Pose a copy of the EFT armature to reshape the converted meshes: move, rotate "
                      "and scale any bone freely (bones are disconnected). The meshes follow live; "
                      "the EFT armature and the weights are not touched. Apply bakes it in, Cancel "
                      "throws it away")
    selected_only: BoolProperty(name="Only selected meshes", default=False,
                                description="Adjust only the selected converted meshes (e.g. one "
                                            "accessory that clips) - the rest stays as it is")
    reuse_last: BoolProperty(name="Start from the last applied pose", default=False,
                             options={"SKIP_SAVE"})
    unparent: BoolProperty(name="Unparent bones", default=True,
                           description="Bones move on their own: moving or rotating one never "
                                       "carries its children along (like unparenting every bone "
                                       "by hand). Off: bones stay in their hierarchy, only "
                                       "disconnected")

    def invoke(self, context, event):
        self.selected_only = any(o.select_get() and o.type == "MESH" for o in context.scene.objects)
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        eft = C.find_eft_armature(context)
        if not eft:
            self.report({"ERROR"}, "No EFT armature in the scene")
            return {"CANCELLED"}
        meshes = TL.converted_meshes(eft)
        if self.selected_only:
            meshes = [o for o in meshes if o.select_get()]
        try:
            TL.adjust_start(eft, meshes, TL.adjust_last_pose() if self.reuse_last else None,
                            unparent=self.unparent)
        except RuntimeError as e:
            self.report({"ERROR"}, str(e))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Adjusting {len(meshes)} mesh(es): pose the bones "
                              "(G / R / S), then Apply or Cancel in the COD2EFT panel")
        return {"FINISHED"}


class COD2EFT_OT_adjust_apply(bpy.types.Operator):
    bl_idname = "cod2eft.adjust_apply"
    bl_label = "Apply"
    bl_description = ("Bake the adjust pose into the meshes (shape keys too) and remove the adjust "
                      "armature. Ctrl+Z undoes it")
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        n = TL.adjust_apply()
        C.Log()(f"Adjustments applied by hand to {n} mesh(es)")
        self.report({"INFO"}, f"Applied to {n} mesh(es)")
        return {"FINISHED"}


class COD2EFT_OT_adjust_cancel(bpy.types.Operator):
    bl_idname = "cod2eft.adjust_cancel"
    bl_label = "Cancel"
    bl_description = "Remove the adjust armature; the meshes go back to how they were"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        TL.adjust_cancel()
        self.report({"INFO"}, "Adjustments discarded")
        return {"FINISHED"}


def _draw_last_fit(b, scene):
    raw = scene.get("cod2eft_last_fit")
    if not raw:
        return
    try:
        f = json.loads(raw)
    except ValueError:
        return
    col = b.column(align=True)
    col.label(text=f"Last fit: {f.get('name', '')}", icon="INFO")
    body = f.get("body", {})

    def worst(keys):
        v = [abs(body[k]) for k in body if k.split("_")[0] in keys]
        return f"{max(v):.1f}" if v else "-"
    if body:
        col.label(text=f"  Off from EFT (cm): torso {worst(('pelvis', 'spine2', 'spine3'))}, "
                       f"legs {worst(('knee', 'shin', 'thigh'))}, arms {worst(('upperarm',))}")
    if "soles" in f:
        col.label(text=f"  Soles {f['soles']:+.1f} cm from floor, tilt {f['sole_tilt']:.1f} deg")
    if "face" in f:
        what = "Eyes" if f.get("face_what") == "eye centres" else "Face"
        col.label(text=f"  {what} {f['face'][0]:+.1f} cm fwd, {f['face'][1]:+.1f} cm up vs EFT")
    post = f.get("posture", {})
    for k, what in (("back", "Back"), ("neck", "Neck")):
        # joint line, and the line through the section centres (the body as seen) when measured
        rows = [(post[x], x.endswith("~")) for x in (k, k + "~") if x in post]
        if rows:
            col.label(text=f"  {what} lean vs EFT: " + ", ".join(
                f"{'body' if body else 'joints'} {abs(c - e):.1f} deg "
                f"{'fwd' if c >= e else 'back'}" for (c, e), body in rows))
    if "tips" in f:
        col.label(text=f"  Fingertips {f['tips'][0]:.1f} cm from EFT's (max {f['tips'][1]:.1f})")
    if f.get("outside"):
        col.label(text="  Sticks out > 3 cm: " + ", ".join(
            f"{k} {v}%" for k, v in sorted(f["outside"].items())))
    try:
        tiles = json.loads(scene.get("cod2eft_uv_tiles", "[]"))
    except ValueError:
        tiles = []
    if tiles:
        col.label(text=f"  Check: {len(tiles)} material(s) use UV tiles outside 0..1 "
                       "(report: WARNING UV tiles)", icon="ERROR")


# ---------------------------------------------------------------------------------------------
# panels
# ---------------------------------------------------------------------------------------------
# One main panel (status: version, template, EFT armature, changed settings) with sub-panels in
# the order of the everyday flow:  Convert a Character -> Export for Unity, then the rarely used
# ones (collapsed by default): Extra Parts, Batch, Settings (Fit / Parts & Weights / Textures),
# Check the Fit, Adjust by Hand, Step by Step.  Blender remembers which ones are open.
class _Panel:
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "COD2EFT"


class _SubPanel(_Panel):
    bl_parent_id = "COD2EFT_PT_panel"
    bl_options = {"DEFAULT_CLOSED"}


class COD2EFT_PT_panel(_Panel, bpy.types.Panel):
    bl_idname = "COD2EFT_PT_panel"
    bl_label = f"COD → EFT Porter  v{C.VERSION_STR}"

    def draw(self, context):
        st = context.scene.cod2eft
        pf = prefs(context)
        L = self.layout
        running = _BATCH["proc"] is not None

        b = L.box()
        home = live_home()
        r = b.row(align=True)
        if home:
            r.label(text="Live from the COD2EFT folder", icon="LINKED")
            sub = r.row(align=True)
            sub.enabled = not running
            sub.operator("cod2eft.reload", text="", icon="FILE_REFRESH")
        else:
            r.label(text="Installed copy", icon="PACKAGE")
        # is there a newer (or different) version in the COD2EFT folder than the one running?
        folder = home or (pf.data_dir if pf and pf.data_dir else None)
        fv = folder_version(folder)
        if fv and fv != C.VERSION:
            col = b.column(align=True)
            col.alert = True
            col.label(text=f"COD2EFT folder has v{_vstr(fv)}, running v{C.VERSION_STR}:",
                      icon="ERROR")
            col.label(text="  press Reload (or restart Blender)" if home else
                      "  close Blender, run Install_COD2EFT_Addon.bat")
        if pf:
            b.prop(pf, "template_path", text="Template")
        eft = C.find_eft_armature(context)
        r = b.row()
        r.label(text=f"EFT armature: {eft.name if eft else 'none in scene'}",
                icon="CHECKMARK" if eft else "ERROR")
        if not eft:
            r.operator("cod2eft.append_template", text="Add", icon="APPEND_BLEND")

        # settings used by Import and Batch: changes are always visible, even when collapsed
        changed = changed_settings(st)
        ntw = saved_tweak_count() if st.apply_tweaks else 0
        if changed:
            col = b.column(align=True)
            col.alert = True
            r = col.row(align=True)
            r.label(text=f"{len(changed)} setting(s) changed from default:", icon="ERROR")
            r.operator("cod2eft.reset_settings", text="Reset", icon="LOOP_BACK")
            for label, val in changed:
                col.label(text=f"    {label}: {val}")
        else:
            b.label(text="Settings: all at their defaults", icon="CHECKMARK")
        if ntw:
            r = b.row(align=True)
            r.alert = True
            r.label(text=f"Saved pose tweaks on {ntw} bone(s) are applied", icon="ERROR")
            r.operator("cod2eft.clear_tweaks", text="", icon="TRASH")
        if running:
            b.label(text="Batch running: " + _BATCH["status"], icon="TIME")


class COD2EFT_PT_convert(_Panel, bpy.types.Panel):
    bl_parent_id = "COD2EFT_PT_panel"
    bl_label = "1. Convert a Character"

    def draw(self, context):
        L = self.layout
        c = L.column(align=True)
        c.scale_y = 1.4
        op = c.operator("cod2eft.import_cod", text="Import + Convert...", icon="PLAY")
        op.convert = True
        op = L.operator("cod2eft.import_cod", text="Import only...", icon="IMPORT")
        op.convert = False
        L.label(text="Uses the Settings below", icon="INFO")


class COD2EFT_PT_export(_Panel, bpy.types.Panel):
    bl_parent_id = "COD2EFT_PT_panel"
    bl_label = "2. Export for Unity"

    def draw(self, context):
        L = self.layout
        c = L.column(align=True)
        c.scale_y = 1.4
        c.operator("cod2eft.export", icon="EXPORT")
        c = L.column(align=True)
        c.label(text="Then run COD2EFT_To_Unity.bat: it copies", icon="INFO")
        c.label(text="    the .fbx + PNGs into the Unity project")


class COD2EFT_PT_parts(_SubPanel, bpy.types.Panel):
    bl_label = "Extra Parts"

    def draw(self, context):
        L = self.layout
        L.operator("cod2eft.split_materials", icon="MOD_EXPLODE")
        L.operator("cod2eft.fp_hands", icon="VIEW_PAN")


class COD2EFT_PT_batch(_SubPanel, bpy.types.Panel):
    bl_label = "Batch Convert (files / folders)"

    def draw(self, context):
        st = context.scene.cod2eft
        L = self.layout
        running = _BATCH["proc"] is not None
        L.prop(st, "output_dir")
        L.prop(st, "export_fbx")
        r = L.row()
        r.enabled = not running
        r.operator("cod2eft.batch", text="Batch Convert...", icon="SEQ_SEQUENCER")
        if _BATCH["status"]:
            L.label(text=("Running: " if running else "") + _BATCH["status"],
                    icon="TIME" if running else "INFO")
            if running:
                L.label(text="(Esc over the 3D view cancels)")
        if _BATCH["out"] and not running:
            L.operator("cod2eft.open_folder", text="Open output folder",
                       icon="FILEBROWSER").what = "out"


class COD2EFT_PT_settings(_SubPanel, bpy.types.Panel):
    bl_label = "Settings (Import + Batch)"

    def draw(self, context):
        st = context.scene.cod2eft
        r = self.layout.row(align=True)
        r.label(text="Changed settings are listed above")
        sub = r.row(align=True)
        sub.enabled = bool(changed_settings(st))
        sub.operator("cod2eft.reset_settings", text="Reset", icon="LOOP_BACK")
        r = self.layout.row(align=True)
        r.prop(st, "preset", text="")
        r.operator("cod2eft.preset_load", text="Load", icon="IMPORT")
        r.operator("cod2eft.preset_save", text="", icon="FILE_TICK")
        r.operator("cod2eft.preset_delete", text="", icon="TRASH")
        start = load_presets()["startup"]
        if start:
            self.layout.label(text=f"New scenes start with preset '{start}'", icon="INFO")


class COD2EFT_PT_settings_fit(_Panel, bpy.types.Panel):
    bl_parent_id = "COD2EFT_PT_settings"
    bl_label = "Fit"
    bl_options = {"DEFAULT_CLOSED"}

    def draw_header(self, context):
        self.layout.label(icon="POSE_HLT")

    def draw(self, context):
        st = context.scene.cod2eft
        col = self.layout.column(align=True)
        col.prop(st, "match_body")
        sub = col.column(align=True)
        sub.enabled = st.match_body
        sub.prop(st, "head_height")
        sub.prop(st, "face_landmark")
        sub.prop(st, "neck_lean")
        sub.prop(st, "head_forward", slider=True)
        sub.prop(st, "match_neck")
        sub.prop(st, "match_fingertips")
        col.prop(st, "match_lengths")
        col.prop(st, "fit_scale")
        col.prop(st, "apply_tweaks")


class COD2EFT_PT_settings_convert(_Panel, bpy.types.Panel):
    bl_parent_id = "COD2EFT_PT_settings"
    bl_label = "Parts & Weights"
    bl_options = {"DEFAULT_CLOSED"}

    def draw_header(self, context):
        self.layout.label(icon="MOD_VERTEX_WEIGHT")

    def draw(self, context):
        st = context.scene.cod2eft
        col = self.layout.column(align=True)
        col.prop(st, "prefer_cast")
        col.prop(st, "max_influences")
        col.prop(st, "join_parts")
        col.prop(st, "split_materials")
        col.prop(st, "fp_hands")
        sub = col.row(align=True)
        sub.enabled = st.fp_hands
        sub.prop(st, "fp_source")


class COD2EFT_PT_settings_textures(_Panel, bpy.types.Panel):
    bl_parent_id = "COD2EFT_PT_settings"
    bl_label = "Textures"
    bl_options = {"DEFAULT_CLOSED"}

    def draw_header(self, context):
        self.layout.prop(context.scene.cod2eft, "convert_textures", text="")

    def draw(self, context):
        st = context.scene.cod2eft
        col = self.layout.column(align=True)
        col.enabled = st.convert_textures
        col.prop(st, "texture_size")
        col.prop(st, "tex_material_mode", text="")
        if st.tex_material_mode == "ENC3":
            col.prop(st, "tex_gloss_match", slider=True)
        _draw_classes(col, st)


class COD2EFT_PT_settings_tex_options(_Panel, bpy.types.Panel):
    bl_parent_id = "COD2EFT_PT_settings_textures"
    bl_label = "Texture Options"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        st = context.scene.cod2eft
        c = self.layout.column(align=True)
        c.enabled = st.convert_textures
        c.prop(st, "tex_layout", text="")
        r = c.row(align=True)
        r.prop(st, "tex_normals", expand=True)
        c.prop(st, "tex_spec")
        c.prop(st, "tex_metal")
        c.prop(st, "tex_ao")
        r = c.row()
        r.enabled = st.tex_ao > 0
        r.prop(st, "tex_ao_spec")
        c.separator()
        c.prop(st, "tex_colour_gain")
        c.prop(st, "tex_colour_sat")


class COD2EFT_PT_check(_SubPanel, bpy.types.Panel):
    bl_label = "Check the Fit"

    def draw(self, context):
        st = context.scene.cod2eft
        L = self.layout
        on = TL.compare_is_on(context.scene)
        L.operator("cod2eft.compare", text="Compare to EFT" + (" (on)" if on else ""),
                   icon="OVERLAY", depress=on)
        r = L.row(align=True)
        r.prop(st, "test_pose", text="")
        r.operator("cod2eft.test_pose", text="Pose", icon="ARMATURE_DATA").rest = False
        r.operator("cod2eft.test_pose", text="Rest", icon="LOOP_BACK").rest = True
        r = L.row(align=True)
        r.operator("cod2eft.check_clipping", icon="MOD_SHRINKWRAP")
        r.operator("cod2eft.clear_clipping", text="", icon="X")
        _draw_last_fit(L, context.scene)


class COD2EFT_PT_adjust(_SubPanel, bpy.types.Panel):
    bl_label = "Adjust by Hand (armature)"

    def draw(self, context):
        L = self.layout
        adj = TL.adjust_rig(context.scene)
        if adj is None:
            L.operator("cod2eft.adjust_start", icon="POSE_HLT")
            if TL.adjust_last_pose(context.scene):
                L.operator("cod2eft.adjust_start", text="Start from last applied pose",
                           icon="RECOVER_LAST").reuse_last = True
        else:
            L.label(text=f"Posing '{adj.name}': {len(TL.adjust_meshes(context.scene))} mesh(es) follow"
                         + (" (bones unparented)" if adj.get("cod2eft_unparented") else ""),
                    icon="INFO")
            r = L.row(align=True)
            r.operator("cod2eft.adjust_apply", icon="CHECKMARK")
            r.operator("cod2eft.adjust_cancel", icon="X")


class COD2EFT_PT_steps(_SubPanel, bpy.types.Panel):
    bl_label = "Step by Step"

    def draw(self, context):
        st = context.scene.cod2eft
        L = self.layout
        L.label(text="(uses the Settings above)", icon="INFO")
        L.prop(st, "eft_armature")
        L.prop(st, "output_name")
        cods = [o.name for o in context.scene.objects
                if C.is_cod_armature(o) and o.visible_get()]
        L.label(text=f"COD armatures: {', '.join(cods) if cods else 'none'}",
                icon="OUTLINER_OB_ARMATURE")
        L.operator("cod2eft.fit", icon="POSE_HLT")
        r = L.row(align=True)
        r.operator("cod2eft.save_tweaks", icon="FILE_TICK")
        r.operator("cod2eft.clear_tweaks", icon="TRASH", text="")
        L.operator("cod2eft.convert", icon="MOD_VERTEX_WEIGHT")
        L.operator("cod2eft.textures", icon="TEXTURE")
        L.operator("cod2eft.oneclick", icon="PLAY")
        L.operator("cod2eft.open_folder", text="Open settings folder",
                   icon="FILEBROWSER").what = "data"


PANELS = (COD2EFT_PT_panel, COD2EFT_PT_convert, COD2EFT_PT_export, COD2EFT_PT_parts,
          COD2EFT_PT_batch, COD2EFT_PT_settings, COD2EFT_PT_settings_fit,
          COD2EFT_PT_settings_convert, COD2EFT_PT_settings_textures,
          COD2EFT_PT_settings_tex_options, COD2EFT_PT_check, COD2EFT_PT_adjust, COD2EFT_PT_steps)

classes = (COD2EFT_Prefs, COD2EFT_MatClass, COD2EFT_Settings, COD2EFT_OT_reload, COD2EFT_OT_reset_settings,
           COD2EFT_OT_split_materials, COD2EFT_OT_fp_hands,
           COD2EFT_OT_append_template, COD2EFT_OT_import,
           COD2EFT_OT_batch, COD2EFT_OT_open_folder, COD2EFT_OT_fit, COD2EFT_OT_convert,
           COD2EFT_OT_oneclick, COD2EFT_OT_save_tweaks, COD2EFT_OT_clear_tweaks,
           COD2EFT_OT_export, COD2EFT_OT_textures, COD2EFT_OT_compare, COD2EFT_OT_test_pose,
           COD2EFT_OT_check_clipping, COD2EFT_OT_clear_clipping, COD2EFT_OT_adjust_start,
           COD2EFT_OT_adjust_apply, COD2EFT_OT_adjust_cancel, COD2EFT_OT_preset_save,
           COD2EFT_OT_preset_load, COD2EFT_OT_preset_delete) + PANELS


def register():
    C.ADDON_PKG = PKG
    # Preferences > Add-ons shows bl_info of the installed loader; show the running code's
    # version there instead (they differ after a live update)
    mod = sys.modules.get(PKG)
    if mod is not None and isinstance(getattr(mod, "bl_info", None), dict):
        mod.bl_info["version"] = tuple(C.VERSION)
    for c in classes:
        bpy.utils.register_class(c)
    bpy.types.Scene.cod2eft = PointerProperty(type=COD2EFT_Settings)
    if _on_load_post not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_on_load_post)
    bpy.app.timers.register(_startup_timer, first_interval=0.5)   # the file open at start-up


def unregister():
    if _on_load_post in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_on_load_post)
    del bpy.types.Scene.cod2eft
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
