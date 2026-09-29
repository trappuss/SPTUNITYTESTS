# COD2EFT changelog

The version is shown at the top of the add-on panel. It goes up with every update.

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
