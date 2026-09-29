# Unity-side session handoff (EFT Tools) — 2026-09-27

For whichever model or session continues the Unity side of the COD→EFT pipeline. The Blender side is a separate COD2EFT session; the user relays messages between the two.

## Ground rules (the user's)
- Be thorough and correct rather than fast. Work from evidence; mark hunches as hunches and ask.
- Keep reports concise.
- Anything the user has to run should be a .bat, as automated as possible.
- Never delete or overwrite another mod's files. List the exact files and ask first.
- **Every update bumps `EFTToolsVersion.Version`** and adds a line to `CHANGELOG.md`. Tell the user which version to expect.

## Where things are
| What | Path on the user's PC | In the device VM (`device_bash`) |
|---|---|---|
| Unity project (WTT-SDK-2022) | `C:\Users\notso\Desktop\RIP\EFT2\Tools\WTT-SDK-2022` | `~/mnt/WTT-SDK-2022` |
| Tool scripts | `...\Assets\Editor\EFTAutoPrefabber\` | same, under the mount |
| SPT game | `G:\G Games\SPT4.1\SPT4.1 Game` | `~/mnt/SPT4.1/SPT4.1 Game` |
| COD2EFT (Blender side, not ours) + contract | `C:\Users\notso\Downloads\Claude Current\SPTModdingTools\COD2EFT\unity\COD2EFT_TEXTURE_SPEC.md` | `~/mnt/SPTModdingTools` |
| Unity Editor.log | `C:\Users\notso\AppData\Local\Unity\Editor\Editor.log` | `~/mnt/Editor/Editor.log` |

- Cloud working copies of the scripts: `/home/claude/eftap/*.cs`.
- Compile check: `/tmp/cc`, run `dotnet build` with `PATH=/opt/dotnet:$PATH`. It references all 84 Unity module DLLs, the SDK's Assembly-CSharp and Unity.AssetBundleBrowser.Editor.
- Unit tests: `/tmp/ct` (parser), `/tmp/mt` (materials), `/tmp/rb` (bundle reader).
- UnityPy 1.25 is installed in the device VM; use it for game-file research.
- Background processes do not survive between `device_bash` calls. Keep each job under 170 s and make it resumable.

## CRITICAL: copying files to the user's PC
- `device_commit_files` **silently wrote stale content** when the same `stagedPath` was reused. That is why the updates before v1.6.0 never landed.
- Always copy to a **new** folder, e.g. `/mnt/user-data/outputs/v5/`, then commit from there.
- **Verify with md5sum** on the device afterwards.
- To check what Unity actually compiled, search `Library/ScriptAssemblies/Assembly-CSharp-Editor.dll`:
  - string literals are UTF-16, e.g. the version `"1.6.0"`;
  - method names are ASCII.
- Unity only recompiles when its window is focused. Check `~/mnt/Editor/Editor.log` for `error CS` and for `[EFT Tools] vX loaded`.

## Current version: v1.6.0 (compiled and loaded, checked in Editor.log)
Files:
- `EFTAutoPrefabCore.cs`: name parser (parts, variants, states `_armor`/`_vest`/`_facecover`, `.001`), bone mapping.
- `EFTAutoPrefabberWindow.cs`: builds prefabs (TorsoSkin, HeadSkin, LegsView holster, bone remap, hands materials) and runs the one-click pipeline.
- `EFTMaterialCore.cs`: presets, COD2EFT tag detection, calibrated values.
- `EFTMaterialFixer.cs`:
  - EFT shaders through the SDK imposter stubs; creates the cutout stub once;
  - COD2EFT enc=2 handling;
  - generic `_eft.png` packing for other textures;
  - hands variants.
- `EFTBundleBuilder.cs`: builds only the chosen bundles (LZ4) through ImposterBuilder.
- `EFTModBuilderCore.cs`, `EFTModBuilderWindow.cs`:
  - builds the SPT/WTT mod;
  - one bundle list per mod folder;
  - Clean;
  - game hands list;
  - `RunPipeline`.
- `EFTLzma.cs`, `EFTSkeletonData.cs`, `EFTAutoModTemplate.bytes` (server DLL template).
- `EFTToolsVersion.cs`, `CHANGELOG.md`.

## Facts found in the game files (don't re-derive)
- **Colour space:** Gamma.
- **Deferred shader:** `Hidden/Internal-DeferredShadingEFT` is Unity BRDF1 (GGX), gamma-space variant.
- **Character shader G-buffer (deferred pass):**
  - specular colour = `_MainTex.a × _Glossness × (SV.x + SV.y·F)/2 × _SpecColor`;
  - smoothness = `_SpecMap.r × _Specularness`;
  - F = (1 − N·V)²/2.
- **Shaders by part:**
  - body: `p0/Reflective/Bumped Specular SMap_Decal`, pathID 5560642222599436764, `_StencilType` 1;
  - hands: `p0/Reflective/Bumped Specular SMap`, pathID 6014991791773097075, `_StencilType` 2;
  - hair: `p0/Cutout/Bumped Diffuse`, pathID -5942632167267530218.
  - All three live in shaders CAB-56d919bd5479d38f741da52a6beef92f.
- **Cubemap:** `patron_cubemap_metall_matte`.
- **Vanilla medians** (393 materials, UV-covered pixels only):

| Part | Specular at F=0 | Smoothness |
|---|---|---|
| Upper | 0.052 | 0.24 |
| Lower | 0.045 | 0.18 |
| Head | 0.044 | 0.29 |
| Hands | 0.057 | 0.27 |

- **COD2EFT calibrated values:**

| Part | `_Glossness` | `_Specularness` | `_ReflectColor` |
|---|---|---|---|
| Upper | 2.4 | 1.0 | 0.87 |
| Lower | 2.0 | 0.75 | 0.61 |
| Head | 2.2 | 0.6 | 0.9 |
| Hands | 2.6 | 0.8 (hunch) | 0.55 |

- **SPT 4.1.6 caches:**
  - server bundleHashCache re-hashes when a file's size or mtime changes;
  - the client cache folder isn't written by the 4.1 client;
  - the real causes of stale outfits were empty bundles and leftover copies.
- **Mod GUID:** any `_` in a mod GUID stops all mods loading.

## Open / next
1. **Surface-class slots — AGREED by the Blender side (COD2EFT 2.4.3, 2026-09-27).** Full contract in the spec, section *Material slots and surface classes*. Summary:
   - `<name>_<Part>[_<class>]`, Part ∈ Head/Upper/Lower/Hands; parse from the end (`<name>` has underscores);
   - `_skin` uses the main slot's atlas; COD2EFT produces it **from 2.5.0 (not yet)** — until then only main + `_alpha` appear; a missing `_skin` or unknown suffix = main slot;
   - `_metal` and `_emissive` are **reserved, never produced** (metal is per-pixel in `_d` alpha; no emissive data, and SMap/SMap_Decal have no emission input);
   - `_alpha` unchanged; FP hands mesh `<name>_Hands` uses only `<name>_Hands…` slots + `<name>_Hands_*` set (checked on Kleo 2.4.3);
   - FBX UV0 = atlas, UV1 `COD_original_UV` = ignore.

   Implement v1.7.0 against that: values per part + class, editable in the Auto Prefabber settings; skin preset (the user found 0.5/0.5 looks good on the OLD untagged Kleo textures). Emissive research no longer needed.
   - **Hands `_Specularness`:** COD hand-skin gloss measured (median, UV-covered, as in `_g`): Kleo FP 0.56, BO5 esports hands 0.49, Park 24_1 FP 0.35 → vanilla 0.27 ÷ these = 0.48 / 0.55 / 0.77. ≈ 0.55 fits the middle; 0.8 fits only Park. First estimate (3 characters).
2. **Kleo — cause confirmed.** `Testing\WARZONE 2 - MW2 Female\EFT_Converted\kleo_empty_.fbx` is a hand-edited export: its `kleo_empty_arms` object is on `mp_kleo_iw9_3_1_Upper` with UVs in the Upper atlas, and COD2EFT's `…_Hands` mesh isn't in it. The user re-converts Kleo with COD2EFT 2.4.3 + *First-person hands* on (all 33467 hand faces then on `mp_kleo_iw9_3_1_Hands`, UV0 in the Hands atlas). That also gives tagged textures for calibration step 2.
3. **Suggestions not started:**
   - verify bundles after each build (Standard shader left in, missing shaders CAB, empty bundle, no LoddedSkin);
   - calibration heads A/B/C;
   - default hands for tops without arms.
4. **Parked:** a .bat that runs the pipeline in batch mode.

