# COD2EFT — next session (Blender side) — written 2026-09-27, end of 2.4.3

For whichever model continues COD2EFT. The Unity side (EFT Auto Prefabber, WTT-SDK) is a
**separate session**; the user relays messages between the two. The contract between them is
`unity\COD2EFT_TEXTURE_SPEC.md` — read it first, it is the source of truth for PNG channels,
slot names and material values.

## The user's ground rules
- Thorough and correct beats fast. Work from evidence; anything placed on a hunch is labelled a
  hunch and asked about.
- Concise reports.
- Anything he has to run = a .bat, as automated as possible.
- **Every update bumps the version** (`VERSION` in `cod2eft_porter.py` = `"version"` in
  `addon_init.py`), adds a `CHANGELOG.md` entry, and updates README/spec where behaviour changes.
- COD2EFT has **no Unity code** since 2.4.2. Unity work is the other session's.

## State you inherit: 2.4.3 (deployed, md5-verified)
- Deployed to `SPTModdingTools\COD2EFT\` and unzipped into the installed Blender 4.4 add-on
  (`Blender Builds\configs\Blender\4.4\scripts\addons\cod2eft`; it is also a *live install*
  pointing at the COD2EFT folder via `cod2eft_home.txt`).
- 2.4.3 changes: Windows long-path (≥260 chars) fix — import via a temp junction, long images
  packed, textures read long paths directly, one bad material no longer stops a character;
  Blender preview uses EFT's maths with the spec's per-part values; report flags images an
  export lists but that are missing with 260+ char paths.
- Regression: all 33 Testing characters give **byte-identical PNGs** vs 2.4.2, on Blender 5.0.1
  and 4.4. UI test (`uitest4.py`) passes on both. Kleo with FP hands: all 33467 faces of
  `mp_kleo_iw9_3_1_Hands` on slot `mp_kleo_iw9_3_1_Hands`, UV0 inside the Hands atlas, FBX
  references `…_Hands_d/_n.png`.
- **Not yet tested on real Windows:** the junction step of the long-path fix (simulated on
  Linux only). The user's next import of *Park 24_1* is the real test.

## Agreed with the Unity side (in the spec, section *Material slots and surface classes*)
- Slot = `<name>_<Part>[_<class>]`, Part ∈ Head/Upper/Lower/Hands.
- `_skin` (same atlas as the main slot) → **COD2EFT will produce it from 2.5.0.** Not done yet.
- `_metal`, `_emissive` → reserved, **not produced** (metal is per-pixel in `_d` alpha and mixed
  within COD materials; COD2EFT has no emissive data and SMap/SMap_Decal have no emission input).
- `_alpha` unchanged. FP hands mesh `<name>_Hands` uses only `<name>_Hands…` slots.

## Next, in order
1. **Wait for / check the user's result of Park 24_1 on Windows** (long-path fix). If textures
   are still missing, read the COD2EFT_Report text in Blender first.
2. **2.5.0: `_skin` slots.** Per part, move faces of skin COD materials to `<name>_<Part>_skin`
   (same atlas; no change to PNGs). Hard part = the detector:
   - must work on hashed IW names (`material_34b5…`) and Cold War names alike;
   - candidate signals (all UNVALIDATED): skin-tone colour rule over UV-covered texels
     (`_dev/skindet.py`, Kovac 2003 rule — a first probe only), the IW "alpha sits in between →
     not a metal mask" class (9 skins + 4 gear on the test set, so not skin-only), wrinkle NOG
     maps, name words (`skin`, `head`, `hand`, `body_…`).
   - **Before shipping:** hand-label every material of all test characters (skin / not), report
     precision + recall per game, show the user a contact sheet of the misses. Then implement,
     add a report line per material, update spec + README + CHANGELOG, re-run the regression.
   - Also split `_Hands` → `_Hands_skin` the same way.
3. Kleo calibration (spec *Calibration step 2*) needs the user to re-convert Kleo with 2.4.3
   and *First-person hands* on; his `EFT_Converted\kleo_empty_.fbx` is a hand-edited export whose
   arms use the Upper slot/atlas and lacks COD2EFT's Hands mesh.
4. Backlog: drop `COD_original_UV` from the FBX (Unity imports it as UV1, unused).

## Known facts / scars (don't re-derive)
- **Park 5_1 and Park 11_1 exports are incomplete on disk:** 10 listed images missing, every one
  with a Windows path ≥ 291 chars (all present files ≤ 285). The exporter couldn't write them.
  Fix = user re-exports to a short folder (`C:\COD\`). Effect now: 2 zipper decals + harness
  rings grey in 5_1, a few maps missing in 11_1.
- MW2019 "split" exports deliberately lack the packed `X_n&Y_g` image — not "missing".
- COD hand-skin gloss (median, UV-covered, as written to `_g`): Kleo FP 0.56, BO5 esports hands
  0.49, Park 24_1 FP 0.35 → Unity `_Specularness` ≈ 0.55 (first estimate, 3 characters).
- Blender 5.0 vs 4.4 vertex positions differ up to ~0.9 cm on `orange_outlaw` and `milk_base`
  Head/Upper — pre-existing (same in 2.4.2), not a regression.
- The cloud copy of Testing lacks `WARZONE 2 - BO7 Female 2\body_c_sat_usa_pl_orange_outlaw\_images`
  (staging gap); a full copy is at `/home/claude/orange`. Without it orange_outlaw's Upper/Lower
  PNGs aren't produced, which looks like a regression but isn't.
- `device_commit_files`: copy to a fresh folder under `/mnt/user-data/outputs/` before
  committing, then **md5sum on the device**. Big transfers: tar + `split -b 90M` into
  `_tmp_stage`, stage ≤2 parts per call, remove the parts afterwards.

## Cloud workspace (only if the new model runs in this same container)
- Working copy: `/home/claude/cod2eft` (identical to the deployed files).
- Blender 5.0.1 = system `python3` (`import bpy`); Blender 4.4 = `/home/claude/bpy44/bin/python`.
- Harness in `/home/claude/fitdev` (copies in `COD2EFT\_dev\`):
  - `runall2.py -- <out> <root>` — batch-convert every character under a folder (+FBX);
  - `runsel.py -- <out> "<extra flags>" <folders…>` — selected folders, e.g. `--fp-hands`;
  - `cmpall.py -- <dirA> <dirB>` — vertex diff of two runs; PNGs compared with `cmp`;
  - `ui/uitest4.py` — installs the zip and exercises the panel operators;
  - `mat/codhist.py` / `codhist.json` — per-material texel stats of the test set;
  - `mat/skindet.py` — first skin-colour probe (unvalidated).
- Baselines: `all50n`/`all44n` = 2.4.2, `all50o`/`all44o` = 2.4.3, `park50` = the four Park
  characters, `kleo_fp` = Kleo with FP hands. Park exports: `/home/claude/newtest`.
- **Disk is tight** (~5 GB free): delete old `all*` run folders before new full runs (~4 GB each
  with .blend files). Machine: 2 cores, 7 GB RAM — at most 2–3 Blender runs at once.
