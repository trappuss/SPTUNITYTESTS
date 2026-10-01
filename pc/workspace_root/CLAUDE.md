# CLAUDE.md — SPTModdingTools workspace (Cowork entry point)

This folder is the user's whole COD → EFT workspace. `README.md` here is the folder map.

**Read next:** `SPTUNITYTESTS/CLAUDE.md` (rules — they apply here), then `SPTUNITYTESTS/docs/COWORK_HANDOFF.md`
(paths, how to work, state, test list), then `SPTUNITYTESTS/docs/PROJECT_CONTEXT.md` as the task needs.

Short version of the rules: thorough and correct beats fast; evidence, not hunches (label hunches); concise reports;
anything the user runs is a `.bat`; every change bumps a version + CHANGELOG line; say what was and wasn't verified;
never delete or overwrite another SPT mod's files.

## Where to edit
- Code and docs: **only in `SPTUNITYTESTS/`** (the git repo). Commit there.
- `COD2EFT/` is a deployed copy written by `SYNC_TO_MY_PC.bat`; edits there are overwritten (the sync backs them up first).
- `Testing/` is the user's data. Read it; write conversions only into `EFT_Converted*` subfolders.
- Anything you clear out goes to `_archive/<date>_<reason>/`, not the bin.

## Cowork mechanics (learned 2026-10-01)
- **First command of a session:** `python3 SPTUNITYTESTS/tools/pc_status.py` (in `$HOME/mnt/SPTModdingTools`): unpushed
  commits, repo vs deployed versions, Unity's last load / compile errors, newest send and report.
- `device_bash` runs in a Linux VM; this folder is `$HOME/mnt/SPTModdingTools`. It cannot run `.bat`/PowerShell, has
  no Blender (`bpy` doesn't install on its Python 3.10) and no GitHub login.
- git in the VM: `git -c safe.directory='*' ...`. Git must be able to delete `.git/index.lock`: ask for delete
  permission on this folder once per session (device_request_delete_permission), or every git command leaves a lock
  that blocks the user's sync.
- **Pushing:** run `SPTUNITYTESTS/PUSH_TO_GITHUB.bat` (no prompts, uses the user's Git login; result in
  `_xfer/push_result.txt`). With computer use you can start it yourself: File Explorer is grantable at the *click* tier,
  so open the repo folder and double-click the .bat (bring Explorer to the front first). Tested 2026-10-01.
- **Commits can't be pushed from the VM.** The sync deploys from the local clone anyway (it does `pull --rebase`, which
  keeps local commits). `SEND_RESULTS_TO_CLAUDE.bat` pushes them. Tell the user when a commit is still unpushed.
- To test exactly what is in the PC clone (including unpushed commits) in the cloud container: `git bundle` (see
  `SPTUNITYTESTS/CLAUDE.md` → *Moving commits between the PC clone and a cloud workspace*). Bring cloud commits back the
  same way and `git pull --ff-only` them on the PC.
- Blender tests: run in the cloud container (`bpy` 4.4 venv; see `SPTUNITYTESTS/CLAUDE.md` → *Testing in a cloud
  session*). Stage only the files needed. The real template is `Testing/CUSTOM/EFT BASIC [Template].blend`.
- Unity's `Editor.log`: request folder access to `C:\Users\notso\AppData\Local\Unity\Editor` (mounted as
  `$HOME/mnt/Editor`). Unity and the game can't be driven from here; ask the user for those steps.
- Copying big folders through the VM is ~7 MB/s and each call stops at 180 s: use `rsync -a` in repeated calls, then
  verify with `rsync -anc` (an interrupted big file can come out wrong — re-copy it and `cmp`).
