# COD2EFT backlog

Ideas parked for later, in no particular order. Nothing here is started.

## Next

- **`_skin` material slots (2.5.0, agreed in the spec).** Put each part's skin faces on
  `<name>_<Part>_skin` (same atlas) so the Unity side can give skin its own values. Needs a skin
  detector that works on hashed IW names and Cold War names alike; measure it against hand-labelled
  materials of all test characters before shipping. Signals to test: colour (skin-tone rule over
  UV-covered texels), the IW "continuous alpha" class, wrinkle NOG maps, names.
- **Drop `COD_original_UV` from the FBX** (Unity imports it as UV1; unused). Low priority.

## Waiting until the Blender side is done

- **Automate the Unity step.** A .bat that runs Unity (WTT-SDK project) without a window:
  import the FBX + PNGs, run the material setup (automatic on import since 2.4), create the
  clothing/head items and build the bundles. Parked on purpose: only once the Blender output is final. First step then: confirm
  how the WTT-SDK builds bundles (Groovey Clothing and Head Creator + Asset Bundle Browser) and
  whether it can be driven from `-batchmode -executeMethod`.

## Unity prep - facts gathered (from EFT's own bundles, SPT 4.1)

- EFT **tops include low-poly third-person hands** (Tshirt_bear_turtleneck / Tshirt_usec_CryeAC:
  ~20 vertices per finger segment, 47 bones incl. all Digit bones). So keeping the COD hands in
  Upper is right.
- The **hands bundle** a top needs (WTT `handsBundlePath`) is the FIRST-person arms model:
  Hands_BEAR = upper arm to fingertips, 15 063 vertices, 40 bones (Upperarm, Forearm1-3, Palm,
  Digits). DONE in Blender: "First-person hands" makes `<name>_Hands` (from the COD first-person
  arms, else Upper's arms). Unity side not tried: it needs its own prefab / bundle.
- Bone paths (Unity bone-name hashes = CRC32 of the transform path, all 47 of the top's and all
  3 of the head's resolved): third-person meshes use
  `Root_Joint/Base HumanPelvis/.../Base HumanSpine3/Base HumanRibcage/Base HumanLCollarbone/...`
  (Neck and Head hang off Spine3 directly); the first-person hands use the same path starting at
  `Base HumanPelvis/` (no Root_Joint).  **The template armature has no `Base HumanRibcage`** -
  check whether the WTT-SDK's skeleton preset supplies it (it probably re-binds by name).
- Character materials: 268 of 282 use `p0/Reflective/Bumped Specular SMap_Decal`; hair uses
  `p0/Cutout/Bumped Diffuse` (not in the WTT-SDK).
- EFT mesh space in the bundles = Blender EFT space with x mirrored (bind poses match the
  template armature exactly).

## Open questions (need a check in the game / Unity)

- (Unity side - the EFT Auto Prefabber session owns it) EFT's hair shader `p0/Cutout/Bumped
  Diffuse` isn't in the stock WTT-SDK; the prefabber writes its own stub.
- Normal maps are written OpenGL/Unity style (evidence in the README); confirm in game.
- Tinted materials (MW2 Kleo's pants: grey colour map x one constant colour) - worked out from
  the export only.
- COD fused colour/specular: where the colour alpha is not a metal mask (skin: a smooth map in
  between, 2.4) it is not used at all - what it really holds for skin (specular amount?
  subsurface?) is unknown. Colour-less maps whose alpha copies them (Kleo's jacket) are taken
  as tint masks - worked out from the export only.
- COD gloss is used as Unity smoothness as it is (cloth medians match EFT's; COD skin is
  glossier than EFT's heads, 0.48-0.56 vs 0.30). IW's own gloss curve for these games isn't
  published (WWII's is alpha = sqrt(2 / (1 + 2^(18 g))), which would make everything shinier).
- All Unity work (materials, prefabs, bundles) is the EFT Auto Prefabber session's; the hand-off
  is unity/COD2EFT_TEXTURE_SPEC.md.
