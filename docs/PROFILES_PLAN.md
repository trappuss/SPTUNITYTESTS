# Source profiles: splitting COD out of the core (design, 2026-10-01)

**Status: design only, parked.** The user's decision (2026-10-01): finish and refine COD first, then split.
This file says *what* moves *where* and how we prove nothing changed, so the split can start the day COD is
signed off. It complements `docs/RENAME_PLAN.md` (names, migrations); the split comes **before** the rename.

## Measured: where the COD knowledge sits (2.6.12)
| Part | Size | COD-specific |
|---|---|---|
| Unity tools (`unity/EFTAutoPrefabber`) | 4,912 lines | Only the PNG tag text `COD2EFT enc=…` and log wording. Works from the contract, not from COD. |
| SPT Inspector (`spt_mod/`) | 4,538 lines | Name / GUID / output folder only. |
| `cod2eft_tools.py` (checks, test poses, split by material, FP hands, hand-adjust) | 814 | None: 0 COD bone names. |
| `cod2eft_files.py` (find and group exports) | 243 | **All of it**: COD file naming (`vm_`, `viewarms`, `head_`, `_lod1`, `_images`, `_mat_info`). |
| `cod2eft_porter.py` (import, fit, weights, export) | 2,568 | **114 COD bone names**, almost all in tables: `default_weight_map`, `fit_table`, `ALIGN_LANDMARKS`, `TWIST_PROFILES`, `UP/LEFT_PAIRS_COD`, `CORE_SHARED`, `KNUCKLE_BONES`, `COD_SKIN_PRIOR`, `cod_nose/eyes/sole/fingertips`, `FAMILY_SIGNATURES`. `run_fit` itself has 17. Import (`ensure_cast`, long-path link) is COD-tooling. |
| `cod2eft_textures.py` (find, decode, atlas, enc2/enc3, preview material) | 2,121 | **The finding/decoding half**: `find_textures`, semantics / `_mat_info` / `.mtl`, NOG / hemi-octahedron / split exports, wrinkle maps, IW metal mask, Cold War packing, `classify_alpha`, opacity slots. The atlas, enc2/enc3 bake, PNG writing and preview material are generic. |

So the generic share is large; the COD share is concentrated in **tables** (porter) and **the texture front end**.

## The profile interface (Blender)
A profile is a Python module in `profiles/<name>/` that the core asks for exactly these things:

| Hook | Returns | COD today |
|---|---|---|
| `discover(inputs)` / `group(files)` | characters: body/head/FP files | `cod2eft_files.py` |
| `import_model(path)` | armature + meshes in Blender | Cast / FBX + long-path link |
| `joint_roles(armature)` | **semantic joint → source bone**: pelvis, spine1-3, neck, head, clavicle/shoulder/elbow/wrist/hip/knee/ankle/ball per side, 5 fingers × 3 (+ tip), eyes, nose | the `j_*` names now spread over `fit_table`, landmarks, pairs |
| `weight_map()` | source bone → {EFT bone: share} (helpers, twist, correctives) | `default_weight_map` + user `bonemap.json` |
| `detect(armature)` | family / head / viewmodel flags | `FAMILY_SIGNATURES`, `is_head_skeleton`, `is_viewmodel` |
| `find_textures(material, model)` | **roles → decoded arrays** in one neutral form: colour (sRGB), F0 / metal share, gloss, normal (OpenGL, unpacked), AO, opacity candidates | `find_textures` + NOG / CW / IW decoding + `classify_alpha` |
| `material_hints(material)` | name words / priors for the enc3 classifier | the WORDS sets |

Everything after these hooks stays in the core and must not mention COD: fit maths, body-volume and posture
matching, weight remap, region split, FP hands, hand-adjust, atlas, enc2/enc3 bake, PNG tag, FBX export, checks.

**Key move:** `fit_table`, `ALIGN_LANDMARKS`, `UP/LEFT_PAIRS`, `CORE_SHARED`, `KNUCKLE_BONES`, `TWIST_PROFILES` are
rewritten once in **semantic names** (`"pelvis"`, `"spine3"`, `"wrist.L"`…). The core resolves them through
`joint_roles()`. COD's profile is then mostly one dictionary (`pelvis: j_mainroot` …).

## Generic profile (the second one, after the split)
- Input: any FBX with an armature and Principled BSDF materials (what most rippers / Blender imports give).
- `joint_roles`: picked once in a panel list (auto-guessed by name similarity and hierarchy; saved per skeleton
  signature in a JSON next to the presets).
- `find_textures`: read the Principled BSDF links (Base Color, Normal Map, Roughness → gloss = 1 − roughness,
  Metallic → metal share, Alpha → opacity). No game-specific decoding.

## How we prove the split changed nothing
1. Before: `tools/regress.py` baselines enc2 + enc3 (with PNG signatures), plus the 716-material NOG-pick table and
   the class table from `docs/material_labels_2026-10-01.json`.
2. Move code in small commits (files → import → joint roles → weight map → textures), each one:
   regression **identical** (notes allowed, no differences), same NOG picks, same classes.
3. Only then add the generic profile, tested on one non-COD character the user picks.
4. Unity / Inspector need no code change for the split (only the later rename).

## What the split must not break (checklist)
- PC-owned files keep their names until the rename: `cod2eft_bonemap.json`, `cod2eft_presets.json`,
  `cod2eft_pose_tweaks.json` (the COD profile reads them).
- Saved `.blend` files: properties `cod2eft_*` on objects / scenes stay readable.
- Batch flags stay; a new `--profile cod|generic` defaults to `cod`.
- `build_addon.py` must include `profiles/**` (2.6.9 found it listing a removed folder - add a test that builds the
  zip and checks its file list).

## Size estimate (hunch, to refine when starting)
Files + import + joint roles + weight map: 2–3 sessions. Texture front end: 2 sessions (largest risk: the many COD
special cases). Generic profile: 1–2 sessions plus a test character from the user.
