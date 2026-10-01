# Cowork handoff (2026-10-01)

For the Cowork session on the user's PC. Read `CLAUDE.md` first (rules), then this file, then `docs/PROJECT_CONTEXT.md` (the full queue, facts and audit).

## Where everything is (on the PC)
| What | Path |
|---|---|
| **This repo (the source of truth)** | `%USERPROFILE%\Downloads\Claude Current\SPTUNITYTESTS`, branch `claude/bold-mayer-11fzxj` |
| COD2EFT, deployed (Blender live install) | `C:\Users\notso\Downloads\Claude Current\SPTModdingTools\COD2EFT` |
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
| COD2EFT (Blender) | **2.6.8** | headless Blender 4.4 + regression harness; the user last ran 2.6.6 |
| EFT Tools (Unity) | **1.7.5** | not compiled (cloud); the user last loaded 1.7.3 |
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

## Test list for the user (in this order; Cowork can drive most of it)
1. **Sync and build.** Close Blender, run `SYNC_TO_MY_PC.bat`, then `BUILD_SPT_INSPECTOR.bat`. Expect COD2EFT 2.6.8, EFT Tools 1.7.5 (`[EFT Tools] v1.7.5 loaded` in Editor.log) and Inspector 0.10.0.
2. **brie "face on the neck".** In Blender, select brie's Upper and check which UV map has the *camera* (render) icon in Object Data → UV Maps.
   - `docs/UV_TILES.md` reproduces the face-on-neck look **only** when `COD_original_UV` is the one used.
   - If that's the case in the user's file, it's a display/UV-map selection issue: find out how it got set (an old file, a join?) and make COD2EFT enforce UV0.
   - Still useful: brie's COD export folder plus its report (reports are now saved automatically to `COD2EFT\reports\`).
3. **Unity on Park**, using *Build bundles + mod*:
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
7. **Still open:** the dark ring at Park's neck; the user's real `EFT BASIC [Template].blend` (cloud tests use one rebuilt from the FBX).

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
