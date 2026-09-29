# Material accuracy: COD → Blender → Unity → EFT (plan, 2026-09-29)

## Why materials are hit or miss today
EFT's character shaders take **per-pixel** data from the textures and **per-material** numbers from Unity:

| Per pixel (textures) | Per material (Unity numbers) |
|---|---|
| `_MainTex.rgb` colour | `_Glossness` (specular gain) |
| `_MainTex.a` specular + reflection mask | `_Specularness` (smoothness gain) |
| `_SpecMap.r` smoothness | `_ReflectColor`, `_DefVals`, `_SpecVals` |

Specular = `_MainTex.a × _Glossness × (SpecVals.x + SpecVals.y·F)/2`, and smoothness = `_SpecMap.r × _Specularness`.

COD2EFT puts **all** of a part's COD materials into one atlas, and so into one Unity material. Unity then applies **one set of numbers per part**, calibrated so that the *median* cloth matches vanilla EFT. Anything far from that median comes out wrong:
- skin, leather and rubber are treated like cloth;
- metal gets a cloth gain on top of a high F0, so it blows out;
- glass has no transparency in these shaders.

Splitting the atlas into one Unity material per surface class (the `_skin` slot plan) would help, but it only moves the problem. Every class then needs hand-tuned numbers in Unity, a class-detection contract between the two sides, and more draw calls.

## Measured: what vanilla EFT does (15 bundles, from_pc/20260928-222836, 2026-09-29)
Per-material numbers and texel statistics are in `docs/vanilla_material_stats.json`. Only texels the meshes' UVs cover are counted. "eff" = what the G-buffer gets at F=0: specular `a·G·SpecVals.x/2`, smoothness `SpecMap.r·S`.

- **Clothing uses neutral numbers: `_Glossness` = 1.0 on all 12 clothing materials, `_Specularness` 1.0–1.27.** EFT bakes the whole look into the pixels, so this plan is how EFT itself works.
- **Heads and bodies are tuned by hand** and don't follow a pattern:
  - USEC head: G 3.0, S 4.37;
  - Wild head: G 2.39, S 1.5;
  - Wild body: G 3.94, S 0.17.
- **One material holds many surfaces.** Inside one clothing texture, smoothness spans roughly 0.1 → 0.45 (10th → 90th percentile) and specular 0.02 → 0.10.

| Vanilla (median of materials) | specular eff | smoothness eff |
|---|---|---|
| Tops (7) | 0.02–0.08, median ≈ 0.03 | 0.06–0.54, median ≈ 0.17 |
| Pants (6) | 0.02–0.11, median ≈ 0.045 | 0.15–0.25, median ≈ 0.16 |
| Heads (2) | 0.03 / 0.11 | 0.26 / 0.37 |
| Wild body skin | 0.13 | 0.02 (very matte) |
| Texels with `a` > 0.5 (metal-like; ≤ 1.5 % of any material) | – | 0.5–0.7 |

The skin-tone split inside tops and pants is unreliable: tan or brown fabric passes the colour rule. Use it only on heads and bodies.

**Consequence for `enc=3`:** use EFT's own clothing numbers, not G₀ = 2. That means `_Glossness` 1.0, `_Specularness` 1.0, `_SpecVals` (1.1, 2), and `_DefVals` / `_ReflectColor` at the vanilla medians. A dielectric's `_d.a` is then 0.04 / 0.55 ≈ 0.073, about 19 steps of 8 bits. That's more precision than the G₀ = 2 estimate below.

## Measured: the COD side (174 materials, 11 models: Kleo MW2, sunflower_base BO7, MW4 beta male)
Data: `docs/cod_material_survey.json`. Script: `tools/cod_survey.py`. Values are what COD2EFT writes today, sampled on the faces that use each material.

- **Specular is already fine:** almost every non-metal comes out at F0 ≈ 0.04. At neutral `_Glossness` 1 that gives an effective specular of 0.022. Vanilla cloth median is 0.03–0.045, so it's close.
- **Gloss is the real gap.** COD gloss has an area-weighted median of **0.37**, and **0.48** on skin-coloured materials. Vanilla smoothness median is **0.16–0.17** for cloth and 0.26–0.37 for heads. So COD is about 2× glossier, and it varies strongly per material: 0.08 up to 0.98.

That's why a single per-part `_Specularness` in Unity can't fix it. The gloss has to be remapped per material in Blender, with a quantile map from each class's COD distribution to the vanilla one.

Bugs found by this survey and fixed in 2.5.1: `m_…` materials losing their normal/gloss maps, and Kleo's first-person sleeves coming out chrome.

## The fix: bake the look into the pixels, keep Unity's numbers fixed
The per-part numbers are only gains, so the same result can be written straight into the textures, per pixel and per COD material:

- **Unity:** (superseded by the measurements above and the "enc=3 contract" below: G₀ = 1) for textures tagged **`COD2EFT enc=3`**, it uses one fixed, neutral set of numbers for every part: `_Glossness` G₀ = 2.0, `_Specularness` 1.0, fixed `_ReflectColor`, `_DefVals`, `_SpecVals`. Unity no longer needs per-part or per-class presets, and the atlas can stay single.
- **Blender** writes, for every texel:
  - `_d.a = spec_eft / (G₀ · k₀)`, where k₀ = (SpecVals.x)/2 at F=0 and `spec_eft` is the target EFT specular;
  - `_g = smooth_eft`, the target EFT smoothness.

  Both targets come from the COD values through a **transfer curve chosen per COD material class**.
- **Blender's preview** already uses EFT's maths, so with fixed numbers it becomes a true preview. What you see in Blender is what the game shows, apart from EFT's gamma-space lighting.

### Material classes
Each COD material is classified from data COD2EFT already reads: the colour-alpha behaviour (metal mask, "in between", none), the metal fraction, gloss and specular statistics, the cut-out test, the tint, and name words.

| Class | Detected by (to validate) | Transfer |
|---|---|---|
| cloth (default) | everything else | COD gloss → cloth curve fitted to vanilla upper/lower medians |
| skin | IW "alpha in between" class, skin-tone colour, NOG wrinkle maps, names (`skin`, `head`, `body_`, `arm`, `hand`) | skin curve fitted to vanilla head/hands (smoothness ≈ 0.27–0.29) |
| leather / rubber / plastic | gloss band + low metal + names (`glove`, `leather`, `boot`, `holster`, `plastic`) | own curve (to measure on vanilla gloves and holsters) |
| metal | metal-mask fraction | keep COD F0 per pixel; colour kept in albedo (EFT's specular has no colour) |
| glass / lens | names (`glass`, `lens`, `visor`, `goggle`), COD glass technique | opaque tinted, high smoothness; the shaders have no transparency |
| hair / cut-out | existing cut-out test | cut-out set; the shader is diffuse only |

Every class, and every material's class, is printed in the report. A per-material override (a list in the panel) lets you correct a wrong class in one click. The override is stored in the .blend and in a JSON file, so batch runs use it too.

### Why this is autonomous
- The curves are **fitted once**, from measurements. On the EFT side: vanilla texel statistics per class from the game bundles; the Unity side already measured 393 materials per part, and this repeats that per class. On the COD side: the existing `codhist` statistics per material.
- They are not hand-tuned per character.
- New characters only need the classifier. Mistakes show in the report and in the Blender preview, before Unity.

### Contract change
- `enc=3` is a new tag. `enc=2` files keep working exactly as now.
- The spec gains an "enc=3" section with G₀ and the fixed numbers.
- Unity gains one branch: `enc=3` → neutral values.
- 8-bit precision: with G₀ = 2 a dielectric's `_d.a` lands around 0.04–0.05, about 11–13 steps of 8 bits. That's fine for a mask that EFT stores in 8 bits itself. To check on skin for banding.

### Also: "no atlas" option
This is still useful for another reason: texture resolution on big characters. With `enc=3` the atlas no longer hurts the materials, so no-atlas becomes a pure size/quality choice (one texture set per COD material, several materials per mesh, like EFT's hands). It's a low priority.

## Steps
1. **Measure (needs data).**
   - Vanilla per-class statistics. Needed: 10–20 vanilla bundles (gloves, holsters, a helmet visor, heads) from `SPT4.1 Game\EscapeFromTarkov_Data\StreamingAssets\Windows\assets\content\characters\…`.
   - COD statistics on the test set: the characters whose materials look wrong in game, with screenshots that name which part looks wrong.
2. **Classifier:** label every material of the test characters by hand, then measure precision and recall per game. That's the step the old handoff planned for skin, now covering all classes.
3. ✅ **Blender 2.6.0:** classes, the `enc=3` bake, report lines and per-material overrides (see "enc=3 contract" below; tested headless, not on the PC).
4. ✅ **Unity 1.7.0:** the `enc=3` branch with neutral values (not compiled here).
5. **In-game A/B:** the same character with `enc=2` and with `enc=3`, next to vanilla.

## enc=3 contract (COD2EFT 2.6.0 ↔ EFT Tools 1.7.0, 2026-09-29)
Built from the measurements above. **Default stays `enc=2`** until the in-game A/B decides.

**PNG tag** (tEXt `Software`, right after IHDR): `COD2EFT enc=<N>[ n=dx]`.
- `enc=2`: as before. `enc=3`: this section.
- ` n=dx`: the `_n` map is DirectX style (green down). No `n=` means OpenGL. This fixes the audit's double flip: Unity 1.7.0 sets Flip Green Channel on tagged `n=dx` maps. Default OpenGL output is byte-identical to 2.5.2.
- Unity < 1.7.0 can't parse `enc=2 n=dx` and treats it as untagged (auto-detects the normal style). It reads plain `enc=3` as enc=2 values, so update both sides together.

**Unity values for `enc=3`** (every part, no calibration): `_Glossness` 1, `_Specularness` 1, plus the vanilla preset of the part for `_SpecVals`, `_DefVals`, `_ReflectColor`, `_Temperature` (`EFTMaterialCore.Cod2EftNeutralPreset`):

| Part | `_SpecVals` | `_DefVals` | `_ReflectColor` |
|---|---|---|---|
| Upper | 1.1, 2 | 0.85, 0.7 | 0.358 |
| Lower | 1.1, 2 | 0.85, 0.7 | 0.255 |
| Head | 1.0, 3 | 0.8, 1.0 | 0.302 |
| Hands | 1.1, 2 | 0.8, 0.7 | 0.198 |

- **Heads** keep the vanilla head preset with G = S = 1. Vanilla heads are hand-tuned and follow no pattern (USEC G 3.0 / S 4.37, Wild G 2.39 / S 1.5). So COD2EFT bakes head skin to the measured vanilla head smoothness for these exact values.
- The Unity material remembers the encoding in a material tag, `COD2EFT_enc` = "3" (enc=2 materials get no tag). Switching the textures between enc=2 and enc=3 therefore re-applies the values. Otherwise user edits are kept, as before.
- Blender's preview uses the same numbers (`EFT_NEUTRAL` in `cod2eft_textures.py`). Keep the two in step.

**What Blender writes for `enc=3`** (per COD material, before packing the atlas):
- `_d.a` = COD F0 / (`_SpecVals.x`/2): ÷0.55, or ÷0.5 on the head. COD F0 already includes AO and the metal share, exactly as in enc=2. So the G-buffer specular at F=0 equals COD's F0, e.g. 0.04 for a dielectric. Above F0 = 0.55 (bright metal) it's cut at 1, and the report gives the share.
- `_g` = gloss curve of the material's class, applied per pixel and blended towards the metal curve by the per-pixel metal share. The curves are quantile maps fitted by `tools/fit_gloss_curves.py` → `docs/gloss_curves.json`. They use the 5–95 % quantiles, (0,0) at the bottom, and slope 1 above the 95th.

| Class | Detected by (first match wins) | COD gloss median → EFT smoothness median | Vanilla target |
|---|---|---|---|
| hair / cut-out | the existing cut-out test (the `_alpha` set) | unchanged (the shader is diffuse only) | – |
| glass / lens | name words `glass lens lenses visor goggle(s)` | smoothness 0.85, colour ×0.3 (**hunches**) | none measured |
| metal | > 50 % of the faces' samples have metal share > 0.5 | 0.69 → 0.67 | texels `a` > 0.5 in 2 pants |
| leather / rubber / plastic | name words `leather glove(s) boot(s) shoe(s) holster plastic rubber(band) strap(s) ziptie handcuff(s) sole(s)`, or metal < 5 % and median gloss ≥ 0.6 (**hunch**) | 0.65 → 0.27 | **hunch:** the glossier half of vanilla clothing (no vanilla gloves or holsters measured) |
| skin | name words `skin arm(s) hand(s) viewarm(s) teeth mouth lips tongue`, or metal < 20 % and a skin-tone mean colour (hue 5–28°, saturation 0.15–0.6, max ≥ 0.3) | 0.47 → 0.32 | the 2 vanilla heads, all covered texels |
| cloth | everything else | 0.34 → 0.17 | 12 vanilla clothing materials |

- **Report:** one line per COD material, e.g. `class xmaterial_57b9…: skin (skin colour), gloss median 0.56 -> smoothness 0.46`.
- **Overrides:** the panel's *Material classes* list is filled by each enc=3 run and saved in the .blend. Change a class there, then press Convert Textures. Batch takes `--class-overrides FILE.json` (`{"COD material name": "skin"}`), and the panel's Batch writes that file from the list. A cut-out can't be overridden.
- **Batch flag:** `--material-mode enc3|enc2`.

**Classifier check** (2.6.0, 174 test materials, labelled by eye on a contact sheet; the plan's full precision/recall step is still open):
- Skin: 22 unique materials classed as skin (28 survey rows, counting LOD and shared copies). Correct: faces, torsos, arms, mouth, teeth, eyes. One miss: a dark-brown pouch (#26). Removed while tuning: an orange logo decal, a dark olive hood, and a watch "face" (the word `face` was dropped).
- Leather: catches the grey buckles (COD F0 0.008, so plastic), a holster, handcuffs, rubber bands, patches and dark glossy gear. One tan fabric (#76) is caught by the gloss rule. Because leather's target is the glossier half of cloth, a wrong leather call on cloth comes out glossier than cloth, not wildly off.
- Metal: 4 materials (carabiner, watch, …). Metal pixels inside other materials go through the per-pixel blend.

**Result on the 4 test characters** (median over each atlas, G-buffer at F=0, specular / smoothness):

| Atlas | enc=2 + per-part values | enc=3 + neutral values | Vanilla (393 materials) |
|---|---|---|---|
| Upper | 0.047–0.052 / 0.23–0.45 | 0.037–0.039 / 0.11–0.25 | 0.052 / 0.24 |
| Lower | 0.035–0.043 / 0.17–0.37 | 0.030–0.039 / 0.11–0.33 | 0.045 / 0.18 |
| Head | 0.030–0.043 / 0.22–0.29 | 0.027–0.039 / 0.19–0.30 | 0.044 / 0.29 |
| Hands (FP) | 0.022–0.056 / 0.13–0.40 | 0.015–0.039 / 0.08–0.40 | 0.057 / 0.27 |

**Known limits and what to look at in the A/B:**
- Specular now equals COD's F0 (0.04 at F=0 for a dielectric). That's inside the 12 vanilla clothing materials (0.02–0.11, median 0.03–0.045) but below the 393-material medians (0.045–0.057). If enc=3 cloth looks too dull, that's the first knob.
- MW2 Kleo's skin is glossier in COD (median 0.56–0.70) than the MW4 and BO7 skins (0.42–0.48). The pooled quantile map keeps that ranking, so Kleo's skin lands at 0.46–0.72, the top of vanilla head skin. First-person hands, which are mostly skin, come out at 0.38–0.40 against vanilla hands 0.27. Look at skin first in game.
- `_d.a` of a dielectric is 0.04/0.55 ≈ 0.073, about 19 steps of 8 bits. Banding is unlikely, but check smooth skin.
