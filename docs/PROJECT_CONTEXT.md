# Project context: COD → EFT (SPT) pipeline, both halves (2026-09-29)

This is **the** handoff and work queue for both halves; the Blender and Unity sides are one project now. Rules for sessions are in `CLAUDE.md`.
Tool docs: `blender/COD2EFT/README.md` (Blender), `unity/README.md` (Unity).
Contract: `blender/COD2EFT/unity/COD2EFT_TEXTURE_SPEC.md`. Materials: `docs/MATERIALS_PLAN.md`.
The old per-side handoffs and backlog (2026-09-27) are in `docs/archive/`; they are history, not instructions.

## Goal
Port Call of Duty characters (currently Warzone-era: MW2019, Vanguard, Cold War, MW2, MW3, BO6,
BO7, MW4 beta) into SPT 4.1.6 / EFT as clothing, heads and hands, with as little manual work as possible.

```
COD export (.fbx/.cast, Greyhound etc.)
  → COD2EFT (Blender 4.4 / 5.0): fit to EFT skeleton, weights, Head/Upper/Lower(/Hands) meshes, atlas PNGs
  → <name>_EFT.fbx + <name>_<Part>_d/_n/_g.png   (COD2EFT_To_Unity.bat copies them into the Unity project)
  → EFT Tools (Unity 2022.3.43f1, WTT-SDK-2022): materials, prefabs (LoddedSkin, TorsoSkin, HeadSkin, LegsView), bundles (LZ4)
  → SPT mod (WTT-CommonLib 3.0.6): bundles.json, db/CustomClothing, db/CustomHeads, server DLL
```

## The user's ground rules (both sides)
- Thorough and correct beats fast. Work from evidence; label hunches and ask about them.
- Keep reports concise.
- Anything the user runs is a **.bat**, as automated as possible.
- **Never delete or overwrite another mod's files.** List them and ask first.
- **Every update bumps the version and adds a CHANGELOG line.**
  - Blender: `VERSION` in `cod2eft_porter.py` = `"version"` in `addon_init.py` (`build_addon.py` refuses a mismatch); `blender/COD2EFT/CHANGELOG.md`.
  - Unity: `EFTToolsVersion.Version`; `unity/EFTAutoPrefabber/CHANGELOG.md`.
  - Tell the user which version to expect (the Blender panel title; Unity logs `[EFT Tools] vX loaded`).

## Current versions (in this repo)
| Side | Version | Status |
|---|---|---|
| COD2EFT | **2.6.2** (2026-09-29) | *Materials: enc=3* (classes + baked gloss curves, default still enc=2); normal style in the PNG tag. Tested headless in Blender 4.4, not yet on the PC |
| EFT Tools | **1.7.2** (2026-09-29) | `enc=3` → neutral values; tag `n=dx` → Flip Green. Checked by reading + a Python mirror of the tag parser; not compiled |
| COD2EFT Inspector (SPT client plugin) | **0.3.0** (2026-09-29) | 0.2.0 verified on the PC (raid, hideout, menu previews, catalog; from_pc/20260929-071416). 0.3.0 adds photo mode (orbit camera, studio lights, turntable) and F12 buttons, compiled with mcs only. |

## How work reaches the PC now
The cloud session pushes to GitHub branch `claude/bold-mayer-11fzxj`. On the PC:
- `SYNC_TO_MY_PC.bat` pulls, backs up, deploys and verifies.
- `SEND_RESULTS_TO_CLAUDE.bat` uploads logs, reports and files changed on the PC into `from_pc/<time>/`.

See the root `README.md`. This replaces the Cowork `device_commit_files` route, which silently wrote stale files.

## The contract between the halves
The source of truth is `blender/COD2EFT/unity/COD2EFT_TEXTURE_SPEC.md` (agreed by both sessions 2026-09-27; uploaded with the first PC send - an earlier note here that it was missing was wrong). The table below summarises it; if they disagree, the spec wins. The audit (below) checked it against the code of both sides.
The material encoding (`enc=2` / `enc=3`, the PNG tag, the Unity values per encoding) is specified in the spec's `enc=3` section and in `docs/MATERIALS_PLAN.md`, "enc=3 contract" (with the measurements behind it).

| Item | Agreed | Code status |
|---|---|---|
| Object names | `<name>_Head/_Upper/_Lower/_Hands` (joined parts, default) | ✅ both sides |
| Material slots | `<name>_<Part>[_<class>]`, parsed from the end | ✅ main + `_alpha` |
| `_skin` class | same atlas as main; Blender produces it **from 2.5.0** | ❌ not started (either side) |
| `_metal`, `_emissive` | reserved, never produced | n/a |
| PNGs | `<slot>_d` (RGB colour, A = COD specular F0), `_n` (OpenGL), `_g` (gloss = smoothness) | ✅ |
| PNG tag | tEXt `Software` = `COD2EFT enc=2` or `enc=3`, plus ` n=dx` for a DirectX normal map, right after IHDR | ✅ 2.6.0 writer / 1.7.0 reader (older Unity reads `n=dx` files as untagged) |
| `enc=3` | pixels carry the look per COD material; Unity uses neutral values (G 1, S 1, vanilla preset of the part) | ✅ both sides, default off (enc=2) until the in-game A/B |
| UVs | UV0 = atlas; UV1 `COD_original_UV` = ignored | ✅ (backlog: drop UV1) |
| FP hands | `<name>_Hands` mesh uses only `<name>_Hands…` slots | ✅ (checked on Kleo 2.4.3) |

**Per-part material values (Unity, `enc=2` textures; `enc=3` uses the neutral values in `docs/MATERIALS_PLAN.md`):**

| Part | `_Glossness` | `_Specularness` | `_ReflectColor` |
|---|---|---|---|
| Upper | 2.4 | 1.0 | 0.87 |
| Lower | 2.0 | 0.75 | 0.61 |
| Head | 2.2 | 0.6 | 0.9 |
| Hands | 2.6 | 0.8 (hunch) | 0.55 |

Measured COD hand-skin gloss (Kleo FP 0.56, BO5 esports 0.49, Park 24_1 FP 0.35) suggests
Hands `_Specularness` ≈ **0.55**. That is a first estimate from 3 characters and isn't applied yet.

## Open work, in order
**Current focus (user, 2026-09-29): material accuracy.** `enc=3` is built: COD2EFT 2.6.0 + EFT Tools 1.7.0 (`docs/MATERIALS_PLAN.md`, "enc=3 contract"). The default stays enc=2 until **the user's in-game A/B** decides. Still open on the material side: vanilla gloves / holsters / visors to measure the leather and glass classes (hunches now), and the labelled precision/recall of the classifier.

**Done and verified (history):**
- Long-path fix verified on real Windows: Park 24_1 (COD2EFT 2.4.4, from_pc/20260928-211853).
- First complete character in game: Park 24_1 (from_pc/20260928-221326). The user's verdict: "looks good".
- Hand-adjust layer works on the PC (2.5.0). 2.5.3 adds *Unparent bones* (the user asked for it).
- Swayback fix confirmed by the user on valeria (2.5.2): "looks better".

**Waiting on the user (PC):**
1. In game on Park 24_1: is there a dark ring at the neck, where head meets top?
2. **Material A/B (redo with 1.7.1):** the first try (from_pc/20260929-002831) showed no difference because of a Unity bug: both prefabs got the enc=2 materials (fixed in 1.7.1). Then convert one character with *Materials* enc=2 and with enc=3 (two output folders or names), run the Unity one-click build for both, and take in-game screenshots next to vanilla. Look at skin first (see the known limits in `docs/MATERIALS_PLAN.md`).
3. Optional: the real `EFT BASIC [Template].blend`. Cloud tests currently use one rebuilt from the FBX.

**Queue, in order.** Each is a separate step. (The material session has pushed: 2.6.0 / 1.7.0.)
1. **Unify the contract.** Move `COD2EFT_TEXTURE_SPEC.md` to `docs/` and update every reference to it: code comments in `cod2eft_textures.py`, `COD2EFT_To_Unity.bat`, the READMEs, the CHANGELOGs' pointers. Remove its "who owns what / separate sessions" wording.
2. ✅ **One source for the per-part material numbers** (2026-09-29: `tools/check_contract.py` fails when Blender's `EFT_PART` / `EFT_NEUTRAL` / `CUTOFF` and Unity's presets / `DefaultCutoff` differ; run it after touching either). Today they are hard-coded twice: `EFT_PART` in `cod2eft_textures.py` (Blender preview) and `EFTMaterialCore.cs` (Unity). Either a shared JSON that both read, or a `tools/` check that fails when they differ. enc=3 added a second set (`EFT_NEUTRAL` vs `Cod2EftNeutralPreset`) that must be covered too.
3. **`_EFT` name suffix.** Batch export writes `<name>_EFT.fbx`, so Unity names bundles `…_eft_top`. The panel export for Park gave `…_top`. Pick one: Unity strips a trailing `_EFT`, or Blender stops adding it. Changing bundle keys breaks existing mods, so ask the user first.
4. ✅ **Regression harness:** `tools/regress.py`, with baselines `tools/baselines/cod2eft_2.6.1.json` (enc2) and `…_2.6.1_enc3.json`. Deterministic: a repeat run is identical. Usage is in CLAUDE.md.
5. **Test data out of git.** `from_pc/` holds GBs of COD exports and bundles, and every clone downloads them. Options: a separate data repo, Git LFS, or a send script that uploads big folders elsewhere. Rewriting history to drop what's already committed needs the user's OK.
6. **Mod Builder:** default hands for tops without their own. Pick a game hands bundle automatically; the user got stuck on this with Park.
7. **Mod Builder:** show "has not been built" before the first build as a note, not an error.
8. **Bundle check after build:** Standard shader left in, missing shaders CAB, empty bundle, no LoddedSkin.
9. **Unity step in batch mode** (a `.bat`: COD model in, SPT mod out). Blockers are listed in the audit below. Best done once the materials settle.
10. **SPT client mod: "COD2EFT Inspector"** (BepInEx plugin, user idea 2026-09-29). **Stage 1 built: v0.1.0**; **0.2.0** adds the read-only outfit catalog (Outfits tab), the first step of the browser (`docs/SPT_INSPECTOR.md`). Next: the user runs `BUILD_SPT_INSPECTOR.bat`, tries it in raid / hideout / menu, and sends `SEND_RESULTS_TO_CLAUDE.bat`; fix from the log. The outfit browser (below, 1) is researched and planned in `docs/SPT_INSPECTOR.md`, not built. Remaining stages:
    1. an outfit/head browser: switch to and preview any top, pants or head in game, sorted and filtered by the mod it comes from vs vanilla;
    2. live material tweaking on the equipped outfit (sliders for `_Glossness` / `_Specularness` / `_ReflectColor` …, side by side with vanilla), with values exported to a JSON that `SEND_RESULTS_TO_CLAUDE.bat` picks up;
    3. ~~a material report per equipped outfit~~ (in 0.1.0);
    4. a fixed screenshot setup (same light, camera and angles, next to a vanilla outfit);
    5. pose tests (crouch, aim, sprint).

    Constraints: it must be compiled on the PC against the user's own SPT/EFT assemblies, through a `.bat`. Never commit game DLLs. Calibrate in raid or hideout lighting, not the menu preview. First check the SPT hub for existing freecam / photo-mode / outfit-preview mods to reuse.
11. **Smaller items:**
    - the neck ring (if confirmed);
    - drop `COD_original_UV` from the FBX;
    - calibration heads A/B/C against a vanilla head;
    - Park 24_1's head wasn't paired with its body (no `head_…` found);
    - ✅ audit leftovers fixed (2.6.2 / 1.7.2): Mod Builder overwrite guard, `install_addon.py` template path encoding, `COD2EFT_To_Unity.bat` PNG glob.

**Superseded:** the `_skin` slot plan (Blender 2.5.0 / Unity 1.7.0 per-class values) is replaced by the enc=3 plan. The version numbers 2.5.x went to other fixes.

## Version compatibility
| Texture tag | Blender (COD2EFT) | Unity (EFT Tools) |
|---|---|---|
| untagged (older COD2EFT, other sources) | < 2.4 | any (generic path: packs `_eft.png`) |
| `COD2EFT enc=2` | ≥ 2.4 | ≥ 1.4.0 |
| `COD2EFT enc=3` (*Materials: enc=3*) | ≥ 2.6.0 | ≥ 1.7.0 (older reads it as enc=2 and applies the per-part values: wrong) |
| ` n=dx` suffix (*Normal maps: DirectX*) | ≥ 2.6.0 | ≥ 1.7.0 flips green; older treats the file as untagged (auto-detects the normal style) |

Sub-mesh names `<name>_<Part>_<label>` (Separate by COD material / Join parts off) need EFT Tools ≥ 1.6.2.

COD2EFT Inspector (`spt_mod/`) is independent of the texture tag: 0.1.0 reads whatever the game loaded. Built for SPT 4.1.6 (EFT client, BepInEx 5, netstandard2.1).

## Facts already established (don't re-derive)
**Game and rendering**
- EFT renders in gamma space, deferred. `Hidden/Internal-DeferredShadingEFT` is Unity's GGX BRDF, gamma variant.
- G-buffer: specular = `_MainTex.a × _Glossness × (SV.x+SV.y·F)/2`, smoothness = `_SpecMap.r × _Specularness`, F = (1−N·V)²/2.
- Shaders:
  - body `p0/Reflective/Bumped Specular SMap_Decal` (stencil 1);
  - hands `…SMap` (stencil 2);
  - hair `p0/Cutout/Bumped Diffuse` (the SDK has no stub; the tools create one);
  - all three are in shaders CAB-56d919bd5479d38f741da52a6beef92f.
- Cubemap: `patron_cubemap_metall_matte`.
- Vanilla medians (393 materials, UV-covered texels only), specular at F=0 / smoothness:
  - Upper 0.052 / 0.24
  - Lower 0.045 / 0.18
  - Head 0.044 / 0.29
  - Hands 0.057 / 0.27
- EFT tops include low-poly third-person hands. The hands bundle is the first-person arms model (Hands_BEAR: 40 bones, bone paths start at `Base HumanPelvis/`).
- Normal maps: EFT's are OpenGL. COD's are DirectX-signed, and COD2EFT converts them.

**SPT**
- Any `_` in a mod GUID stops **all** mods loading.
- Bundle keys are global across mods; a duplicate key fails to load.
- The server bundle cache re-hashes on size or mtime. The 4.1 client doesn't write its own cache. Stale outfits came from empty (~730-byte) bundles or leftover copies in other mods.

**COD exports**
- Park 5_1 and 11_1 exports are incomplete: files with paths of 291+ characters were never written. The fix is to re-export to `C:\COD\`.
- MW2019 split exports deliberately lack the packed `X_n&Y_g` image.

## Missing from the repo (upload if possible)
- The Blender dev harness `COD2EFT\_dev\`:
  - `runall2.py`, `runsel.py`, `cmpall.py`;
  - `ui/uitest4.py`;
  - `mat/codhist.py` + `codhist.json`, `mat/skindet.py`.
  Needed for regressions and for the skin detector.
- Test characters (the `Testing\` folder, COD exports). Too big for git (GitHub caps files at 100 MB), so send single characters as needed.
- `EFT BASIC [Template].blend`. Only the `.fbx` is here (from_pc/20260928-231514); the cloud sessions rebuild the .blend from it (see CLAUDE.md).
- The Unity side's unit tests (parser, materials, bundle reader) and its compile setup (84 Unity module DLLs). Without these, C# can only be checked by reading it.

## Audit (2026-09-29, by reading the code; Blender and Unity were not available to run)
**Fixed in this pass**
- ✅ **Unity 1.6.1: Mod Builder crash.** Picking one of the game's own hands for a top made `Validate()` throw a NullReferenceException (`EFTModBuilderWindow.cs` ~line 363). `Validate` runs from OnGUI and before both build buttons, so the window broke.
- ✅ **Blender 2.4.4: long-path link could make paths longer.** The `%TEMP%\cod2eft_j\…` link (~53+ characters) was used even for short folders such as `C:\COD\kleo` (the tool's own advice). A 250-character file then became about 293 characters and its texture loaded empty.
- ✅ **Blender 2.4.4: leftover junction.** If re-pointing the images failed, the junction stayed in %TEMP%. Some temp cleaners follow junctions and would delete the real export (uncertain).

**Open: contract mismatches**
- ✅ **DirectX normals are flipped twice.** Fixed in 2.6.0 / 1.7.0: the tag says `n=dx` and Unity flips those. Original finding:
  - With COD2EFT's *Normal maps: DirectX* option, green is flipped, but the tag still says `enc=2`.
  - Unity treats every tagged `_n` as OpenGL (`EFTMaterialFixer.cs:218`), so those maps come out inverted.
  - The default (OpenGL) is fine.
  - Fix: write the style into the tag (e.g. `enc=2 n=dx`), or have Unity auto-detect tagged maps. This needs a spec update.
- ❗ **Split outputs don't parse in Unity.**
  - *Join parts* off gives `<name>_Upper_00`, `_01`. The parser reads `00` as a variant, so you get one prefab per piece.
  - *Separate by COD material* gives `<name>_Upper_<label>`. Those names are ignored, or read as a state when the label is `vest`/`rig`/`ar`/`base`.
  - The default joined output is fine.
  - Fix: agree on a piece-naming scheme (e.g. `<piece>_<name>_Upper`, which the parser already groups).

**Open: Unity**
- ✅ (1.7.2) **The "don't overwrite another mod" guard only looked for DLLs** in the mod folder's top level. Pointing the builder at an existing asset-only mod would overwrite its `bundles.json`, `modinfo.json` and `db/*`. It never deletes anything.
- ✅ **Corrupt PNG hang.** A chunk length ≥ 2³¹ made the tag reader loop forever. Fixed in 1.7.0 (a length past the bytes read ends the scan).
- **Batch mode blockers:**
  - `Scan`/`BuildAll` are private window methods that read `Selection`.
  - The hand-off to the mod step uses `EditorApplication.delayCall`, which doesn't fire with `-quit`.
  - `RunPipeline` needs a window and returns void.
  - `Clean(true)` can show a delete dialog.
  - No command-line inputs.
  - Reusable static pieces: `EFTMaterialFixer.Fix`, `EFTBundleBuilder.Build`, `EFTModBuilderCore.*`.

**Open: Blender**
- ✅ (2.6.2) **Non-ASCII template path.** `COD2EFT_Convert.bat` writes `template_path.txt` in the console code page, and `install_addon.py` reads it as UTF-8. A path with e.g. `Ü` aborts the install.
- ✅ (2.6.2) `COD2EFT_To_Unity.bat` copied `<name>_*.png`, so copying `Kleo` also picks up `Kleo_Alt_*` textures. Low.
- Minor points in rarely used paths:
  - the `mklink` fallback doesn't quote a path with `&`;
  - UNC (`\\server\…`) folders skip the long-path check;
  - two Blenders importing the same folder share one link name.
- `python -m pyflakes`: only false positives (Blender annotations) and one unused `import re` (`cod2eft_batch.py`).

**Repo organisation, done**
- Files moved into `blender/COD2EFT/` (mirrors the PC folder, so `build_addon.py` and `install_addon.py` work unchanged), `unity/`, `docs/` and `pc/`.
- `vendor/cod2eft_cast/` was only inside the zip; restored.
- The zip is no longer committed. A rebuilt zip has the same file list as the uploaded one.
- The context dumps were deduplicated. Each held its content two or three times, and "blender side context.txt" actually started with the Unity handoff.
- `blender_path.txt` (per-PC) removed from git.
