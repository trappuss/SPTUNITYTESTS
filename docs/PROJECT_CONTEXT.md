# Project context: COD → EFT (SPT) pipeline, both halves (2026-09-29)

This doc merges the Blender and Unity handoffs of 2026-09-27. The two sessions are now one, so
the user no longer has to relay messages between them.
Details stay in the per-side docs:
- Blender: `blender/COD2EFT/NEXT_SESSION.md`, `README.md`, `BACKLOG.md`
- Unity: `unity/NEXT_SESSION.md`, `unity/README.md`
- The original context dumps are unchanged in `docs/archive/`

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
| COD2EFT | **2.6.0** (2026-09-29) | *Materials: enc=3* (classes + baked gloss curves, default still enc=2); normal style in the PNG tag. Tested headless in Blender 4.4, not yet on the PC |
| EFT Tools | **1.7.0** (2026-09-29) | `enc=3` → neutral values; tag `n=dx` → Flip Green. Checked by reading + a Python mirror of the tag parser; not compiled |

## How work reaches the PC now
The cloud session pushes to GitHub branch `claude/bold-mayer-11fzxj`. On the PC:
- `SYNC_TO_MY_PC.bat` pulls, backs up, deploys and verifies.
- `SEND_RESULTS_TO_CLAUDE.bat` uploads logs, reports and files changed on the PC into `from_pc/<time>/`.

See the root `README.md`. This replaces the Cowork `device_commit_files` route, which silently wrote stale files.

## The contract between the halves
The source of truth was `COD2EFT_TEXTURE_SPEC.md`. **It is not in the repo** (`blender/COD2EFT/unity/`); since 2.6.0 / 1.7.0 the repo's contract for the material encoding is `docs/MATERIALS_PLAN.md`, section "enc=3 contract".
It was **not** found at `COD2EFT\unity\COD2EFT_TEXTURE_SPEC.md` on the PC (first send, 2026-09-28), so its location is unknown. The audit (below) checked it against the code of both sides.

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
**Current focus (user, 2026-09-29): material accuracy.** See `docs/MATERIALS_PLAN.md`. `enc=3` is built (COD2EFT 2.6.0, EFT Tools 1.7.0). **Next: the user's in-game A/B** (one character converted with enc=2 and with enc=3, next to vanilla), which decides the default. Still open: vanilla gloves / holsters / visors to measure the leather and glass classes, and the labelled precision/recall of the classifier.

**Hips/waist twist on curvy characters:** fixed in 2.5.2 (the pelvis section is limited against the waist section). Valeria's waist went from −4.0 to +0.4 cm. Still to check on the PC and in game.

1. ✅ **Long-path fix verified on real Windows** (2026-09-28, COD2EFT 2.4.4, from_pc/20260928-211853).
   - Park 24_1 (paths up to 269 characters) imported through the temp junction and packed 3 long-path images.
   - All 19 materials got textures and the conversion finished Upper + Lower. It used to stop at Upper.
   - All 12 PNGs are tagged `enc=2`, and the atlases show no grey or missing blocks.
   - **Open:** no head was converted (no `head_…` file was paired with the body). Also, 40% of Lower's vertices stick out more than 3 cm beyond EFT's body (up to 10.8 cm).
   - ✅ **First complete character in game** (2026-09-28, from_pc/20260928-221326).
     - Park 24_1 was re-converted in Blender with head and first-person hands, then run through the Unity one-click pipeline.
     - Result: 4 prefabs (top, pants, head, hands), 4 bundles plus the `shaders`/`cubemaps` dependencies, and mod `RCTA_ClothingMod_Test` (`com.wuvgawore.rcta-clothingmod-test`).
     - The user's verdict in game: "looks good". Visible in the screenshot: hair cut-out, sunglasses, decals, belt and holster all render; the pistol sits in the COD thigh holster.
     - **To look at:** a dark ring around the neck where head meets top (possibly COD's texture edge, the normals or occlusion; not investigated). The white shirt also reads a bit flat next to vanilla.
   - Mod Builder gotcha: bundles appear in its list for as long as their prefabs are in `Assets`, even after the source character is deleted. Remove old prefabs, then Rescan + Clean.
2. **User:** re-convert Kleo with *First-person hands* on. The current `EFT_Converted\kleo_empty_.fbx` is hand-edited: its arms are on the Upper slot/atlas and COD2EFT's Hands mesh is missing. The re-conversion unblocks calibration step 2 (tagged textures).
3. **Blender: `_skin` slots.** Probably not needed if enc=3 wins the A/B (the class is baked per COD material, the atlas stays single). 2.6.0's skin-colour rule is a start for the detector.
   - The hard part is the skin detector. It must work on hashed IW names and Cold War names.
   - Hand-label every test material first, then report precision and recall per game and show the user a contact sheet of the misses.
   - Also split `_Hands` into `_Hands_skin`.
4. **Unity 1.7.0:** values per part + class, editable in the Auto Prefabber settings.
   - Skin preset: 0.5/0.5 looked good on the old untagged Kleo textures.
   - Hands `_Specularness` ≈ 0.55.
5. **Unity suggestions, not started:**
   - verify bundles after each build (Standard shader left in, missing shaders CAB, empty bundle, no LoddedSkin);
   - calibration heads A/B/C against a vanilla head in game;
   - default hands for tops without arms.
6. **Parked:** a .bat that runs the whole Unity step in `-batchmode`. See the audit for what blocks it.
7. **Blender backlog:** drop `COD_original_UV` from the FBX.

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
- `COD2EFT_TEXTURE_SPEC.md`: the contract. `SEND_RESULTS_TO_CLAUDE.bat` fetches it.
- The Blender dev harness `COD2EFT\_dev\`:
  - `runall2.py`, `runsel.py`, `cmpall.py`;
  - `ui/uitest4.py`;
  - `mat/codhist.py` + `codhist.json`, `mat/skindet.py`.
  Needed for regressions and for the skin detector.
- Test characters (the `Testing\` folder, COD exports). Too big for git (GitHub caps files at 100 MB), so send single characters as needed.
- `EFT BASIC [Template].blend`, needed to run COD2EFT at all.
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
- ⚠ **The "don't overwrite another mod" guard only looks for DLLs** in the mod folder's top level. Pointing the builder at an existing asset-only mod would overwrite its `bundles.json`, `modinfo.json` and `db/*`. It never deletes anything.
- ✅ **Corrupt PNG hang.** A chunk length ≥ 2³¹ made the tag reader loop forever. Fixed in 1.7.0 (a length past the bytes read ends the scan).
- **Batch mode blockers:**
  - `Scan`/`BuildAll` are private window methods that read `Selection`.
  - The hand-off to the mod step uses `EditorApplication.delayCall`, which doesn't fire with `-quit`.
  - `RunPipeline` needs a window and returns void.
  - `Clean(true)` can show a delete dialog.
  - No command-line inputs.
  - Reusable static pieces: `EFTMaterialFixer.Fix`, `EFTBundleBuilder.Build`, `EFTModBuilderCore.*`.

**Open: Blender**
- ⚠ **Non-ASCII template path.** `COD2EFT_Convert.bat` writes `template_path.txt` in the console code page, and `install_addon.py` reads it as UTF-8. A path with e.g. `Ü` aborts the install.
- `COD2EFT_To_Unity.bat` copies `<name>_*.png`, so copying `Kleo` also picks up `Kleo_Alt_*` textures. Low.
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
