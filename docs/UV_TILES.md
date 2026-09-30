# UV tiles and the "face on the neck" (2026-09-30, COD2EFT 2.6.8)

**Short answer:** the UV tiles are not the cause. COD uses tiles outside 0..1 as a *repeating*
texture (left/right copies shifted by one tile), which is what `convert_part` already does. The
"face on the neck" look reproduces exactly when the atlas is sampled with the **second** UV layer,
`COD_original_UV`, instead of UV0. Behaviour is unchanged. 2.6.8 only adds a report WARNING and a
panel *Check* line when a material uses tiles.

## What was measured

### brie (BO7 `c_sat_usa_pl_brie_brioche`, the FBX from `from_pc/20260929-210039`)
- Off-tile faces: **Upper 3,114** (u−1: 2,212, u+1: 900, v+1: 2), **Lower 711** (u−1: 456, u+1: 255),
  Head 0, Hands 0.
- Upper: all of them sample **one atlas cell, the glove texture**. The outlines of those faces sit
  exactly on the glove shapes painted in that cell. Tile u−1 holds the left glove (92 % at x < 0),
  tile u+1 the right one (100 % at x > 0). Their height (z 1.24–1.61 m) is the arms of the upper's
  third-person hands in EFT's pose, not the neck.
- Their 3D mirror partners (x → −x) that could be matched (164 sampled) all have the **same
  fractional UV**, so both gloves read the same texels. That is the repeat rule, and 0 matched the
  mirror rule (u → 1 − u).
- The off-tile faces are separate islands. Only 4 vertices are shared with tile-0 faces, and those
  have identical UVs, so they are continuous under any rule.
- **Software render of the FBX** (per-pixel, `docs/previews/uv_tiles_brie.jpg`):
  - with UV0 (the atlas UVs, what Unity and the FBX use): face, neck, straps and gloves all look right;
  - with `COD_original_UV` on the same atlas: **a face appears on the neck** (the Head mesh's neck
    samples the face region of the head atlas), and the whole torso reads the wrong cells.

### All test characters (Kleo MW2, sunflower BO7, MW4 Beta Male / Female, 55 COD materials (58 mesh pieces) that use tiles)
Tile use is **common**, in every game tested (up to u−3…u+4 on zip ties and wires, v−3…v+3 on cables).
Seams are the decisive test: the loops where an off-tile face and a tile-0 face of the same material
meet at the same 3D point.

| UVs at the shared point | Loops | Continuous under |
|---|---|---|
| exactly one (or more) tiles apart | 333 | repeat only |
| identical (the point is on the tile border) | 659 | repeat, mirror and clamp |
| mirrored (u_a = −u_b + 2k) | **0** | mirror only |
| ordinary UV seam | 11,006 | (no information) |

- **Image counts:** 3–11 per material, and each semantic lists one image. There is no second colour /
  NOG map per tile, so there is nothing UDIM-like.
- Park 24_1 (MW-era, tiles present) was judged "looks good" in game with the repeat rule.

**Conclusion:** repeat is the rule, and clamp would smear the glove / strap islands. No behaviour change.

## The likely cause (a hunch until the user confirms)
Something showed the textures with `COD_original_UV` (the 2nd UV layer) instead of UV0. In the
converted .blend (checked on Kleo) and in brie's FBX re-imported, UV0 (`UVMap`) is both active and
render-active. The Image Texture nodes have no UV Map node, so Blender's Material Preview uses the
**render-active** UV layer (the camera icon in *Object Data > UV Maps*). Clicking the camera icon of
`COD_original_UV` gives exactly this look. Other viewers that pick the last UV set would too.

Not done, and why: pinning the atlas UV map in the preview material (a UV Map node) was considered.
Joining or renaming could leave the name dangling, and it can't be tested in a real Blender GUI here.
The queued item "drop `COD_original_UV` from the FBX" (PROJECT_CONTEXT, smaller items) removes the
trap for FBX viewers.

## What would settle it
From the user:
1. A screenshot of the bad preview, with *Object Data > UV Maps* of the Upper visible (which layer
   has the camera icon?). Say whether it was the converted .blend, the FBX re-imported, or Unity.
2. brie's COD export (the folder with `_images` / `_mat_info`). It allows a direct comparison of
   COD's own look against the conversion, and the tile report on the real source. The FBX alone
   doesn't carry the COD material per face.

## The diagnostic (2.6.8)
`convert_part` counts, per COD material, the faces per UV tile (by face centre, the same tile the
wrap uses). A material with more than one tile, or none at tile 0, gets:
- in the report: `WARNING UV tiles: <material> has faces outside the 0..1 UV square (u-1 v+0: 46,
  u0 v0: 46; 5 image(s) listed) ...`
- in the panel's *Last fit* box: `Check: N material(s) use UV tiles outside 0..1`.

Given the survey, expect this on most characters. It is a pointer for when something looks wrong,
not an error.
