# COD2EFT changelog

The version is shown at the top of the add-on panel. It goes up with every update.

## 2.6.2 — 2026-09-29
- `Install_COD2EFT_Addon.bat`: a template path with non-ASCII characters (e.g. `Ü`) no longer aborts the install. `COD2EFT_Convert.bat` writes `template_path.txt` in the console code page, and `install_addon.py` now reads UTF-8 first, then the console / ANSI code pages. (Audit item. Checked in Python with a cp850 file; not run on Windows.)
- `COD2EFT_To_Unity.bat` copies only COD2EFT's own sets, `<name>_<Head|Upper|Lower|Hands>_*.png`. Before, copying `Kleo` also picked up `Kleo_Alt_*` textures. (Audit item.)
- No change to conversions: the regression harness (`tools/regress.py`) gives the same PNGs as 2.5.2 for enc=2 and 2.6.0 for enc=3 on all 4 test characters.

## 2.6.1 — 2026-09-29
- Hair / cut-out preview cutoff 0.5 → **0.3**, to match EFT Tools 1.7.1. You found 0.25–0.35 best: at 0.5, fine hair and fringe details vanished (on valeria, 0.5 hides 7–11 % of the texels with alpha ≥ 0.25; 0.3 hides about 2 %). The test that decides which materials are cut-outs still uses 0.5, so the PNGs are unchanged.

## 2.6.0 — 2026-09-29
- **New setting, Materials: enc=3** (panel: Textures; batch: `--material-mode enc3`). The default stays **enc=2** (unchanged output) until the in-game A/B.
  - Each COD material is classified: cloth / skin / leather-rubber-plastic / metal / glass / hair (cut-out). The class comes from name words, the metal share, a skin-tone colour, the gloss level and the cut-out test.
  - Its gloss is remapped per pixel with a quantile curve for its class, fitted from the 174 test materials onto vanilla EFT smoothness (`tools/fit_gloss_curves.py`). Medians: cloth 0.34 → 0.17, skin 0.47 → 0.32, leather 0.65 → 0.27 (a hunch), metal 0.69 → 0.67.
  - `_d` alpha becomes COD's F0 ÷ (`_SpecVals.x`/2), so EFT's neutral clothing values (`_Glossness` 1, `_Specularness` 1) reproduce COD's specular.
  - The PNGs are tagged `COD2EFT enc=3` and need EFT Tools 1.7.0. The Blender preview uses the same neutral values.
  - The report prints one `class …` line per COD material. The **Material classes** list in the panel overrides a class; it's saved in the .blend and passed to Batch as a JSON file (`--class-overrides`).
- **DirectX normal maps are tagged `n=dx`** (`COD2EFT enc=2 n=dx`), so Unity 1.7.0 no longer flips them a second time (audit item). OpenGL output (the default) is unchanged.
- Regression: enc=2 output of Kleo, sunflower_base, MW4 male and MW4 female is byte-identical to 2.5.2 (all 66 PNGs; meshes, UVs and material values in the .blend identical). The survey's existing fields are identical too. Tested headless in Blender 4.4 with a template rebuilt from `EFT BASIC [Template].fbx`; not yet run on the PC or checked in game.

## 2.5.3 — 2026-09-29
- **Adjust by hand: "Unparent bones" option** (on by default, in the Start Adjusting dialog). The adjust copy's bones get no parents, so moving or rotating one bone never carries its children along. This matches the user's manual workflow. Off: bones stay in their hierarchy, only disconnected (as in 2.5.0).
- *Start from last applied pose* now saves every bone in armature space, so a pose restores identically whether the bones are unparented or not. Poses saved by 2.5.0–2.5.2 still load.
- Tested headless in Blender 4.4: moving the pelvis leaves the spine bones untouched; the baked mesh equals the live preview to within 0.0004 mm; a pose saved unparented restores identically into a parented rig.

## 2.5.2 — 2026-09-29
- **Swayback / twisted waist on curvy characters fixed** (MW4 Beta Female valeria).
  - Cause: the fit lines up the middle of the hip cross-section with EFT's. On a character with wide hips or glutes, that middle sits far back (valeria: 12.3 cm behind her waist section; EFT's own body: 4.3 cm; other test characters: 0.6–3.2 cm). The whole body got pushed 4.8 cm forward, and the waist ended up 4 cm behind EFT's, so the spine bent.
  - Fix: the hip section may now sit at most EFT's gap + 1 cm behind the waist section. The report names it: "pelvis vs waist (…, hip shape)".
  - Valeria after the fit: waist −4.0 → +0.4 cm, chest 0.0 cm, body moved 2.3 cm forward instead of 4.8. Hips −3.1 cm and upper thighs −2.2 cm: her shape behind EFT's, not the pose.
  - Kleo, sunflower_base and the MW4 male are under the limit and unchanged.
  - The slack can be changed with the environment variable `COD2EFT_PELVIS_SLACK` (metres, default 0.01).
  - Tested headless in Blender 4.4, with a template rebuilt from `EFT BASIC [Template].fbx`, so not your exact .blend. Check it on the PC.

## 2.5.1 — 2026-09-29
Two texture bugs, found by measuring every material of Kleo (MW2), sunflower_base (BO7) and the MW4 beta male: 174 materials, 11 models.
- **BO6 / BO7 / MW4 `m_…` materials lost their normal and gloss maps.** These exports write the image list of material `m_<name>` as `_mat_info/<name>.txt`, with no Name line, and it wasn't found. The material then kept only its colour: no normal map, flat gloss 0.5. Affected on the test set: sunflower_base's first-person arm skin, teeth and eye moisture.
- **MW2 Kleo's first-person sleeves came out as white chrome.** Their grey colour map and its alpha are both flat (0.969 / 0.968), so the "alpha copies a colour-less map" rule from 2.4 didn't trigger, and the whole sleeve read as 100 % metal. Flat grey maps whose alpha matches the colour now count as tint masks too. Real metal keeps an alpha well above its colour (MW4 carabiner: 0.88 vs 0.55) and is unchanged.
- Regression over the 174 materials: only these 6 materials changed.

## 2.5.0 — 2026-09-29
- **Adjust by hand (armature), non-destructive.** New box in the panel.
  - **Start Adjusting** adds a copy of the EFT armature whose bones are disconnected, so any bone can be moved, rotated or scaled. It drives an extra Armature modifier at the top of every converted mesh, so the meshes follow live. The EFT armature and the weights are not touched.
  - **Apply** bakes the pose into the meshes (shape keys too) and removes the copy. **Cancel** throws it away.
  - *Only selected meshes* adjusts just the selected meshes, e.g. one clipping accessory. *Start from last applied pose* brings back the previous adjustment.
  - This replaces the manual route (back up the armature, unparent all bones, pose, apply the Armature modifier per mesh, re-parent).
  - Export applies an adjustment that is still in progress first, so the FBX matches what you see.
  - Tested headless in Blender 4.4: the baked meshes match the live preview to within 0.0004 mm, shape keys included.
- **FBX export settings are now fixed in the code.** Blender re-uses an operator's last-used values within a session, so a hand-made FBX export with other settings (scale, leaf bones, axes, apply transform) used to leak into COD2EFT's export. The values are unchanged (checked on the Park 24_1 FBX that worked in game): UnitScaleFactor 100 (cm), −Z forward / Y up, scale 1, leaf bones on, all bones written.

## 2.4.4 — 2026-09-29
- **Long-path import:** the temp link is only used when it is shorter than the model's folder.
  Before, a short folder such as `C:\COD\kleo` holding a 250+ character file was imported through
  `%TEMP%\cod2eft_j\…`, which made the paths *longer* and could leave textures empty.
- The temp link is now always removed, even when re-pointing the images fails part-way.

## 2.4.3 — 2026-09-27
- **Missing textures with long folder paths (Windows) fixed.** Park 24_1 imported with missing
  textures and its conversion stopped at Upper: some image paths are 260+ characters (MAX_PATH),
  which Windows won't open unless long paths are switched on. Now:
  - the import goes through a short link to the model's folder (a junction in the temp folder,
    removed right after); images with long paths are packed into the .blend;
  - the texture conversion opens long paths directly (Blender gets a short temporary copy);
  - one unreadable material no longer stops the rest of the character: it comes out grey
    (a cut-out one is left out) and the report names it.
- **Blender preview uses EFT's maths** (from the Unity side's findings, see the spec): colour ×
  `_DefVals` rim term, specular = `_d` alpha × `_Glossness` × `_SpecVals` rim term, roughness =
  1 − gloss × `_Specularness`, per part; cut-out sets have no specular. The FBX still names the
  same textures as before (the colour is linked straight in for the export).
- Measured COD hand-skin gloss for the Unity side's hands value (spec, *Changes*).

## 2.4.2 — 2026-09-27
- All Unity work (materials, prefabs, bundles) belongs to the EFT Auto Prefabber session.
  COD2EFT's Unity script is removed so there is only one Unity tool; `COD2EFT_To_Unity.bat` only
  copies the FBX and PNGs. The hand-off is `unity/COD2EFT_TEXTURE_SPEC.md`.

## 2.4.1 — 2026-09-27
- **Unity side handed to the EFT Auto Prefabber** (WTT-SDK, its material fixer), which does the
  same job: two tools setting up the same materials would undo each other. COD2EFT's Unity script
  is now a fallback only (automatic setup off by default) and `COD2EFT_To_Unity.bat` no longer
  installs it.
- New `unity/COD2EFT_TEXTURE_SPEC.md`: the hand-off between the two tools — who owns what, what
  each PNG channel holds, the facts from the game files, and what's still open.
- Checked in the game files: EFT renders in **Gamma** colour space with its **own** deferred
  lighting shader. 2.4.0's shader values assumed Unity's standard linear lighting, so they are
  not a verified match — noted in the script, README and spec.

## 2.4.0 — 2026-09-27
Materials: as close to the COD look as EFT's shaders allow, with no manual Unity steps.

- **Unity script (unity/COD2EFT_MaterialSetup.cs) now works by itself.** Drop the FBX and PNGs into
  any folder under Assets: the texture imports are set (`_n` Normal map, `_g` linear …), one
  material per set is made with EFT's own shader (`SMap_Decal` for Head/Upper/Lower, `SMap` +
  Hands stencil for `_Hands`, the cut-out shader for `_alpha` sets), and the model's material
  slots are pointed at them — so prefabs made from the model get the right materials. Before,
  Unity's own *Standard* materials ended up in the bundles (flat 0.5 smoothness, no gloss map,
  often no normal map).
- **New texture encoding (enc=2, tagged inside each PNG):** `_MainTex.a` = COD's specular
  reflectance, `_SpecMap` = COD's gloss, and the script sets EFT's shader to pass them through.
  Specular strength default 1.3 → 1.0. Convert again for the new files.
- **Skin no longer read as metal.** The colour map's alpha is only taken as a metal mask where it
  is (near) two-level; skin's smooth in-between alpha made faces grey and wet-looking.
  Colour-less tinted maps (MW2 Kleo's pants, collar, white jacket) aren't read as metal either.
- **Decals** (logos, patches, a necklace, emblems on black) go into the cut-out set instead of
  showing as black shapes.
- **MW2019 exports** (Greyhound split files) now get their normal, gloss and occlusion maps
  (before: none), and their metal mask back.
- Normal / gloss maps found in the IW slot `0x4` even with a dark occlusion channel or a flat
  normal (MW2 Kleo's watch, MW3 papa's largest head material).
- Blender preview: the material shows `_MainTex.a` as the specular reflectance (IOR).

## 2.3.1 — 2026-09-27
- Hair, lash, brow, beard and fringe cards now get a cut-out (`<name>_<Part>_alpha_*` set). COD keeps
  the cut-out in a separate single-channel map (a different slot per game); the tool finds it and
  checks it against the mesh before using it. The report names the map. On the 33 test characters,
  15 now get cut-outs (before: 1).
- Black Ops 6/7 (`m/` material names): material info files are found again, so their textures are
  matched by COD's own slot names.
- Materials whose only colour is a 1×1 texture get that constant colour.
- Blender: cut-out sets show cut in Material Preview (alpha clip at 0.5, back faces hidden, like EFT).
- Unity script: cut-out `_d` textures import with "Mip Maps Preserve Coverage" (0.5).

## 2.3.0 — 2026-09-27
First numbered release. Earlier builds show 2.2.0 (or nothing) in Preferences → Add-ons.

- Version shown in the panel title, the Setup line, Preferences → Add-ons, reports and batch
  logs; the panel warns when the COD2EFT folder holds a different version than the one running.
- Live install: the add-on loads its code from the COD2EFT folder (Install_COD2EFT_Addon.bat).
- Head lined up by the eye centres, Head height (limited / full / off), Head forward 0.5.
- Fingers: knuckles moved onto EFT's, fingertips aimed at EFT's (Match fingers).
- First-person hands (off by default): `<name>_Hands` from the COD first-person arms, or from
  Upper's arms.
- Settings (Import + Batch) box with changed-setting warnings and Reset.
- Separate by COD material; texture atlas packing, skin normal/gloss and metal colour fixes.
