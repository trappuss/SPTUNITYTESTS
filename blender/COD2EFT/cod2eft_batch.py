"""
COD2EFT batch converter - run by COD2EFT_Convert.bat, or manually:

  blender --background --factory-startup --python cod2eft_batch.py -- ^
        --template "EFT BASIC [Template].blend" --out OUTDIR [options] FILES_OR_FOLDERS...

Options:
  --cast            when a folder has both .fbx and .cast of a model, use the .cast
  --joints-only     old joint-on-joint fit (don't match body volume / floor / face)
  --head-height M   off / limited (default: spine + neck stretched at most +-6 % so the eyes
                    reach EFT's eye height) / full (0.75 - 1.35)
  --match-height    same as --head-height full
  --face-landmark L eyes (default: line the head up by the eyeball centres) / nose
  --fp-hands        also make <name>_Hands for EFT's first-person hands (weighted to EFT's
                    40 hand bones; own texture set) - from the character's COD first-person
                    arms model when it was exported, else from the arms of Upper
  --fp-hands-from third   always from the arms of Upper
  --no-fingertips   don't move the finger roots onto EFT's knuckles or aim the last finger
                    segments at EFT's fingertips
  --neck-lean DEG   most the neck may lean forward to line the face up (default 35)
  --head-forward F  0..1 share of the leftover face gap taken up by the upper spine (default 0.5)
  --no-textures     don't convert the COD textures
  --texture-size N  atlas size per part (default 2048)
  --normal-style S  opengl (default - what EFT's own normal maps use) or directx
  --spec-strength F multiplier on the specular written to the colour alpha (default 1 = COD)
  --metal-colour F  0..1 share of a metal's colour kept in the colour map (default 0.7)
  --ao-strength F   0..1 how much COD ambient occlusion goes into the colour (default 1)
  --no-ao-spec      don't multiply the occlusion into the specular as well
  --texture-layout L islands (default: only the used parts of each texture, sharper) or whole
  --material-mode M enc2 (default: COD's values, Unity calibrates per part) or enc3 (each COD
                    material classified, its look baked into the textures for EFT's neutral
                    values; the report lists every material's class)
  --colour-brightness F   multiplier on the colour map (default 1 = as COD)
  --colour-saturation F   0 = grey .. 1 = as COD (default) .. 2
  --gloss-match F   enc3 only: 0 = COD's own gloss .. 1 = the full vanilla curve (default)
  --class-overrides FILE  JSON {"COD material name": "cloth|skin|leather|metal|glass"} that wins
                    over the enc3 classifier (the panel writes one from its class list)
  --no-lengths      aim only (don't stretch limb segments onto their targets)
  --no-tweaks       don't apply the saved pose tweaks (cod2eft_pose_tweaks.json)
  --fit-scale       best-fit scale to the EFT torso instead of keeping real-world size
  --no-join         keep every COD sub-mesh separate (still sorted into Head/Upper/Lower)
  --split-materials after converting, split each part into one object per COD material
  --export-fbx      also write <name>_EFT.fbx next to the .blend (template FBX settings)
  --max-influences N  (default 4)
  --data-dir DIR    folder with cod2eft_bonemap.json / cod2eft_pose_tweaks.json

Drop single files, a character folder, or a whole tree of folders.  Files are grouped into
characters (see cod2eft_files.py): body_X + head_X, and Cold War torso/lowerbody/arms/head
parts, are converted together; first-person models and alternative heads are skipped.
Each character becomes one <name>_EFT.blend (+ .fbx, + report).
"""
import bpy
import sys
import os
import re
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import cod2eft_porter as C  # noqa: E402
import cod2eft_files as F  # noqa: E402
import cod2eft_textures as TX  # noqa: E402
import cod2eft_tools as TL  # noqa: E402

MODEL_EXT = (".fbx", ".cast")


def parse(argv):
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    o = dict(template=None, out=None, cast=False, lengths=True, fit_scale=False, join=True,
             export_fbx=False, max_inf=4, body=True, height=False, neck_lean=35.0,
             head_forward=0.5, head_height="limited", face="eyes", tips=True, tweaks=True,
             split=False, fp_hands=False, fp_source="AUTO", textures=True, tex_size=2048, inputs=[],
             tex=dict(normal_style="OPENGL", spec_scale=1.0, ao_strength=1.0, ao_in_spec=True,
                      uv_layout="ISLANDS", metal_keep=0.7, material_mode="ENC2",
                      class_overrides={}, colour_gain=1.0, colour_sat=1.0, gloss_match=1.0))
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--template":
            o["template"] = argv[i + 1]; i += 1
        elif a == "--out":
            o["out"] = argv[i + 1]; i += 1
        elif a == "--cast":
            o["cast"] = True
        elif a == "--joints-only":
            o["body"] = False
        elif a == "--no-textures":
            o["textures"] = False
        elif a == "--texture-size":
            o["tex_size"] = int(argv[i + 1]); i += 1
        elif a == "--normal-style":
            o["tex"]["normal_style"] = argv[i + 1].upper(); i += 1
        elif a == "--spec-strength":
            o["tex"]["spec_scale"] = float(argv[i + 1]); i += 1
        elif a == "--ao-strength":
            o["tex"]["ao_strength"] = min(1.0, max(0.0, float(argv[i + 1]))); i += 1
        elif a == "--metal-colour":
            o["tex"]["metal_keep"] = min(1.0, max(0.0, float(argv[i + 1]))); i += 1
        elif a == "--no-ao-spec":
            o["tex"]["ao_in_spec"] = False
        elif a == "--material-mode":
            o["tex"]["material_mode"] = "ENC3" if argv[i + 1].lower() == "enc3" else "ENC2"
            i += 1
        elif a == "--colour-brightness":
            o["tex"]["colour_gain"] = min(2.0, max(0.25, float(argv[i + 1]))); i += 1
        elif a == "--colour-saturation":
            o["tex"]["colour_sat"] = min(2.0, max(0.0, float(argv[i + 1]))); i += 1
        elif a == "--gloss-match":
            o["tex"]["gloss_match"] = min(1.0, max(0.0, float(argv[i + 1]))); i += 1
        elif a == "--class-overrides":
            o["tex"]["class_overrides"] = TX.load_class_overrides(argv[i + 1]); i += 1
        elif a == "--texture-layout":
            o["tex"]["uv_layout"] = argv[i + 1].upper(); i += 1
        elif a == "--match-height":
            o["height"] = True
            o["head_height"] = "full"
        elif a == "--head-height":
            o["head_height"] = argv[i + 1].lower(); i += 1
        elif a == "--face-landmark":
            o["face"] = argv[i + 1].lower(); i += 1
        elif a == "--fp-hands":
            o["fp_hands"] = True
        elif a == "--fp-hands-from":
            o["fp_source"] = "THIRD" if argv[i + 1].lower().startswith("third") else "AUTO"
            i += 1
        elif a == "--no-fingertips":
            o["tips"] = False
        elif a == "--neck-lean":
            o["neck_lean"] = float(argv[i + 1]); i += 1
        elif a == "--head-forward":
            o["head_forward"] = float(argv[i + 1]); i += 1
        elif a == "--no-lengths":
            o["lengths"] = False
        elif a == "--no-tweaks":
            o["tweaks"] = False
        elif a == "--fit-scale":
            o["fit_scale"] = True
        elif a == "--no-join":
            o["join"] = False
        elif a == "--split-materials":
            o["split"] = True
        elif a == "--export-fbx":
            o["export_fbx"] = True
        elif a == "--data-dir":
            os.environ["COD2EFT_DATA_DIR"] = argv[i + 1]; i += 1
        elif a == "--max-influences":
            o["max_inf"] = int(argv[i + 1]); i += 1
        else:
            o["inputs"].append(a)
        i += 1
    return o


def safe_autopack(log):
    """The template has 'Automatically Pack Resources' on; a COD export whose textures are
    missing would make the save fail.  Pack what exists, and turn autopack off only if needed."""
    if not bpy.data.use_autopack:
        return
    missing = []
    for img in bpy.data.images:
        if img.packed_file or img.source != "FILE" or not img.filepath:
            continue
        if os.path.isfile(bpy.path.abspath(img.filepath)):
            try:
                img.pack()
            except Exception:
                missing.append(img.name)
        else:
            missing.append(img.name)
    if missing:
        bpy.data.use_autopack = False
        log(f"NOTE: {len(missing)} COD texture file(s) not found on disk (e.g. "
            f"{missing[0]}) - autopack switched off for this file so it can be saved")


def convert_group(char, opt):
    key = char["key"]
    files = list(char["bodies"]) + ([char["head"]] if char["head"] else [])
    log = C.Log()
    log(f"===== {key} =====   (folder: {char['dir']})   COD2EFT v{C.VERSION_STR}")
    bpy.ops.wm.open_mainfile(filepath=opt["template"])
    before = set(bpy.data.objects)
    for f in files:
        log(f"Importing {os.path.basename(f)}")
        C.import_model(f, log)
    new = [o for o in bpy.data.objects if o not in before]
    cod_arms = [o for o in new if C.is_cod_armature(o)]
    eft = C.find_eft_armature()
    if eft is None:
        raise RuntimeError("Template has no EFT armature (bone 'Base HumanPelvis')")
    log(f"EFT armature: {eft.name}; COD armatures: {[a.name for a in cod_arms]}")
    rig, meshes, _ = C.run_fit(cod_arms, eft, match_lengths=opt["lengths"],
                               fit_scale=opt["fit_scale"], apply_tweaks=opt["tweaks"], log=log,
                               basename=key, match_body=opt["body"],
                               match_height=opt["height"], neck_max_lean=opt["neck_lean"],
                               head_forward=opt["head_forward"], head_height=opt["head_height"],
                               face_landmark=opt["face"], match_fingertips=opt["tips"])
    results, _ = C.run_convert(rig, eft, max_influences=opt["max_inf"], join_parts=opt["join"],
                               log=log)
    if opt["fp_hands"]:
        h = C.make_fp_hands(eft, results, char.get("fp"), log, source=opt["fp_source"],
                            max_influences=opt["max_inf"], match_lengths=opt["lengths"],
                            match_fingertips=opt["tips"], fit_scale=opt["fit_scale"])
        if h is not None:
            results.append(h)
    if opt["textures"]:
        try:
            TX.convert_textures(results, opt["out"], key, size=opt["tex_size"], log=log,
                                **opt["tex"])
            # drop import leftovers; the original COD materials have a fake user and stay, so
            # Convert Textures can be run again on this .blend
            bpy.data.orphans_purge(do_recursive=True)
        except Exception as ex:
            traceback.print_exc()
            log(f"WARNING: texture conversion failed ({ex}) - meshes keep the COD materials")
    if opt["split"]:
        results = TL.split_by_material(results, log)
    # remove import leftovers (e.g. the FBX root empty) that ended up empty
    for o in list(bpy.data.objects):
        if o in before or o in results:
            continue
        if o.type == "EMPTY" and not o.children:
            bpy.data.objects.remove(o, do_unlink=True)
    os.makedirs(opt["out"], exist_ok=True)
    out_blend = os.path.join(opt["out"], f"{key}_EFT.blend")
    if opt["export_fbx"]:
        C.export_fbx(eft, results, os.path.join(opt["out"], f"{key}_EFT.fbx"), log)
    safe_autopack(log)
    log(f"Saving {out_blend}")
    log.to_text()
    bpy.ops.wm.save_as_mainfile(filepath=out_blend)
    log(f"Saved {out_blend}")
    with open(os.path.join(opt["out"], f"{key}_EFT_report.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(log.lines) + "\n")
    return out_blend


def main():
    try:
        sys.stdout.reconfigure(line_buffering=True)     # live progress when run from the add-on
    except Exception:
        pass
    opt = parse(sys.argv)
    if not opt["template"] or not os.path.isfile(opt["template"]):
        print(f"[COD2EFT] ERROR: template not found: {opt['template']}")
        sys.exit(2)
    say = lambda m: print(f"[COD2EFT] {m}")
    say(f"COD2EFT v{C.VERSION_STR} (code in {os.path.dirname(os.path.abspath(C.__file__))}), "
        f"Blender {bpy.app.version_string}")
    files = F.discover(opt["inputs"], prefer_cast=opt["cast"], log=say)
    tmpl = os.path.normcase(os.path.abspath(opt["template"]))
    files = [f for f in files
             if os.path.normcase(os.path.splitext(os.path.abspath(f))[0]) != os.path.splitext(tmpl)[0]]
    chars, skipped = F.group(files, log=say)
    say(f"{len(chars)} character(s) to convert, {len(skipped)} file(s) skipped")
    for f, why in skipped:
        say(f"  skip {os.path.basename(f)}: {why}")
    if not chars:
        say("ERROR: no COD character models found in the inputs")
        sys.exit(2)
    if not opt["out"]:
        ins = [os.path.abspath(p.strip().strip('"')) for p in opt["inputs"]]
        base = ins[0] if os.path.isdir(ins[0]) else os.path.dirname(ins[0])
        opt["out"] = os.path.join(base, "EFT_Converted")
    os.makedirs(opt["out"], exist_ok=True)
    ok, bad = [], []
    for i, ch in enumerate(chars, 1):
        say(f"PROGRESS {i}/{len(chars)}: {ch['key']}")
        say(f"--- {ch['key']}: " + ", ".join(os.path.basename(f) for f in
                                           ch["bodies"] + ([ch["head"]] if ch["head"] else [])))
        try:
            ok.append(convert_group(ch, opt))
        except Exception:
            traceback.print_exc()
            bad.append(ch["key"])
    say("")
    say("================= SUMMARY =================")
    for p in ok:
        say(f"OK     {p}")
    for k in bad:
        say(f"FAILED {k}")
    with open(os.path.join(opt["out"], "_COD2EFT_summary.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join([f"OK     {p}" for p in ok] + [f"FAILED {k}" for k in bad] +
                           [f"skip   {f}: {w}" for f, w in skipped]) + "\n")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
