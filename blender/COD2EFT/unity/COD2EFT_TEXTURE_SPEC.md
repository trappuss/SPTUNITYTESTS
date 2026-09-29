# COD2EFT → Unity hand-off (texture spec)

This file is the contract between the two tools that work on a character:

- **COD2EFT** (Blender add-on, folder `SPTModdingTools\COD2EFT`): fits the COD model to EFT and writes `<name>_EFT.fbx` + PNG texture sets.
- **EFT Auto Prefabber** (WTT-SDK, `Assets\Editor\EFTAutoPrefabber`): Unity side — materials (its `EFTMaterialFixer`), prefabs, bundles, mod.

Read it before changing either side. If a change touches anything below, update this file in the same step and add a line to *Changes*.

## Who owns what

| | Owner | The other side … |
|---|---|---|
| Everything in `SPTModdingTools\COD2EFT` (add-on, .bat files, README, this spec) | COD2EFT | doesn't edit these files |
| Everything in the WTT-SDK project (`Assets\Editor\EFTAutoPrefabber`, materials, prefabs, bundles) | Auto Prefabber | doesn't edit or add files there |
| **Everything in Unity** (materials: shader choice, values, texture import settings; prefabs; bundles; in-game checks) | **Auto Prefabber** | COD2EFT has no Unity code (its old material script was removed in 2.4.2). |
| What is *in* the PNGs (colour, specular, gloss, normals, cut-outs) | COD2EFT | reads them as described below; asks for changes here rather than re-deriving the data |

## Files (COD2EFT 2.4+)

Per part (`Head`, `Upper`, `Lower`, `Hands`):

| File | Contents |
|---|---|
| `<name>_<Part>_d.png` | RGB = colour (sRGB, COD's occlusion multiplied in). **A = specular reflectance**, see below. |
| `<name>_<Part>_n.png` | Normal map, **OpenGL / Unity convention (green up)**, already converted from COD's DirectX maps. Import as *Normal map*, no green flip. Exception (2.6.0+): with COD2EFT's *Normal maps: DirectX* option the tag ends in ` n=dx`, and then Flip Green Channel is on. |
| `<name>_<Part>_g.png` | COD gloss, greyscale, white = glossy (linear data). |
| `<name>_<Part>_alpha_d/_n/_g.png` | Cut-out set (hair, lashes, brows, beards, fringe, decals): `_d` **A = opacity** (1 = drawn), cut at 0.5. The model uses a separate material slot `<name>_<Part>_alpha` for these faces. |

Material slots in the FBX are named exactly like the sets: `<name>_<Part>` and `<name>_<Part>_alpha`. With surface classes (below) a part can also have `<name>_<Part>_skin`, which uses the part's main set.

### Material slots and surface classes (agreed 2026-09-27)

Slot name = `<name>_<Part>[_<class>]`. `<name>` can contain underscores, so read it from the end: last token a class (if it is one), then the part.

| Slot | Texture set it uses | Produced by COD2EFT |
|---|---|---|
| `<name>_<Part>` | `<name>_<Part>_d/_n/_g` | yes: cloth / gear, and everything not in another class |
| `<name>_<Part>_skin` | `<name>_<Part>_d/_n/_g` (same atlas as the main slot) | **planned, 2.5.0** — not yet. Skin detection is validated on the test characters first |
| `<name>_<Part>_alpha` | `<name>_<Part>_alpha_d/_n/_g` | yes (unchanged) — no class split inside cut-outs |
| `<name>_<Part>_metal` | — | reserved, not produced: metal is per pixel in `_d` alpha, and in COD it mostly shares a material with non-metal (buckles on a strap texture), so a face-level split would catch little |
| `<name>_<Part>_emissive` | — | reserved, not produced: COD2EFT drops COD's emissive maps, and `SMap` / `SMap_Decal` have no emission input (decompiled shader properties) |

- Part = `Head`, `Upper`, `Lower` or `Hands`.
- The split is per face, by COD material: a COD material goes wholly into one class.
- A part may have only its main slot. An unknown class suffix → treat as the main slot.
- **First-person hands:** mesh `<name>_Hands` (after *Separate by COD material*: `<name>_Hands_<material>`), every face on a `<name>_Hands…` slot, texture set `<name>_Hands_d/_n/_g`. Checked on 2.4.3 with MW2 Kleo: all 33467 faces of `mp_kleo_iw9_3_1_Hands` on `mp_kleo_iw9_3_1_Hands`, its UV channel 0 inside the Hands atlas, and the FBX references `mp_kleo_iw9_3_1_Hands_d/_n.png`.
- **UV channels:** channel 0 = the atlas UVs. Channel 1 (`COD_original_UV`) is COD's own UVs, kept so the textures can be converted again; ignore it.
- `Testing\WARZONE 2 - MW2 Female\EFT_Converted\kleo_empty_.fbx` is a hand-edited export: its `kleo_empty_arms` object uses `mp_kleo_iw9_3_1_Upper` with UVs in the Upper atlas, and COD2EFT's `mp_kleo_iw9_3_1_Hands` object isn't in the file. Converting again with *First-person hands* on gives the right one.

**Tag:** every PNG carries a PNG `tEXt` chunk `Software` = `COD2EFT enc=2`, right after the header (first 4 KB of the file). No tag = older COD2EFT (≤ 2.3.x), whose `_d` alpha means something else (see *Changes*).
Since 2.6.0 the tag is `COD2EFT enc=<N>[ n=dx]`: `enc=3` with *Materials: enc=3* (see *Encoding 3*), and ` n=dx` when the normal map is DirectX style (no `n=` = OpenGL). Unity 1.7.0 reads both parts; older Unity reads a tag with ` n=dx` as untagged.

### Encoding 3 (COD2EFT 2.6.0 ↔ EFT Tools 1.7.0)
The look is baked into the pixels per COD material, so Unity uses one neutral set of values. Full details and the measurements behind it are in `docs/MATERIALS_PLAN.md`, "enc=3 contract".
- `_d` A = COD F0 / (`_SpecVals.x` / 2), i.e. ÷ 0.55, or ÷ 0.5 on the head, cut at 1. The G-buffer specular at F=0 then equals COD's F0.
- `_g` = target EFT smoothness: COD gloss through the quantile curve of the material's class (cloth / skin / leather-rubber-plastic / metal / glass), blended per pixel towards the metal curve by the metal share. Cut-out sets are unchanged.
- **Unity values, every part:** `_Glossness` 1, `_Specularness` 1, plus the vanilla preset's `_SpecVals`, `_DefVals`, `_ReflectColor` for the part (`EFTMaterialCore.Cod2EftNeutralPreset`, the same numbers as `EFT_NEUTRAL` in `cod2eft_textures.py`). Heads use the vanilla head preset with G = S = 1: vanilla heads are hand-tuned, so the head's skin is baked to the measured vanilla head smoothness instead.
- Unity marks a material set up for enc=3 with the material tag `COD2EFT_enc` = `3`. Switching the textures between enc=2 and enc=3 re-applies the values.

### `_d` alpha (encoding 2) — what the value is

COD's own specular reflectance **F0**, as COD stores it, × the "Specular strength" option (default 1.0). It has **not** been scaled for EFT's shader.

| Surface | `_d` alpha |
|---|---|
| Non-metal with COD reflectance (IW colour-alpha ≤ 0.1, Cold War spec map) | that value (cloth on the test characters 0.004–0.09) |
| Non-metal without one (skin, eyes, teeth, colour maps without alpha, tint masks) | 0.04 |
| Metal (IW metal mask) | 0.1 + share × colour luminance, up to ~1 |
| everything | × COD's occlusion (option "AO into specular", on by default) |

So it is a **per-pixel specular mask with real material meaning**, not derived from the gloss map. Where a tool needs EFT-like numbers, map it (a scale, or a curve) instead of replacing it with a gloss-based mask — a gloss-based mask brings back the "wet skin" (COD skin gloss is 0.5–0.56: 0.4 × 0.5 + 0.03 = 0.23, about 6× the skin value).

### `_g`

COD gloss as stored (Cold War roughness already inverted). Medians on the test characters: cloth 0.24, skin 0.48–0.56. EFT's own `_SpecMap` × `_Specularness`: clothes 0.22, heads 0.30 (422 materials).

## Facts from the game files (SPT 4.1, checked 2026-09-27)

- `globalgamemanagers`: `m_ActiveColorSpace = 0` → **Gamma** colour space. `GraphicsSettings.m_Deferred` mode 2 → EFT uses its **own deferred lighting shader**, not Unity's built-in one.
- Character shaders (decompiled in the WTT-SDK's `Shader Assets`), deferred pass: G-buffer specular = `_MainTex.a × _Glossness × (_SpecVals.x + _SpecVals.y × F) / 2 × _SpecColor`, G-buffer smoothness = `_SpecMap.r × _Specularness`, colour = `_MainTex.rgb × (_DefVals.x + _DefVals.y × F)`, F = (1 − N·V)² / 2. Forward pass: Blinn-Phong with exponent `_SpecMap.r × _Specularness × 128`.
- 268 of 282 character materials: `p0/Reflective/Bumped Specular SMap_Decal`, `_StencilType` 1. First-person hands: the main material of 95 of 103 uses `p0/Reflective/Bumped Specular SMap`, `_StencilType` 2. Hair: `p0/Cutout/Bumped Diffuse` (3 materials).
- EFT's own normal maps read as OpenGL (both tools found this independently).

## Open — needs a check in the game

- **How EFT's custom deferred shader lights the G-buffer** (does smoothness drive GGX, or a Blinn-Phong exponent like the forward pass?). Both tools' shader values rest on an assumption until this is known. (COD2EFT's removed Unity script had assumed Unity's standard deferred lighting in linear space — wrong at least on the colour space.)
  - **Answered 2026-09-27 (Auto Prefabber):** decompiled `Hidden/Internal-DeferredShadingEFT` (globalgamemanagers.assets path 18, the shader `m_Deferred` points at). It is Unity's standard BRDF1 — GGX distribution, Smith-joint visibility, Disney diffuse, Schlick Fresnel on the G-buffer specular colour — in its **gamma-space** form (specular term square-rooted), plus an EFT special case for a thermal-style light. Smoothness = G-buffer alpha, perceptual roughness = 1 − smoothness, roughness = that². Deferred reflections (`Hidden/Internal-DeferredReflections`) are Unity's standard ones (mip = r·(1.7 − 0.7r)·6). So `_SpecMap.r × _Specularness` is **GGX smoothness**; the ×128 Blinn-Phong exponent only applies to forward-rendered objects.
- A side-by-side in the game: one vanilla head and one converted head, same scene, to calibrate the `_d` alpha / `_g` mapping.

## Unity side (Auto Prefabber) — how the textures are read

Applies to tagged PNGs (`COD2EFT enc=2`). Untagged files keep the Auto Prefabber's generic path, and files named like COD2EFT output get a log note asking for a re-convert.

- `_d` → `_MainTex` **as stored**: no rebuilt alpha and no `_eft.png`. Alpha Is Transparency is off.
- `_n` → Normal map, Flip Green off. No auto-detection.
- `_g` → `_SpecMap` as stored.
- Cut-out = the material slot name ends in `_alpha`. Name keywords are not used for tagged sets. Shader `p0/Cutout/Bumped Diffuse`, `_Cutoff` 0.5. The `_alpha_d` import has Mip Maps Preserve Coverage on and Alpha Cutoff 0.5.
- Max Size is raised to at least the file's own size on `_d`, `_n` and `_g`.
- A material used only by hands meshes (`<name>_Hands`) is set up directly as a hands material: `p0/Reflective/Bumped Specular SMap`, `_StencilType` 2. It is not a variant of Upper's material.

**Chosen values (calibration step 1: statistical).**

Vanilla targets were measured over 393 vanilla SMap/SMap_Decal materials, counting only texels the meshes' UVs cover. COD inputs are dielectric F0 0.04 and gloss medians cloth 0.24 / skin ≈ 0.5. Each value maps a COD input onto the vanilla median for that part:

- `_Glossness` = vanilla spec ÷ (0.04 × SpecVals.x / 2)
- `_ReflectColor` = vanilla reflection ÷ (0.04 × SpecVals.x / 2)
- `_Specularness` = vanilla smoothness ÷ COD gloss

| Part | Vanilla spec at F=0 (a·Gl·SV.x/2) | Vanilla smoothness | Vanilla reflection (a·RC·SV.x/2) | `_Glossness` | `_Specularness` | `_ReflectColor` | `_SpecVals` | `_DefVals` |
|---|---|---|---|---|---|---|---|---|
| Upper | 0.052 | 0.24 | 0.019 | 2.4 | 1.0 | 0.87 | (1.1, 2) | (0.85, 0.7) |
| Lower | 0.045 | 0.18 | 0.013 | 2.0 | 0.75 | 0.61 | (1.1, 2) | (0.85, 0.7) |
| Head | 0.044 | 0.29 | 0.018 | 2.2 | 0.6 | 0.90 | (1.0, 3) | (0.8, 1.0) |
| Hands | 0.057 | 0.27 | 0.012 | 2.6 | 0.8 (hunch) | 0.55 | (1.1, 2) | (0.8, 0.7) |

- The hands `_Specularness` is a hunch: no COD hands gloss has been measured yet.
- Result for COD skin (F0 0.04, gloss 0.5): G-buffer specular 0.044, smoothness 0.30, the vanilla head medians. Before this, a gloss-based mask gave ≈ 0.12 specular and 0.5 smoothness.
- The Blender preview can use the same maths, with the deferred path above:
  - G-buffer specular colour = `_d.a × _Glossness × (SV.x + SV.y·F)/2`
  - smoothness = `_g × _Specularness`
  - F = (1 − N·V)²/2
  - lit with GGX in gamma space

**Calibration step 2 (still open):** the in-game side-by-side, vanilla head next to the converted Kleo head (2.4.2). It needs Kleo re-converted with 2.4.2; the Kleo PNGs in `Testing\WARZONE 2 - MW2 Female\EFT_Converted` are untagged. Values are adjusted there if needed, and this table is updated.

## Changes

- 2026-09-29 COD2EFT 2.6.0 / EFT Tools 1.7.0: **encoding 3** (optional, default still enc=2 until the in-game A/B), see *Encoding 3*. The tag gains ` n=dx` for DirectX normal maps; Unity 1.7.0 flips those. This fixes the audit's double flip. enc=2 output is unchanged (byte-identical PNGs on the 4 test characters).
- 2026-09-27 COD2EFT: slot naming with surface classes agreed (Auto Prefabber's proposal, *Material slots and surface classes*). COD2EFT produces `_skin` from 2.5.0; `_metal` and `_emissive` are reserved, not produced.
- 2026-09-27 COD2EFT 2.4.3: PNGs unchanged. The Blender preview now uses the table's values and the deferred maths above (approximate: Blender lights in linear space). Windows paths of 260+ characters no longer lose textures. **COD hand-skin gloss measured** (median over UV-covered texels, as written to `_g`): MW2 Kleo first-person 0.56, BO5 esports female hands 0.49, Park 24_1 (Cold War) first-person 0.35. Vanilla hands smoothness 0.27 ÷ these = 0.48 / 0.55 / 0.77, so `_Specularness` ≈ 0.55 fits the middle one; 0.8 fits only the Park one. Only 3 characters had a hand-skin gloss map, so treat it as a first estimate. Gloves on the same hands span 0.21–0.94.
- 2026-09-27 Auto Prefabber: reads tagged enc=2 sets as described in *Unity side*. `_d` is used as stored, `_n` is taken as OpenGL, cut-out comes from the `_alpha` slot, Max Size is at least the file size, and `_Hands` materials are set up directly. Chosen values are in the table above. Deferred lighting was checked (GGX, gamma), see *Open*.
- 2026-09-27 COD2EFT 2.4.2: COD2EFT's Unity script removed - all Unity work is the Auto Prefabber's.
- 2026-09-27 COD2EFT 2.4.1: this file. COD2EFT's Unity script → fallback only (auto setup off, not installed by the .bat).
- 2026-09-27 COD2EFT 2.4.0: encoding 2 (tagged PNGs). `_d` alpha = COD reflectance × 1.0 (was × 1.3); skin, tint masks and colour maps without alpha are no longer read as metal (before, skin alpha 0.2–0.3); decals moved to the `_alpha` sets; MW2019 exports get normal/gloss maps.
- ≤ 2.3.x (untagged): `_d` alpha = COD reflectance × 1.3, with skin often wrongly read as metal (0.2–0.5).
