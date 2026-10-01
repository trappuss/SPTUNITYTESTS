# CLAUDE.md: COD → EFT (SPT) character pipeline

**Working in Cowork on the user's PC?** Start with `docs/COWORK_HANDOFF.md` (paths, how to work, sessions so far, test list). On the PC this repo is `Downloads\Claude Current\SPTModdingTools\SPTUNITYTESTS`, inside the user's workspace folder; that folder's `CLAUDE.md`/`README.md` (copies in `pc/workspace_root/`) cover the Cowork mechanics.

Read `docs/PROJECT_CONTEXT.md` first: current state, the work queue in order, version compatibility, known facts, the audit. Then read only what the task needs.

## The project
- **COD2EFT** (`blender/COD2EFT/`) is a Blender add-on. It fits Call of Duty character exports onto EFT's skeleton, converts weights and textures, and exports FBX + PNG.
- **EFT Tools** (`unity/EFTAutoPrefabber/`) are Unity 2022.3 editor scripts for the WTT-SDK: materials, prefabs, bundles and the SPT mod.
- It used to be two separate sessions. It is **one project now**, and either side may be changed. Older text that says "the other session owns X" or "the user relays messages" is history.
- **The contract** between the halves is `docs/COD2EFT_TEXTURE_SPEC.md`: PNG channels, slot and object names, material values. Change both sides together, and record which versions go together in the compatibility table in `docs/PROJECT_CONTEXT.md`.

## The user's rules
- **Thorough and correct beats fast.** Work from evidence. Label hunches as hunches and ask about them.
- **Keep reports concise.** The user is on limited credits: work efficiently, use targeted reads and few subagents.
- **Anything the user runs is a `.bat`**, as automated as possible.
- **Never delete or overwrite another SPT mod's files.** List them and ask first.
- **Every change bumps a version and adds a CHANGELOG line:**
  - Blender: `VERSION` in `cod2eft_porter.py` must equal `"version"` in `addon_init.py` (`build_addon.py` refuses a mismatch). Changelog: `blender/COD2EFT/CHANGELOG.md`.
  - Unity: `EFTToolsVersion.Version`. Changelog: `unity/EFTAutoPrefabber/CHANGELOG.md`.
  - Also update the versions table in the root `README.md`, and tell the user which version to expect.
- **Say what was verified and what wasn't.** Unity can't run in the cloud, and neither can the game.

## How changes reach the user's PC
- Push to branch **`claude/bold-mayer-11fzxj`**.
- The user runs `SYNC_TO_MY_PC.bat`. It deploys `blender/COD2EFT/` to their COD2EFT folder (a live install) and `unity/EFTAutoPrefabber/` to `WTT-SDK-2022\Assets\Editor\EFTAutoPrefabber`. It backs up replaced files, never deletes anything, and verifies every file by SHA-256.
- The user sends results back with `SEND_RESULTS_TO_CLAUDE.bat`. They land in `from_pc/<time>/` (Editor.log, reports, screenshots, files changed on the PC). **Never edit or commit inside `from_pc/`**; only read it.
- Files the sync once deployed stay on the PC even after they leave the repo. The repo is the source of truth.
- `.gitattributes` is `* -text`: files are stored exactly as used on Windows (`.bat`/`.ps1` with CRLF). Keep new `.bat`/`.ps1` files CRLF.

## Testing in a cloud session (Linux, no GPU, no Unity)
- **Blender as a Python module:**
  ```
  python3.11 -m venv venv
  pip download bpy==4.4.0 --no-deps -d wheels
  pip install wheels/bpy-*.whl "numpy<2" pillow UnityPy
  ```
  Headless `import bpy` works. Rendering does not (no EGL); plot with PIL instead.
- **Template:** rebuild it from `from_pc/20260928-231514/attached/EFT BASIC [Template].fbx` with `bpy.ops.import_scene.fbx(filepath=..., ignore_leaf_bones=True)`. It must give 58 bones on `Body EFT Armature`. Save it as `EFT BASIC [Template].blend`. It is not byte-identical to the user's .blend, so say so when reporting. The user's real one is on the PC at `SPTModdingTools\Testing\CUSTOM\EFT BASIC [Template].blend` (77 MB, not in git); ask for it via `SEND_RESULTS_TO_CLAUDE.bat` when a test needs it.
- **Running the add-on:**
  1. Copy `blender/COD2EFT/*.py` and `vendor/` into a folder `cod2eft/`, with `addon_init.py` renamed to `__init__.py`.
  2. Put its parent folder on `sys.path`.
  3. Run `cod2eft_batch.py` with `runpy`, setting `sys.argv = ["blender", "--", "--template", T, "--out", OUT, "--cast", FOLDER]`. Add `--no-textures` for fit-only runs.
- **Test data:**
  - COD exports: `from_pc/20260928-223033/attached/` (Kleo MW2, sunflower_base BO7, MW4 Beta Male) and `from_pc/20260928-230805/attached/` (MW4 Beta Female / valeria).
  - Vanilla EFT bundles: `from_pc/20260928-222836/attached/`.
  - Park 24_1 output that worked in game: `from_pc/20260928-221326/attached/`.
- **Regression:** run `python tools/regress.py --out /tmp/reg --baseline tools/baselines/cod2eft_<ver>.json` (Python with bpy). It converts all 4 test characters with the add-on in the repo and compares the fit numbers, parts, mesh counts and PNG hashes. Exit code 1 means something changed, and the output lists exactly what. Output is nearly deterministic: a repeat run can change a PNG's hash by 1-bit float noise on a few pixels; regress.py (2026-10-01) then compares 16 × 16 block means and prints a `note:` instead of a difference. Latest baseline: `tools/baselines/cod2eft_2.6.11.json` (enc2, with PNG signatures). Add `-- --material-mode enc3` for the enc3 path, and `--save-baseline` after an intended change. `tools/cod_survey.py` re-measures every COD material.
  - `python tools/check_contract.py` (no bpy) checks that the Blender preview's material numbers and cutoff match Unity's.
- **C#:** there is no compiler, so check by reading. Mirror regexes and maths in Python where useful. Say it is uncompiled.
- **SPT client plugin** (`spt_mod/COD2EFTInspector/`): `tests/compile_check.sh` compiles it with mono `mcs` (`apt-get install mono-mcs`) against public Unity/BepInEx reference DLLs; `tests/run_tests.sh` runs the Unity-free tests. mcs lacks C# 7 type patterns, so avoid `x is T t`. See `docs/SPT_INSPECTOR.md`.
- **PowerShell** (`pc/*.ps1`): must run on Windows PowerShell 5.1. Parse-check with pwsh if available. Under `$ErrorActionPreference = 'Stop'`, don't redirect native stderr (`2>$null`).

## Build with the future in mind (the user, 2026-09-30)
The project will become **SPT Character Studio Suite**: not only COD sources, parts named "SPT Character Porter / Builder /
Inspector" (`docs/RENAME_PLAN.md`). **Parked:** nothing gets renamed until the user is fully satisfied with COD. Until then:
- Don't rename existing files, identifiers, GUIDs or folders on your own. Renaming follows the plan's phases and migrations.
- **New code keeps COD-specific knowledge in one place** (COD bone names, Cast / SEModel import, COD texture packing,
  COD material names): its own function / module / table, named as COD. Game-neutral steps (fitting, weights, EFT
  output, the contract, the in-game tools) shouldn't assume COD.
- New user-facing text in generic features says "source" / "character", not "COD", unless it is COD-specific.
- New identifiers in neutral code avoid a new `cod2eft` prefix where the file's own convention allows it. New GUIDs,
  folders and settings files need a migration note in `RENAME_PLAN.md`.
- When a change would make the later rename harder (a new PC-owned file name, a new saved setting), add a line to the
  plan's "What a rename breaks" list.

## Known traps
- `bpy.ops` re-uses an operator's last-used values within a session, so pass every setting explicitly (see `export_fbx`).
- Windows MAX_PATH: COD exports have 260+ character paths. See the long-path import in `cod2eft_porter.py` and `core.longpaths` in the PC scripts.
- SPT: any `_` in a mod GUID stops all mods loading. Bundle keys are global across mods.
- The Mod Builder lists a bundle for as long as its prefab is in `Assets`.
