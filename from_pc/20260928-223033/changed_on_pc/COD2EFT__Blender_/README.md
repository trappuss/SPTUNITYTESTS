# COD2EFT — Call of Duty → Escape from Tarkov model porter

This tool poses Call of Duty character models onto the EFT skeleton and turns COD's own weights into EFT bone weights. It outputs **Head / Upper / Lower** meshes on your EFT armature, each with one EFT-style texture set, ready for Unity (WTT-SDK → Groovey → Clothing and Head Creator).

It has been tested on 33 characters across every Warzone-era game: WZ1 (MW2019, Vanguard, Cold War) and WZ2 (MW2, MW3, BO6, BO7), plus the MW4 beta. Both FBX and Cast input work, on Blender 4.4 and 5.0.

## Install (once)

Use either of these:

- **In Blender:** Edit → Preferences → Add-ons → ▾ → **Install from Disk…** → pick `COD2EFT_Blender_Addon.zip` and tick it. Then, in the add-on's preferences, set **Template** to your `EFT BASIC [Template].blend`.
- **Or double-click `Install_COD2EFT_Addon.bat`** (with Blender closed) — recommended. It does the same as the above, fills in the template path, and removes the old single-file version. It also makes a **live install**: the add-on loads its code straight from this COD2EFT folder. After that, updated files in this folder take effect the next time Blender starts, or right away with the **↻** button next to "live from folder" at the top of the panel. **You only run the .bat once** (run it again only if you move the COD2EFT folder). If the folder is missing, the add-on falls back to the copy installed with the zip.

**Which version am I running?** The panel's title and its first line show it (e.g. **COD2EFT v2.3.0**), and so do Preferences → Add-ons, the top of every report and every batch log. The version goes up with every update (changes are listed in `CHANGELOG.md`). If the files in this folder are a different version from the one running, the panel says so in red and tells you what to do: press **↻** (live install), or close Blender and run `Install_COD2EFT_Addon.bat` (installed copy). The .bat also prints the version it replaces and the one it installs. An add-on that shows no version number at all is from before 2.3.0.

You don't need the Cast add-on. A copy of DTZxPorter's MIT-licensed importer ships inside the zip and is used when Blender doesn't have one.

## Use (all inside Blender)

Press **N** in the 3D view and open the **COD2EFT** tab.

| Panel section | What it does |
|---|---|
| **Setup** | Shows the template and whether the scene has the EFT armature. **Add** appends it (Import does this for you if it's missing). Also shows where the add-on code comes from (live folder or installed copy) with a reload button. |
| **Settings (Import + Batch)** | Every setting that changes what Import and Batch produce: the fit (body-volume matching, **Head height**, **Line up by** eyes / nose, **Neck lean limit**, **Head forward**, **Match fingertips**, joint snapping, best-fit scale, saved pose tweaks), the conversion (prefer .cast, max bones per vertex, join parts, **Separate by COD material**) and the textures. Folded away by default, but **any setting that isn't at its default is always listed in red**, with a **Reset** button, and so are saved pose tweaks (with a delete button), so nothing changes an import without you seeing it. |
| **Convert one character** | **Import + Convert…**: pick any file of a character (the body is enough, since the head and Cold War parts are found next to it) or just open its folder and press the button. The character ends up as Head / Upper / Lower on the EFT armature in the current scene. **Import only…** stops after import so you can run the steps yourself. **Separate by COD Material** splits the selected converted parts (or all of them) into one object per original COD material — see below. **Make First-Person Hands** adds `<name>_Hands` (see *First-person hands* below). **Export FBX for Unity** writes the result. |
| **Batch** | **Batch Convert…**: pick files, a folder, or a whole tree of folders. It runs in a background Blender, so the UI stays usable, and shows its progress in the panel (Esc over the 3D view cancels). Each character becomes `<name>_EFT.blend`, `.fbx` and `_report.txt` in `EFT_Converted\` (or the **Output** folder you set), plus `_COD2EFT_summary.txt`. |
| **Check** | **Compare to EFT** colours the COD parts green and EFT's own template meshes magenta. Where magenta shows through, EFT's body is outside the COD mesh. **Test pose** (squat, arms up, arms down, twist, stride, lean) poses the EFT armature so you can check how the weights bend; **Rest** puts it back. **Check Gear Clipping** colours the COD parts by how far they stick out of EFT's own body (white = on or inside, up to red = 8 cm or more). EFT vests, rigs, backpacks and helmets are made for EFT's body, so red areas are where they may clip. It also shows the last fit's numbers: how far the torso, legs, arms and soles are from EFT, the face offset, and how much sticks out. |
| **Step by step** | **1. Fit** → tweak the pose → **Save Pose Tweaks** → **2. Convert** → **Convert Textures** (re-runs the texture step, e.g. at another size or after separating / joining pieces). It uses the Settings above. |

Each run writes a log to the `COD2EFT_Report` text block (Scripting workspace).

## Separating gear (vests, hats …)

After conversion each part has one atlas material, so Blender's "separate by material" can't split it any more. **Separate by COD Material** (button, or the setting to do it on every import / batch) uses the original COD material of every face, which the tool keeps, and makes one object per COD material: `<name>_Upper_<COD material>`. Each piece keeps the armature, weights and UVs and still uses the part's atlas.

To give a piece — say a vest made of several COD materials — its own texture set: join its pieces (Ctrl+J), then press **Convert Textures**. It rebuilds the atlas from the original COD materials for just that object and names the files after the object's part name (`<name>_<Part>_<COD material>_d.png` …). This works in any saved .blend: the original COD materials are now kept in the file for this (fake user).

## Without opening Blender

Drag files or folders onto **`COD2EFT_Convert.bat`**. It does the same as **Batch Convert**. The first run finds `blender.exe` and your template, and remembers both.

## What it works out by itself

| Thing | How |
|---|---|
| **Which files belong together** | `body_X` + `head_X`; the Cold War `*_torso` / `*_lowerbody` / `*_arms` / `*_head` parts; `rex_body_…` + `rex_head_…_high_fe`. When a folder has exactly one body and one head with unrelated names (Vanguard), they're paired. Otherwise (e.g. the MW2019 folder with 9 bodies and 2 heads) each is converted on its own, and a head alone becomes a Head-only output. |
| **What to skip** | First-person models: `vm_*`, `*_vm_*`, `viewarms` / `viewbody` / `viewlegs` files, and any skeleton with a `tag_view` bone (a character's first-person arms model is used for **First-person hands** when that is on). It also skips alternative heads (`_high`, `_lod1`, `_blendshape`) when the standard head exists, duplicate parts (`torso` vs `torso_pc`), files with no COD character skeleton (EFT files, props), and Maya `lambert1` placeholder meshes. |
| **Game / rig family** (reported) | This is read from the skeleton: T9 (Cold War), IW8 (MW2019 / Vanguard / early MW2), IW9/JUP (MW2 / MW3), T10/SAT (BO6 / BO7), or MW4 beta. The conversion itself works on bone names and is the same for every family. |
| **Coordinate spaces** | FBX (Y-down, 0.01 scale), Cast (Z-up, cm), heads exported in their own space (rooted at `j_spine4` or `j_spinelower`), and Cold War parts that each have a different local space. Parts are aligned to the body through their shared bones. Bones that are posed differently in the two files (visors, helmets) are detected and ignored. |
| **Rotation** | Built from named directions (up = pelvis→neck, left = `_ri`→`_le`), so the model can never come out mirrored or facing backwards. |
| **Inside-out meshes** | About ⅓ of the test meshes (MW2019, Vanguard, MW2, Cold War bases) have triangles wound against their own normals. They would render inside-out in Unity. The tool flips them and keeps the original normals exactly. |
| **Pose** | Arms and fingers are aimed and stretched so the shoulders, elbows, wrists and knuckles land on the EFT joints. |
| **Body volume** | COD and EFT put their joints in different places inside the body. EFT's spine joints sit near the back (its chest centre is about 8 cm in front of the Spine3 joint; COD's is about 1 cm), and EFT's knee joint sits near the front of the knee. So the torso, hips and legs are lined up by their cross-sections, measured from EFT's own T-shirt and pants, instead of joint on joint. Before this, the chest came out about 6 cm too far back and the knees about 5 cm too far forward. |
| **Feet** | The soles are kept flat, as they stand in COD's bind pose, and are put on EFT's floor, turned to EFT's toe direction. Before this, the feet copied the EFT foot bone's angle, which left the toes up and the heels about 2 cm under the floor. |
| **Shoulders** | The shoulder joint is moved (through the collarbone) so the upper arm's cross-section lines up with EFT's (usually 1.5–2 cm). |
| **Hips** (measured only) | COD hip joints sit about 3 cm wider apart than EFT's, and the COD upper thigh ends up about 2 cm further out. Moving the hip joints in to fix this squeezed the groin, which is weighted to the pelvis, so it isn't applied. The thigh numbers are in the report. |
| **Head** (for EFT hats, helmets and glasses) | EFT's headwear is placed for EFT's head, so the COD head is brought to where EFT's head is. The landmark is the **eyeball centres** (COD's eye bones sit at the eyeball centre, within 2 mm on 15 test heads; EFT's were measured from its own head model in the game files). Without eye bones the nose tip is used. The head stays upright. **Front/back:** the neck leans forward (at most 35°), and **Head forward** (default 0.5) lets the upper spine lean to take up half of what's left. **Height:** **Head height → Limited** (default) stretches or shortens the spine and neck together by one factor, at most 6 %, so the eyes reach EFT's eye height; **Full** allows 0.75–1.35 (the old "Match height"), **Keep COD height** doesn't change it. On the 21 test characters with eye bones, the eyes are now a median 0.9 cm from EFT's (90 % within 1.6 cm), instead of 2.2 cm (90 % within 4.1 cm) — mostly they were too low, so hats would float. Head size is not changed: the COD eye spacing averages the same as EFT's (6.4 cm). |
| **Fingers** (for the grip on weapons) | EFT's knuckles and fingertips were measured from the hands of EFT's own third-person tops (the first-person hands agree within 2–5 mm). The finger roots are moved onto EFT's knuckles (at most 3 cm) and the last finger segment is aimed at EFT's fingertip, lengthened or shortened at most ×0.8–×1.25. Fingertips are now a median 0.1 cm from EFT's instead of 0.8 cm (max 1.0 instead of 2.6), and the worst finger-segment stretch went from ×1.9 to ×1.57 (the first finger segments no longer have to bend over to reach EFT's knuckle row). Setting: **Match fingers**. |
| **Weights** | COD weights are kept and renamed to EFT bones. Helper, cloth and gear bones follow their nearest mapped parent. Joint correctives (T9 knee/elbow bulge, IW8 `*dq`, T9 `hiphalfrot` / `neck2`) are split 50/50 between the two bones either side of the joint. The forearm and thigh twist uses EFT's own weight falloff. Max 4 bones per vertex, and only the 58 template bones are written (matching the WTT skin preset order). |
| **First-person hands** (off by default) | EFT tops need a separate first-person hands model (the hands bundle). EFT's own (`Hands_BEAR`) turned out to use the same bone names as the body, just 40 of them (upper arms, forearms, palms, fingers), and to reach from the fingertips to just past the shoulder. With **Settings → Convert → First-person hands** on (or the **Make First-Person Hands** button under Convert one character, or `--fp-hands`), Convert also makes `<name>_Hands`, weighted only to those 40 bones, with its own texture set. **Hands from → COD first-person arms** (default) uses the character's own first-person arms model (`vm_arms_*`, `*_viewarms`, `vm_c_*`, `*_vm`, found in the same folder by name: 25 of the 33 test characters have one, and each was matched and fitted) and fits it to EFT's arms, knuckles and fingertips on its own. COD's first-person arms are made for that view: 7 000–85 000 vertices on the test characters (median about 25 000; EFT's own have 15 000), so check the count before building the bundle. Without one, or with **Third-person arms**, the arms of Upper are used (4 700–9 900 vertices on the test characters, cut just past the shoulder). `<name>_Hands` is hidden in Blender because it overlaps Upper; it is exported in the FBX with the other parts. |
| **Head / Upper / Lower** | Everything from a head model goes to Head. For body meshes, each connected piece goes to the region that holds most of its weight: belts go with pants, and jacket/hoodie hems go with the top. A piece that really spans torso and legs (a one-piece suit) is cut where the leg weights take over. |

## Textures

**Convert textures** turns each part's COD materials into ONE EFT texture set in an atlas. Materials that use the same textures share their space in the atlas.

| File | Contents | EFT shader slot |
|---|---|---|
| `<name>_<Part>_d.png` | RGB = colour (COD's occlusion multiplied in); A = COD's specular reflectance F0 (occlusion multiplied in too) | `_MainTex` |
| `<name>_<Part>_n.png` | Normal map, OpenGL/Unity style (green = up) | `_BumpMap` (texture type **Normal map**) |
| `<name>_<Part>_g.png` | COD's gloss = Unity smoothness (white = shiny) | `_SpecMap` |

The UV map becomes the atlas layout, and COD's own UVs are kept as a second UV map, `COD_original_UV`. Each PNG is tagged inside the file (`COD2EFT enc=2`), so the Unity side can recognise it wherever it's copied (see `unity\COD2EFT_TEXTURE_SPEC.md`).

**What EFT does with them.** EFT's character shaders (`p0/Reflective/Bumped Specular SMap(_Decal)`, decompiled in the WTT-SDK) write their lighting buffer as: colour = `_MainTex.rgb` × (`_DefVals.x` + `_DefVals.y` × F), specular = `_MainTex.a` × `_Glossness` × (`_SpecVals.x` + `_SpecVals.y` × F) / 2, smoothness = `_SpecMap.r` × `_Specularness` (F is a rim term). EFT renders in **Gamma** colour space with its **own** deferred lighting shader (checked in the game's `globalgamemanagers`), so how those buffer values finally look is not known yet — the shader values that go with these textures are the Unity side's job (see `unity\COD2EFT_TEXTURE_SPEC.md`). For comparison, measured over EFT's own 422 character materials: buffer specular median 0.05 (clothes and heads alike), smoothness median 0.22 (clothes) and 0.30 (heads). COD's cloth gloss has nearly the same median (0.24); COD skin is glossier (0.48–0.56).

**Multiple materials per part are fine.** EFT does it too: of its first-person hands, 65 of 103 have 2–4 materials (skin, watch, watch glass); boss heads with hair have 2 (head + cut-out hair). A converted part has 1 material, plus 1 cut-out material if it has hair / lash / decal cards.

**Hair, lash, brow, beard and fringe cards** go into a separate `<name>_<Part>_alpha_*` set whose `_d` alpha is the cut-out mask (1 = visible). That is how EFT does it: its hair materials (Big Pipe, Birdeye, Partizan) use the shader `p0/Cutout/Bumped Diffuse` with the cut-out in the **alpha of the diffuse** (`_MainTex`, DXT5), `_Cutoff` 0.48–0.87, a normal map, no spec map, and back faces culled. COD keeps the cut-out in a **separate single-channel map** instead, in a different slot per game, which the tool now finds:

| Game | Where COD keeps the cut-out |
|---|---|
| Cold War | the `alphaMap` semantic, or the colour map's alpha on hair cards |
| MW2019, Vanguard, MW2, MW3 | hashed slot `0xC`, `0x1B` or `0x1C` |
| BO6, BO7, MW4 beta | hashed slot `49` or `4a` |

The same slots hold other masks on other materials (a face's detail mask, a strap mask, a thermal view), so a map is only used when it cuts the mesh the way cards do: it reaches full white, and strands run across the faces instead of whole faces being on or off. On the test characters this picks every hair, lash, brow, beard and fringe material (32), plus one Cold War button decal that has its own alpha map, and nothing else (since 2.4 also 5 decals whose cut-out is in the colour map's alpha, see *Decals* below); before, only 1 of 33 characters got any cut-out, because most COD materials have hashed names. The report lists the map each material uses. In Blender the cut-out shows in **Material Preview** (not in Solid view). COD's hair cards are single-sided like EFT's, and they face outward, so EFT's back-face culling barely changes them (checked in renders).

### Texture options

Under **Settings → Textures → Texture options** in the COD2EFT tab (the batch uses the same settings; the .bat has matching flags). Every run starts again from the original COD materials, so you can change a setting and press **Convert Textures** again.

| Option | Default | What it does |
|---|---|---|
| Texture size | 2048 | Atlas size per part (1024 / 2048 / 4096). |
| Texture layout | Used parts only | Only the areas of each COD texture that the part's UVs actually use go into the atlas, and every material gets the same texel density on the model. **Whole textures** puts each COD texture in whole (the old layout). |
| Normal maps | OpenGL | **DirectX** flips the green channel. See below for which one EFT uses. |
| Specular strength | 1.0 | Multiplier on COD's reflectance written to `_MainTex` alpha. 1 = COD's own value. |
| Metal colour kept | 0.7 | How much of a metal part's colour stays in the colour map. COD's metal colour is its *specular* colour, so a fully physical conversion makes metal black (the old behaviour: buckles, badges and zips came out black). EFT's own gear doesn't do that: in 51 diffuse maps from EFT's equipment bundles, the high-specular (metal) areas have a median brightness of 0.35, brighter than the rest (0.19). COD's metal areas average 0.50 (48 materials), so 0.7 keeps them about as bright as EFT's. 0 = black metal. |
| AO strength | 1.0 | How much of COD's ambient occlusion is multiplied into the colour. EFT's character shader has no AO slot, so this is the only way to keep it. |
| AO into specular too | on | Also multiplies the occlusion into the specular, so creases don't shine. |

**Why "used parts only" is the default.** A COD part often uses only a small corner of a big texture: a strap on a pants texture, or gear that landed in Upper while its texture is shared with Lower. The old layout put the whole texture in the atlas, so those bits came out blurry — WTT's "looks like 512 instead of 2k" problem. Now each texture is cut down to the rectangles its UV islands use (with 4 px of real texture around each for mip-maps), and all materials are sized to the same pixels-per-metre on the model. It never scales past the COD texture's own resolution. Measured on the four test characters at 2048:

| Part | Lowest material, whole textures | Lowest material, used parts only |
|---|---|---|
| BO7 silver, Head | 208 px/m | 1419 px/m |
| Cold War female, Upper | 141 px/m | 1118 px/m |
| MW2 Kleo, Upper | 135 px/m | 830 px/m |
| MW2019 codl male, Upper | 159 px/m | 641 px/m (capped by COD's own texture) |

For comparison, EFT's own template clothes (1024 textures) are about 860 px/m, and its head about 1960 px/m. The report prints the density of every part.

**Filling the atlas.** Even texel density alone left atlases 60–80% full: a few big pieces (the shirt, the pants) decide when the next size step no longer fits. So the layout then tries keeping the 1–3 biggest texture sets at that size and growing everything else, and keeps whichever gives the most detail over the whole surface. Measured on the same characters: atlases now 77–95% full, and +11% texel density overall (surface-weighted), with the lowest density per part unchanged. (Letting pieces turn 90° was also tried: +0%, so it isn't done.)

It does not repack individual UV islands or rebake textures, so scattered islands inside a used rectangle still waste some space.

**Normal maps: OpenGL, not DirectX.** WTT's notes recommend DirectX normals. Tested against EFT itself, the evidence says otherwise (and if hands or faces look wrong in Unity, first check that `_n.png` is imported as **Normal map** and used by an EFT material — see *Testing in SPT*):

- the EFT character shader (`p0/Reflective/Bumped Specular SMap`, decompiled in the WTT-SDK) unpacks `_BumpMap` the standard Unity way, with no flip;
- a height-field test gives the OpenGL sign on all of EFT's own normal maps in the template (`Tshirt_bear_Voin`, `Pants_wild_bomber`, `Bear_head`), and they render correctly as OpenGL in Blender;
- the same test gives the DirectX sign on 77 of the 81 COD maps of the test characters. The other 4 change sign with the image scale, which rotated or mirrored UV islands cause, so every COD map is converted rather than deciding per map.

So the default writes OpenGL/Unity maps. To check in Unity: light the model from above. Seams, stitching and panel lines should look raised or recessed the same way the colour map suggests. If bumps look like dents everywhere, switch **Normal maps** to DirectX and convert again. The Blender preview shows both correctly.

**Subsurface.** EFT's character shader has no subsurface slot, so COD's subsurface maps aren't used. The occlusion is the only other "extra" COD map, and it goes into the colour and specular as described above.

**Picking the right COD images.** Some materials have several normal/gloss ("NOG") maps. Skin materials also carry *wrinkle* maps — on BO7 `orange_outlaw`'s face, a 2 × 2 tile of expression wrinkles at the same size as the real map, with a flat gloss of 1.0. The tool used to take that one, which gave the face wrong creases and very glossy, wet-looking skin. It now prefers the map whose gloss actually varies, then the one the size of the colour map. On all the other test materials with several candidates, the pick is unchanged.

**Tinted materials.** A few materials (MW2 Kleo's pants) have a colour map with no colour in it and one constant colour among their images (here dark navy). The colour is then colour map × that colour. This is worked out from the export only — check it against the game.

**The colour map's alpha: metal or not.** In the Infinity Ward games the colour map's alpha is a metal mask: ≤ 0.1 is a non-metal's reflectance, above that the pixel turns to metal (colour = its specular colour). That holds for gear, but not everywhere, and reading it as metal where it isn't made skin grey and shiny (reflectance 0.2–0.3 instead of about 0.04 — what made converted skin look wet). Checked on all 146 such materials of the test characters, the tool now reads it per material, on the mesh:

| The alpha … | Read as | Test characters |
|---|---|---|
| is mostly ≤ 0.12 or ≥ 0.88 | a metal mask (as before) | most gear |
| sits in between over more than half the surface | **not** a metal mask: a non-metal (0.04), except where it stands far above the material's own level (rivets) | 9 skins, a mouth, a hair fray, 4 gear materials |
| is the same everywhere (≥ 0.9) | no alpha at all (a colour map without one): a non-metal | eyes, teeth, hands skin … |
| belongs to a tinted, colour-less map, or copies it | a tint mask, not specular | MW2 Kleo's pants, collar and white jacket (the jacket came out as white chrome) |

The report says which one each material got.

**Decals.** Logos, patches, a necklace, MW2 Kleo's emblem: their colour map is black where nothing is drawn, and the alpha says where. They now go into the cut-out set, so they no longer show as black shapes.

**MW2019 (Greyhound "split" exports).** These have separate `X_n`, `X_g`, `X_o` files instead of the packed normal/gloss map, and the packed colour PNG has lost its alpha. The tool didn't find them, so MW2019 characters had **no normal or gloss map at all**. It now uses the split files (`X_n` holds the packed map's two hemi-octahedron channels — decoding them that way gives the same DirectX height test as the other IW maps, 18 of 23 maps) and rebuilds the metal mask from the split colour / specular files.

**How it was worked out.** The EFT format comes from the shader EFT uses and from EFT's own textures in the template. For example, `Tshirt_bear_Voin_d` stores specular in its alpha, with an average of 0.12. The COD formats were checked on the test exports:

- **Infinity Ward games** (MW2019, Vanguard, MW2, MW3, BO6, BO7, MW4 beta) use:
  - a colour map (RGB colour; A = reflectance / metal mask);
  - a packed "NOG" map (R = gloss, G/A = normal, B = occlusion).
- **Cold War** uses separate colour, normal, gloss, specular and occlusion maps. Its "glossMap" actually holds *roughness*: metal parts average 0.06–0.17 and cloth 0.85–0.94. So it's inverted.
- **Finding the right files.** The exporter's files (`_mat_info`, `*_images.txt`, `.mtl`, the image the importer put on the material) say which image is which. Where they only have hashed names, the image content decides.

**Things to check.**

- **Specular strength / gloss:** passed through as COD has them (1.0). COD skin is glossier than EFT's heads (see above); if a face looks too shiny in the game, lower **Specular strength** here, or have the Unity side lower the material's value.
- **Missing textures:** if an export has no `_images` folder, that part keeps its COD materials, and the report says so. If one material's images can't be read, that material comes out grey (a cut-out one is left out) and the rest of the character still converts; the report names it.
- **Long folder paths (Windows).** Windows can't open a path of 260 characters or more unless long paths are switched on, and COD exports get there easily (deep folders + long image names: Park 24_1's blouse-button decal is 269). Before 2.4.3 those textures were missing after import, and the texture conversion stopped at the first one, so later parts kept their COD materials. Now the import goes through a short link to the model's folder (a junction in the temp folder, removed right after), the long images are packed into the .blend, and the conversion reads long paths directly. Moving the test folder somewhere shorter (e.g. `C:\COD\`) avoids the issue altogether.

**The Blender preview (Material Preview / Rendered).** The converted materials show the part the way EFT draws it, with the values the Unity side sets (see the table in `unity\COD2EFT_TEXTURE_SPEC.md`):

- colour = `_d` RGB × (`_DefVals`.x + `_DefVals`.y·F), F = (1 − N·V)²/2 — a bit darker face-on, brighter at the rim;
- specular = `_d` alpha × `_Glossness` × (`_SpecVals`.x + `_SpecVals`.y·F)/2;
- roughness = 1 − `_g` × `_Specularness`;
- cut-out sets: no specular (EFT's cut-out shader is diffuse only).

EFT lights in gamma space and Blender in linear space, so this is a close guide rather than an exact match (the colour factor is converted, highlights are approximate). The values only affect the preview; the PNGs and the FBX are the same as before.

## Testing in SPT (Unity + WTT-SDK)

**Why a model can look wrong in Unity / EFT.** Unity makes its own *Standard* materials when it imports an FBX. Those don't read `_MainTex` alpha or `_SpecMap` at all (smoothness is a flat 0.5 everywhere, so skin and hands shine evenly), and a `_n.png` imported as a plain texture gives no normal map. That is what the first MW2 Kleo bundles had. The materials have to be switched to EFT's own shaders.

**Who does that:** the **EFT Auto Prefabber** in the WTT-SDK (`Assets\Editor\EFTAutoPrefabber`, its material fixer) owns the Unity side — materials, prefabs, bundles. What the COD2EFT PNGs contain, and who owns what, is written down in **`unity\COD2EFT_TEXTURE_SPEC.md`**; both tools follow it. COD2EFT itself has no Unity code.

1. Drag `<name>_EFT.fbx` (from `EFT_Converted`) onto **`COD2EFT_To_Unity.bat`**: it copies the FBX and its PNGs into `Assets\COD2EFT\<name>\` of your WTT-SDK project (the first time it asks for the project folder). Or copy them by hand into your working folder.
2. Set up materials, prefabs and bundles with the EFT Auto Prefabber.
3. Manual route instead of the prefabber: open **Custom Windows → Groovey → Tools → Clothing and Head Creator**:
   - For **Global Skin Preset**, choose `ScriptPresets/ExampleSkeletonSkin.preset`. Its 58 bones are in the same order as the converted meshes.
   - Select the part objects as the skin objects.
   - Create, then build the bundles in the Asset Bundle Browser as the SDK describes.
4. Server side: WTT-CommonLib's CustomClothing / CustomHeads services take one JSON file per item (`db/CustomClothing/`, `db/CustomHeads/`), with the bundle paths under your mod's `bundles/` folder. See its [README](https://github.com/WelcomeToThursday/WTT-CommonLib/blob/main/README.md).
   - A **top** needs a hands bundle (`handsBundlePath`): turn on **First-person hands** and use `<name>_Hands` for it (untested in the game so far), or point it at one of EFT's own hands bundles.
   - The README doesn't show a **bottom** example.

## Customising

- `cod2eft_bonemap.json`: override any COD bone. Copy it from `cod2eft_bonemap.example.json`. A value can be `"LForearm2"` or a split like `{"LThigh1": 0.5, "LCalf": 0.5}`.
- **Save Pose Tweaks** (Step by step): stores your rotations on top of the automatic fit. They're applied on every later conversion, batch included.
- The bone map and tweaks live in the settings folder: this `COD2EFT` folder when installed with the .bat, otherwise Blender's config folder. You can change it in the add-on preferences, and **Open settings folder** takes you there.
- The .bat's options are set with `OPTIONS=` at the top of `COD2EFT_Convert.bat`: `--cast`, `--joints-only`, `--head-height off|limited|full`, `--face-landmark eyes|nose`, `--no-fingertips`, `--match-height` (= `--head-height full`), `--neck-lean DEG`, `--head-forward F`, `--no-lengths`, `--no-tweaks`, `--fit-scale`, `--no-join`, `--split-materials`, `--max-influences N`, `--no-textures`, `--texture-size N`, `--texture-layout islands|whole`, `--normal-style opengl|directx`, `--spec-strength F`, `--metal-colour F`, `--ao-strength F`, `--no-ao-spec`, `--export-fbx`. Saved pose tweaks now apply to batch runs only while **Apply saved pose tweaks** is on (before, the batch always applied them).

## Known limits

- The body-volume targets were measured on EFT's own Bear T-shirt and wild bomber pants in the template. Bulky COD gear (plate carriers, backpacks, long coats) can shift where the torso's middle appears to be. So a COD value more than 3 cm away from the typical COD value (the median of the 31 test bodies) is limited to 3 cm, and the report lists it.
- The **Body match after fit** and **Soles after fit** lines in each report show how close each landmark ended up.
- EFT only has a male body. Female COD models are fitted onto it, so the joints line up but the shape isn't reshaped.
- EFT's palm is about 29% longer than COD's (the width is about the same), so gloves are stretched along the hand to put the knuckles on the EFT finger joints. Use `--no-lengths` (or untick **Snap limb joints to EFT**) to keep COD's lengths.
- The Cold War nude bases are anatomical base bodies, not outfits.
- Texture atlases reuse COD's UVs. The UVs of faces that cross a texture repeat (usually a few dozen per part) can't be represented in an atlas and may look stretched; the report counts them.
