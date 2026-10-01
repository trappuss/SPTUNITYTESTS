# EFT Tools changelog

One version number for everything in this folder. The number is shown at the top of the EFT Auto Prefabber and EFT Mod Builder windows, and logged in the Console as `[EFT Tools] vX.Y.Z loaded`.

## 1.7.6 — 2026-10-01
- **Scan: clear message for an FBX without skinning.** When a model has meshes but no SkinnedMeshRenderers (an FBX exported without its armature), the log now says so and points to COD2EFT 2.6.9, instead of only "no SkinnedMeshRenderers found". User report: Park 24_1 enc2 FBX scanned as 0 meshes; the FBX had no armature (COD2EFT export bug, fixed in 2.6.9).

## 1.7.5 — 2026-09-30
- **Mod Builder: bundle check after *Build bundles + mod*.** Each bundle it just built is opened in the editor (`AssetBundle.LoadFromFile`, then unloaded) and checked for:
  - an empty bundle (under 2 KB, or no assets);
  - no prefab with a LoddedSkin;
  - a material on the Standard shader;
  - a material with no shader while the bundle doesn't reference the game's shaders bundle (`CAB-56d919bd…`);
  - an empty `_MainTex` / `_BumpMap` / `_SpecMap` slot.
  The log ends with `Bundle check: OK - …` or `Bundle check: FAIL - N problem(s)`, with one line per problem above it. It only reports and never blocks or changes the mod. A bundle whose name is already loaded in the editor is skipped with a note.
- The contract moved to `docs/COD2EFT_TEXTURE_SPEC.md` in the repo (comment in `EFTMaterialCore.cs` updated).
- Not compiled here; checked by reading. The texture-slot test treats an unresolved reference (a texture in another bundle) as fine; that is a hunch about how Unity shows such references in the editor.

## 1.7.4 — 2026-09-30
- **Mod Builder: a top without hands no longer blocks the build** (you got stuck on this with Park). If the top has no matching hands bundle in this mod (`<model>_hands`, or the mod's only hands bundle), the game's **DefaultUsecHands** (`assets/content/hands/usec/usec_hands_skin.bundle`) is picked; DefaultBearHands if the game list has no USEC default. The log says so, and the top shows *Picked automatically … change it above*. Picking any other hands in the list replaces it. If the mod's own hands bundle appears later, the top switches to it by itself.
- **Mod Builder: "has not been built" is a note before the first build.** The periodic check listed every bundle not built yet as an ERROR. For a bundle this mod has never shipped, it is now a note (`… is not built yet - 'Build bundles + mod' builds it`). *Build Mod only* stays disabled until the files exist, and the real build still checks everything. A bundle that was shipped before and whose file is gone is still an error.
- Not compiled here; checked by reading.

## 1.7.3 — 2026-09-29
- **Optional high-quality compression (BC7) for COD2EFT textures** (Materials options, off by default).
  - Off: Unity's default DXT5 for `_d` and DXT1 for `_g`. These are the formats vanilla uses; in all 15 vanilla bundles, `_MainTex` is DXT5, `_SpecMap` DXT1 and `_BumpMap` DXT5.
  - On: a Windows BC7 override on `_d` and `_g`, for smoother gradients (skin, gloss). `_g` doubles in size. Normal maps are left alone.
  - Turning it off again removes only overrides this tool set (marked in the importer), never your own.
  - Whether BC7 looks better in game is a hunch to check.
- Not compiled here; checked by reading.

## 1.7.2 — 2026-09-29
- Mod Builder: a mod folder that already has files but no `modbuilder.json` (so this builder didn't make it) is refused. Before, only a foreign DLL was caught, so an asset-only mod's `bundles.json`, `modinfo.json` and `db` files would have been overwritten. (Audit item.) Your own mod folders have `modbuilder.json` and are not affected.
- Not compiled here; checked by reading.

## 1.7.1 — 2026-09-29
- **Fix: two models with the same material names shared one set of materials.** This happened when both were set up in one run, e.g. the same character converted as enc=2 and enc=3. The extracted materials were remembered by name only, so both prefabs used the first model's materials. This is why the enc=2/enc=3 A/B looked identical: the enc=3 bundle had the enc=2 materials. They are now keyed by model file + material name.
- If a material still points at a same-named texture in another folder, the texture next to its own model now wins.
- **Alpha cutoff default 0.5 → 0.3** (your in-game finding: 0.25–0.35). A saved 0.5 in the window is moved to 0.3 once. Existing cut-out materials still at exactly 0.5 take the current setting on the next Fix.
- Not compiled here; checked by reading.

## 1.7.0 — 2026-09-29
- **COD2EFT `enc=3` textures** (COD2EFT 2.6.0, *Materials: enc=3*) get EFT's neutral clothing values on every part: `_Glossness` 1, `_Specularness` 1, and the vanilla preset's `_SpecVals`, `_DefVals`, `_ReflectColor` for the part. There's no per-part calibration, because the look is baked into the textures per COD material.
  - Heads use the vanilla head preset with G = S = 1. Vanilla heads are hand-tuned (see `EFTMaterialCore.Cod2EftNeutralPreset`).
  - `enc=2` textures behave exactly as before.
- When a material's textures switch between enc=2 and enc=3, its values are re-applied. The material remembers enc=3 in a tag, `COD2EFT_enc`.
- Normal maps tagged `n=dx` (COD2EFT's DirectX option) get Flip Green Channel. Before, every tagged map was treated as OpenGL, so DirectX ones came out inverted.
- Tag reader: a corrupt chunk length (≥ 2³¹) no longer hangs the reader in an endless loop.
- Not compiled here (no Unity in the cloud session). The tag parser was checked in a Python mirror.

## 1.6.2 — 2026-09-29
- Name parser: COD2EFT sub-meshes `<name>_<Part>_<label>` now join their part's prefab. These come from *Separate by COD material* (`mp_milsim_us_sf_1_1_Lower_material_5c0b2a55701ee9c2`) and *Join parts* off (`kleo_Upper_00`). Before, they were ignored, or read as a variant (one prefab per piece) or a state (`…_Upper_mtl_vest`). A one-character variant (`Upper_1`), a state word or a LOD after the part keeps its old meaning.

## 1.6.1 — 2026-09-29
- Mod Builder: choosing one of the game's own hands for a top no longer throws a NullReferenceException in the checks (it broke the window and both build buttons).

## 1.6.0 — 2026-09-27
- Version number shown in both windows.
- Warning plus a Refresh button when Unity has not compiled the newest scripts on disk yet.

## 1.5.0 — 2026-09-27
**Mod Builder:**
- Each mod folder has its own bundle list. A new folder starts empty.
- Bundles that were deleted from the project, and were never built into this mod, are dropped from the list.
- Deleted bundles that this mod did ship are kept under "No longer in the project", with a Forget button.
- The hands dropdown also lists the game's own first-person hands.

## 1.4.0 — 2026-09-27
**COD2EFT 2.4+ textures** (tagged `COD2EFT enc=2`):
- `_d` is used as stored.
- `_n` is treated as OpenGL.
- Cut-out comes from the `_alpha` material slot, with coverage-preserving mip maps.
- Max Size is set to at least the file size.
- `_Hands` materials are set up directly as hands materials.

**Per-part values:** calibrated against 393 vanilla materials. The game's deferred lighting was verified as GGX in gamma space.

## 1.3.0 — 2026-09-27
- EFT game shaders with vanilla values.
- TorsoSkin armor/vest meshes and HeadSkin face-cover meshes.
- Pistol holster on pants.
- LZ4 bundle builder.
- One-click prefabs + bundles + mod.
- Clean button and auto-clean on build.

## 1.2.0
- Material fixer: extracts materials, finds textures, detects the normal-map convention, gloss, alpha.

## 1.1.0
- Mod Builder: bundles.json, WTT clothing and heads, server DLL.

## 1.0.0
- Auto Prefabber.
