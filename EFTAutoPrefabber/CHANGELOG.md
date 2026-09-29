# EFT Tools changelog

One version number for everything in this folder. The number is shown at the top of the EFT Auto Prefabber and EFT Mod Builder windows, and logged in the Console as `[EFT Tools] vX.Y.Z loaded`.

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
