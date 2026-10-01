# Cowork handoff (2026-10-01, updated after the first Cowork session)

For the Cowork session on the user's PC. Read `CLAUDE.md` first (rules), then this file, then `docs/PROJECT_CONTEXT.md` (the full queue, facts and audit).

**Workspace (since 2026-10-01):** everything is in `C:\Users\notso\Downloads\Claude Current\SPTModdingTools`; connect that one folder in Cowork. Its own `CLAUDE.md` + `README.md` (copies kept in `pc/workspace_root/`) are the folder map and the Cowork mechanics: VM limits, git lock files, no push from the VM, Editor.log access, big copies.

## Where everything is (on the PC)
| What | Path |
|---|---|
| **This repo (the source of truth)** | `%USERPROFILE%\Downloads\Claude Current\SPTModdingTools\SPTUNITYTESTS`, branch `claude/bold-mayer-11fzxj` |
| COD2EFT, deployed (Blender live install) | `C:\Users\notso\Downloads\Claude Current\SPTModdingTools\COD2EFT` (conversion reports in `reports\`) |
| The user's real EFT template | `...\SPTModdingTools\Testing\CUSTOM\EFT BASIC [Template].blend` (also `EFT GEAR [Template].blend`, `EFT BASIC [Template].fbx`) |
| Blender | `G:\G Apps\Blender Builds\stable\blender-4.4.3-stable.802179c51ccc\blender.exe` |
| Unity project | `C:\Users\notso\Desktop\RIP\EFT2\Tools\WTT-SDK-2022` (tools in `Assets\Editor\EFTAutoPrefabber`) |
| Unity log | `%LOCALAPPDATA%\Unity\Editor\Editor.log` |
| SPT game | `G:\G Games\SPT4.1\SPT4.1 Game` (BepInEx log `BepInEx\LogOutput.log`, Inspector shots `COD2EFT_Screenshots\`) |
| COD test exports | `...\SPTModdingTools\Testing\` |

## How to work from Cowork
- **Edit in the repo clone, never in the deployed copies.** Commit, then `git push`, then run `SYNC_TO_MY_PC.bat`, which deploys and verifies.
  - The sync refuses to run while tracked files have uncommitted edits. Commit first.
  - `BUILD_SPT_INSPECTOR.bat` builds the in-game plugin.
- **Cowork can test for real**, which the cloud sessions could not:
  - Blender: `blender.exe --background --python ...`, or `blender/COD2EFT/COD2EFT_Convert.bat`;
  - read `Editor.log` and the BepInEx log directly;
  - `tools/regress.py` also runs with Blender's own Python.
- `SEND_RESULTS_TO_CLAUDE.bat` is only needed to hand results to a *cloud* session. Cowork can read the files itself.
- The rules don't change: bump the version and add a CHANGELOG line on every change, report what was verified, give the user `.bat` files, and never touch other mods' files. The future rename is parked (`docs/RENAME_PLAN.md`).
- **Old trap, no longer relevant:** the pre-merge Cowork notes warned that `device_commit_files` silently wrote stale files. Git + sync replaces that route, and it SHA-256-checks every deployed file.

## Current versions (in the repo)
| Part | Version | Verified |
|---|---|---|
| COD2EFT (Blender) | **2.6.12** | export fix tested headless (Blender 4.4 + 5.0, real template, full Park 24_1 conversion); the user runs 2.6.8 |
| EFT Tools (Unity) | **1.7.6** | not compiled; the user loaded 1.7.5 (Editor.log 2026-10-01) |
| COD2EFT Inspector (SPT plugin) | **0.10.0** | compiled with mono against reference DLLs; the user built and ran 0.10.0 |

## Sessions so far and what each is waiting for
All cloud sessions are idle. Their work is in the repo, and nothing is in flight.

| Session | Delivered | Waiting for |
|---|---|---|
| Unification (this one) | repo layout, PC bridge (sync/send), audit, 2.5.x fixes (adjust tool, swayback, FBX scale, presets), regress.py, CLAUDE.md | — |
| Material fix (enc=3) | 2.6.0–2.6.4 / 1.7.0–1.7.3: enc=3 materials, look sliders, BC7 option | **in-game A/B: enc2 vs enc3** next to vanilla; ~20 more vanilla bundles (gloves, holsters, visors, heads) |
| Posture | 2.6.6 posture numbers, 2.6.7 *Match neck* | the user's check of 2.6.7 on a female character. It asked whether to do step 3–4 next: the *Head priority* setting, and a fast *Re-fit* with side-view comparisons |
| SPT Inspector (3 sessions) | 0.1 → 0.10: show/hide meshes, photo mode (isolate / backdrop / transparent), try-on, poses, materials tab, A/B sheet | the user's test of 0.10.0. The *Wear* error and the hideout-vs-menu outfit were addressed and need re-testing |
| Blender panel tidy-up | 2.6.3 sub-panels | the user's look at the layout in real Blender |
| No-PC work | 2.6.8 UV-tile warning + `docs/UV_TILES.md`; 1.7.4 default hands + "not built" note; 1.7.5 bundle check after build; contract moved to `docs/COD2EFT_TEXTURE_SPEC.md` | the checks below |

## Results of the first Cowork session (2026-10-01)
- **Step 1 done:** sync OK, `[EFT Tools] v1.7.5 loaded`, Inspector 0.10.0 built and loaded.
- **Step 2 answered:** brie's render UV is `map1` (UV0) and the face is right; the face-on-neck look appears only with `COD_original_UV` as render UV. **Then found (2.6.10):** the grey shiny neck came from the wrong map: brie's skin used its 2 × 2 *wrinkle map* as normal/gloss (gloss 0.95, four small faces in `_n`, one on the neck). Fixed; please look at brie again in Blender. (The 39 faces across a texture repeat are on the beret band at the top of the head, overhanging by ~0.02 UV: minor.)
- **Step 3 blocked → fixed:** Unity's Scan found 0 skinned meshes in Park's enc2 FBX because the FBX had **no armature**: Blender's exporter skips hidden objects and the `EFT_Template` collection was hidden. COD2EFT 2.6.9 makes everything visible for the export and checks the written FBX; EFT Tools 1.7.6 explains an unskinned FBX. The enc3 FBX (exported while visible) is fine but wasn't scanned yet.
- **Step 7:** the dark ring at Park's neck is in her own texture (nothing to do). The real template is located (table above).
- **Commit `0bd79fa` (2.6.9 / 1.7.6) and the reorganisation commit are NOT on GitHub yet**: the VM has no GitHub login. `SEND_RESULTS_TO_CLAUDE.bat` pushes them; the sync deploys them meanwhile.

## Done unattended later on 2026-10-01 (user away)
- 2.6.10 brie wrinkle-map fix; 2.6.11 FBX without `COD_original_UV`; 2.6.12 material classes checked against 259 hand-labelled materials (81 → 90 % on sure labels).
- `tools/regress.py` tolerates 1-bit float noise (block-mean signatures). New tools: `tools/convert.py`, `tools/check_addon_zip.py`, `tools/material_labels.py`, `tools/pc_status.py` (run it first in a Cowork session).
- PC ↔ cloud without GitHub: `git bundle` via `_xfer/` (CLAUDE.md). Tested both ways.
- Decision (user, 2026-10-01): finish and refine COD first, then split into source profiles: design in `docs/PROFILES_PLAN.md`.

## Test list for the user (in this order; Cowork can drive most of it)
1. ✅ (2026-10-01) **Sync and build.** Close Blender, run `SYNC_TO_MY_PC.bat`, then `BUILD_SPT_INSPECTOR.bat`. Expect COD2EFT 2.6.8, EFT Tools 1.7.5 (`[EFT Tools] v1.7.5 loaded` in Editor.log) and Inspector 0.10.0.
2. ✅ (answered, see above) **brie "face on the neck".** In Blender, select brie's Upper and check which UV map has the *camera* (render) icon in Object Data → UV Maps.
   - `docs/UV_TILES.md` reproduces the face-on-neck look **only** when `COD_original_UV` is the one used.
   - If that's the case in the user's file, it's a display/UV-map selection issue: find out how it got set (an old file, a join?) and make COD2EFT enforce UV0.
   - Still useful: brie's COD export folder plus its report (reports are now saved automatically to `COD2EFT\reports\`).
3. **Unity on Park** (next). First `SYNC_TO_MY_PC.bat` (2.6.9 / 1.7.6), re-export the enc2 FBX from Blender (its log line must end with `3 skinned meshes + armature …`), copy both into `Assets\COD2EFT\enc2|enc3`, Scan each. Then *Build bundles + mod*:
   - the default hands are picked automatically;
   - the new bundle-check line reads OK/FAIL;
   - the export now uses *FBX Units Scale* (since 2.6.5), so there should be no paper-thin character.
4. **Posture (2.6.7):** re-convert a female character (valeria or Park) and look at the side view. If the head still looks forward, decide whether to build the posture session's steps 3–4.
5. **Material A/B:**
   - convert one character with *Materials* enc2 and enc3;
   - build both, or one after the other: delete the old `Assets\COD2EFT\<name>`, then Scan → Prefabs + bundles + mod;
   - compare in game next to vanilla using the Inspector's Photo / A/B sheet;
   - check skin first, then gloves, leather and metal.
6. **Inspector 0.10.0 in the hideout:**
   - Wear (incl. hideout vs menu);
   - isolate / backdrop / transparent background;
   - aim drag and reset buttons;
   - the Materials tab.
7. ✅ Dark ring: part of Park's texture. Real template: found (see the paths table).

## Decisions only the user can make
- **`_EFT` suffix:** batch export names bundles `…_eft_top`, the panel export `…_top`. Changing this renames existing bundles.
- **Test data out of git:** `from_pc/` holds GBs of exports, and every clone downloads them. Options: a separate data repo, Git LFS, or cleaning history.
- **Posture steps 3–4:** whether to build them (see above).
- **enc3 as default:** only after the A/B.
- **The rename to SPT Character Studio Suite:** parked until COD is "done" (`docs/RENAME_PLAN.md`).

## Key docs
- `docs/PROJECT_CONTEXT.md`: state, ordered queue, compatibility table, known facts, audit.
- `docs/COD2EFT_TEXTURE_SPEC.md`: the contract between Blender and Unity.
- `docs/MATERIALS_PLAN.md`: enc=3, plus the vanilla and COD measurements.
- `docs/UV_TILES.md`: the UV-tile investigation.
- `docs/SPT_INSPECTOR.md`: the in-game plugin.
- `docs/RENAME_PLAN.md`: the future rename (parked).
- `tools/`: `regress.py` (+ `baselines/`), `check_contract.py`, survey and measurement scripts.
