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


class COD2EFT_Settings(bpy.types.PropertyGroup):
    eft_armature: PointerProperty(name="EFT Armature", type=bpy.types.Object, poll=_poll_eft)
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
    join_parts: BoolProperty(name="Join into Head/Upper/Lower", default=True)
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
    if st.convert_textures:
        cmd += ["--texture-size", st.texture_size, "--normal-style", st.tex_normals.lower(),
                "--spec-strength", f"{st.tex_spec:g}", "--metal-colour", f"{st.tex_metal:g}",
                "--ao-strength", f"{st.tex_ao:g}",
                "--texture-layout", st.tex_layout.lower()]
        if not st.tex_ao_spec:
            cmd.append("--no-ao-spec")
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
                      match_fingertips=st.match_fingertips)
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
                TX.convert_textures(results, out, _out_name(context, results), log=log,
                                    **_tex_kwargs(st))
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
            ("head_forward", "Head forward"), ("match_lengths", "Snap limb joints"),
            ("fit_scale", "Best-fit scale"), ("apply_tweaks", "Apply saved tweaks"),
            ("max_influences", "Max bones / vertex"), ("join_parts", "Join parts"),
            ("split_materials", "Separate by COD material"),
            ("convert_textures", "Convert textures"), ("texture_size", "Texture size"),
            ("tex_layout", "Texture layout"), ("tex_normals", "Normal maps"),
            ("tex_spec", "Specular strength"), ("tex_metal", "Metal colour kept"),
            ("tex_ao", "AO strength"),
            ("tex_ao_spec", "AO into specular"))


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
                TX.convert_textures([h], out, _out_name(context, objs), log=log,
                                    **_tex_kwargs(st))
                msg += f" - textures in {out}"
            except Exception as ex:
                log(f"WARNING: texture conversion failed: {ex}")
                msg += " (texture conversion failed - see the report)"
        log.to_text()
        self.report({"INFO"}, msg)
        return {"FINISHED"}


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
                metal_keep=st.tex_metal)


def _draw_tex_options(layout, st):
    if not st.convert_textures:
        return
    c = layout.column(align=True)
    c.prop(st, "show_tex_options", icon="TRIA_DOWN" if st.show_tex_options else "TRIA_RIGHT",
           emboss=False)
    if st.show_tex_options:
        c.prop(st, "tex_layout", text="")
        r = c.row(align=True)
        r.prop(st, "tex_normals", expand=True)
        c.prop(st, "tex_spec")
        c.prop(st, "tex_metal")
        c.prop(st, "tex_ao")
        r = c.row()
        r.enabled = st.tex_ao > 0
        r.prop(st, "tex_ao_spec")


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
        res = TX.convert_textures(objs, out, _out_name(context, objs), log=log,
                                  **_tex_kwargs(st))
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
    if "tips" in f:
        col.label(text=f"  Fingertips {f['tips'][0]:.1f} cm from EFT's (max {f['tips'][1]:.1f})")
    if f.get("outside"):
        col.label(text="  Sticks out > 3 cm: " + ", ".join(
            f"{k} {v}%" for k, v in sorted(f["outside"].items())))


# ---------------------------------------------------------------------------------------------
# panel
# ---------------------------------------------------------------------------------------------
class COD2EFT_PT_panel(bpy.types.Panel):
    bl_label = f"COD → EFT Porter  v{C.VERSION_STR}"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "COD2EFT"

    def draw(self, context):
        st = context.scene.cod2eft
        pf = prefs(context)
        L = self.layout
        running = _BATCH["proc"] is not None

        b = L.box()
        home = live_home()
        r = b.row(align=True)
        r.label(text=f"COD2EFT v{C.VERSION_STR}", icon="PREFERENCES")
        if home:
            r.label(text="live from folder", icon="LINKED")
            sub = r.row(align=True)
            sub.enabled = not running
            sub.operator("cod2eft.reload", text="", icon="FILE_REFRESH")
        else:
            r.label(text="installed copy", icon="PACKAGE")
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

        # ---- settings used by Import and Batch: changes are always visible -----------------
        b = L.box()
        r = b.row(align=True)
        r.prop(st, "show_settings", text="Settings (Import + Batch)",
               icon="TRIA_DOWN" if st.show_settings else "TRIA_RIGHT", emboss=False)
        changed = changed_settings(st)
        ntw = saved_tweak_count() if st.apply_tweaks else 0
        if changed:
            r.operator("cod2eft.reset_settings", text="Reset", icon="LOOP_BACK")
            col = b.column(align=True)
            col.alert = True
            col.label(text=f"{len(changed)} setting(s) changed from default:", icon="ERROR")
            for label, val in changed:
                col.label(text=f"    {label}: {val}")
        else:
            b.label(text="All settings at their defaults", icon="CHECKMARK")
        if ntw:
            r = b.row(align=True)
            r.alert = True
            r.label(text=f"Saved pose tweaks on {ntw} bone(s) are applied", icon="ERROR")
            r.operator("cod2eft.clear_tweaks", text="", icon="TRASH")
        if st.show_settings:
            col = b.column(align=True)
            col.label(text="Fit", icon="POSE_HLT")
            col.prop(st, "match_body")
            sub = col.column(align=True)
            sub.enabled = st.match_body
            sub.prop(st, "head_height")
            sub.prop(st, "face_landmark")
            sub.prop(st, "neck_lean")
            sub.prop(st, "head_forward", slider=True)
            sub.prop(st, "match_fingertips")
            col.prop(st, "match_lengths")
            col.prop(st, "fit_scale")
            col.prop(st, "apply_tweaks")
            col = b.column(align=True)
            col.label(text="Convert", icon="MOD_VERTEX_WEIGHT")
            col.prop(st, "prefer_cast")
            col.prop(st, "max_influences")
            col.prop(st, "join_parts")
            col.prop(st, "split_materials")
            col.prop(st, "fp_hands")
            sub = col.row(align=True)
            sub.enabled = st.fp_hands
            sub.prop(st, "fp_source")
            col = b.column(align=True)
            col.label(text="Textures", icon="TEXTURE")
            r = col.row(align=True)
            r.prop(st, "convert_textures")
            sub = r.row(align=True)
            sub.enabled = st.convert_textures
            sub.prop(st, "texture_size", text="")
            _draw_tex_options(col, st)

        b = L.box()
        b.label(text="Convert one character (this scene)", icon="IMPORT")
        op = b.operator("cod2eft.import_cod", text="Import + Convert...", icon="PLAY")
        op.convert = True
        op = b.operator("cod2eft.import_cod", text="Import only...", icon="IMPORT")
        op.convert = False
        b.operator("cod2eft.split_materials", icon="MOD_EXPLODE")
        b.operator("cod2eft.fp_hands", icon="VIEW_PAN")
        b.operator("cod2eft.export", icon="EXPORT")

        b = L.box()
        b.label(text="Batch (files or whole folders)", icon="FILE_FOLDER")
        b.prop(st, "output_dir")
        b.prop(st, "export_fbx")
        r = b.row()
        r.enabled = not running
        r.operator("cod2eft.batch", text="Batch Convert...", icon="SEQ_SEQUENCER")
        if _BATCH["status"]:
            b.label(text=("Running: " if running else "") + _BATCH["status"],
                    icon="TIME" if running else "INFO")
            if running:
                b.label(text="(Esc over the 3D view cancels)")
        if _BATCH["out"] and not running:
            b.operator("cod2eft.open_folder", text="Open output folder",
                       icon="FILEBROWSER").what = "out"

        b = L.box()
        b.label(text="Check", icon="VIEWZOOM")
        on = TL.compare_is_on(context.scene)
        b.operator("cod2eft.compare", text="Compare to EFT" + (" (on)" if on else ""),
                   icon="OVERLAY", depress=on)
        r = b.row(align=True)
        r.prop(st, "test_pose", text="")
        r.operator("cod2eft.test_pose", text="Pose", icon="ARMATURE_DATA").rest = False
        r.operator("cod2eft.test_pose", text="Rest", icon="LOOP_BACK").rest = True
        r = b.row(align=True)
        r.operator("cod2eft.check_clipping", icon="MOD_SHRINKWRAP")
        r.operator("cod2eft.clear_clipping", text="", icon="X")
        _draw_last_fit(b, context.scene)

        b = L.box()
        b.prop(st, "show_steps", icon="TRIA_DOWN" if st.show_steps else "TRIA_RIGHT",
               emboss=False)
        if st.show_steps:
            b.label(text="(uses the Settings above)", icon="INFO")
            b.prop(st, "eft_armature")
            b.prop(st, "output_name")
            cods = [o.name for o in context.scene.objects
                    if C.is_cod_armature(o) and o.visible_get()]
            b.label(text=f"COD armatures: {', '.join(cods) if cods else 'none'}",
                    icon="OUTLINER_OB_ARMATURE")
            b.operator("cod2eft.fit", icon="POSE_HLT")
            r = b.row(align=True)
            r.operator("cod2eft.save_tweaks", icon="FILE_TICK")
            r.operator("cod2eft.clear_tweaks", icon="TRASH", text="")
            b.operator("cod2eft.convert", icon="MOD_VERTEX_WEIGHT")
            b.operator("cod2eft.textures", icon="TEXTURE")
            b.operator("cod2eft.oneclick", icon="PLAY")
            b.operator("cod2eft.open_folder", text="Open settings folder",
                       icon="FILEBROWSER").what = "data"


classes = (COD2EFT_Prefs, COD2EFT_Settings, COD2EFT_OT_reload, COD2EFT_OT_reset_settings,
           COD2EFT_OT_split_materials, COD2EFT_OT_fp_hands,
           COD2EFT_OT_append_template, COD2EFT_OT_import,
           COD2EFT_OT_batch, COD2EFT_OT_open_folder, COD2EFT_OT_fit, COD2EFT_OT_convert,
           COD2EFT_OT_oneclick, COD2EFT_OT_save_tweaks, COD2EFT_OT_clear_tweaks,
           COD2EFT_OT_export, COD2EFT_OT_textures, COD2EFT_OT_compare, COD2EFT_OT_test_pose,
           COD2EFT_OT_check_clipping, COD2EFT_OT_clear_clipping, COD2EFT_PT_panel)


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


def unregister():
    del bpy.types.Scene.cod2eft
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
