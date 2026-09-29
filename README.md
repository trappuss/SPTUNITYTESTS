# COD → EFT (SPT) character pipeline

This repo ports Call of Duty character models into SPT / Escape from Tarkov with as little manual
work as possible. It has two halves, and this repo is the one place both live:

| Half | Folder | Deployed on the PC to | Version |
|---|---|---|---|
| **COD2EFT**, a Blender add-on: fits COD models onto the EFT skeleton, converts weights and textures, exports FBX + PNG | `blender/COD2EFT/` | `C:\Users\notso\Downloads\Claude Current\SPTModdingTools\COD2EFT` (live install: Blender loads its code from there) | 2.6.2 |
| **EFT Tools**, Unity editor scripts (EFT Auto Prefabber + Mod Builder): materials, prefabs, bundles, SPT mod | `unity/EFTAutoPrefabber/` | `...\WTT-SDK-2022\Assets\Editor\EFTAutoPrefabber` | 1.7.2 |

**Start here:** [`docs/PROJECT_CONTEXT.md`](docs/PROJECT_CONTEXT.md) has the combined state, the
contract between the two halves, open work and the audit.

## Getting Claude's work onto your PC, and your results back

GitHub is the bridge. Claude (in the cloud) pushes to branch `claude/bold-mayer-11fzxj`, and your PC pulls from it.

**Once:** download [`GET_STARTED.bat`](GET_STARTED.bat) (open it on GitHub, then **Download raw file**)
and double-click it. It installs Git if needed, clones this repo to
`%USERPROFILE%\Downloads\Claude Current\SPTUNITYTESTS` and runs the first sync. The first time
Git talks to GitHub, it may open a browser for you to sign in.

**After that, two buttons:**

| Double-click | What it does |
|---|---|
| `SYNC_TO_MY_PC.bat` | Pulls the latest from GitHub and copies changed files into your COD2EFT folder and Unity project. Every file it replaces is first backed up to `pc\_backup\<time>\`; it never deletes anything. It checks every file by SHA-256 afterwards and re-installs the Blender add-on if the add-on itself changed. It then watches Unity's Editor.log until you click into Unity: it reports either `EFT Tools vX loaded` or the compile errors. |
| `SEND_RESULTS_TO_CLAUDE.bat` | Double-click it, or **drag reports, screenshots or folders onto it**. It collects the Unity Editor.log (tail + errors), the installed versions, any tool files that were changed on the PC (e.g. by a Cowork session) and your files. It also asks for an optional message. Everything goes to `from_pc\<time>\` and is pushed to GitHub. Then tell Claude "check from_pc". |

Both scripts repair the repo folder themselves if an earlier run failed half-way. If Claude
changed the same file in the meantime, your results go to a separate `pc-results/<time>` branch
instead of failing.

Your own per-PC files are never overwritten: `blender_path.txt`, `template_path.txt`,
`wtt_path.txt`, `cod2eft_bonemap.json`, `cod2eft_pose_tweaks.json`, and the spec on the PC.
The folder locations are asked once and kept in `pc\config.local.txt`.

**Working in Cowork (after Thursday):** the clone on your PC is a normal folder. Point Cowork
at it and at `docs/PROJECT_CONTEXT.md`. If Cowork edits the deployed copies directly, run
`SEND_RESULTS_TO_CLAUDE.bat`: it picks up every file that differs from the repo, so nothing is lost.

## Layout

```
blender/COD2EFT/        the add-on source folder, exactly as on the PC (bats, build/install scripts, docs)
  vendor/cod2eft_cast/  bundled Cast importer (MIT); was only inside the zip, now restored
  unity/COD2EFT_TEXTURE_SPEC.md   the contract between the two halves
unity/EFTAutoPrefabber/ the Unity editor scripts (+ .meta files), exactly as in Assets\Editor
unity/README.md         what the Unity tools do (materials, prefabs, bundles, caches, SPT rules)
CLAUDE.md               rules + setup for every Claude / Cowork session (read automatically)
docs/PROJECT_CONTEXT.md the handoff: state, contract summary, work queue, compatibility, audit
docs/MATERIALS_PLAN.md  material accuracy plan + measurements
docs/previews/          Blender preview renders
docs/archive/           old context dumps and per-side handoffs (history only)
tools/                  measurement scripts (vanilla bundles, COD material survey)
pc/                     scripts behind the .bat files
from_pc/                what SEND_RESULTS_TO_CLAUDE.bat uploads
```

The Blender add-on zip is no longer committed: `Install_COD2EFT_Addon.bat` builds it, and
`python blender/COD2EFT/build_addon.py` does too.
