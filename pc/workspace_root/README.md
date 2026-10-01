# SPTModdingTools — workspace map

Everything for the COD → EFT (SPT) character pipeline on this PC lives here (since 2026-10-01; the repo used to be
`Downloads\Claude Current\SPTUNITYTESTS`).

| Folder / file | What it is | Who edits it |
|---|---|---|
| `SPTUNITYTESTS\` | **The git repo** (github.com/trappuss/SPTUNITYTESTS, branch `claude/bold-mayer-11fzxj`). Source of truth for the Blender add-on, the Unity tools, the SPT Inspector plugin, docs and test tools. | Claude (Cowork or cloud), through git |
| `COD2EFT\` | The **deployed** Blender add-on. Blender loads its code from here (live install). `reports\` = conversion reports the panel saves. | Only `SYNC_TO_MY_PC.bat` (copies from the repo). Don't edit by hand. |
| `Testing\` | COD exports (one folder per character) and the conversions made from them (`EFT_Converted*` inside). `Testing\CUSTOM\` = the EFT templates (`EFT BASIC [Template].blend`). | You |
| `_archive\` | Files moved out of the way during clean-ups. Nothing was deleted; each subfolder says why. | — |
| `SYNC_TO_MY_PC.bat` | Get the latest tools: pulls the repo and deploys to `COD2EFT\` and the Unity project. | — |
| `SEND_RESULTS_TO_CLAUDE.bat` | Send logs / reports / screenshots to a cloud session (and push Cowork's commits). Drag files onto it to include them. | — |
| `BUILD_SPT_INSPECTOR.bat` | Build + install the in-game COD2EFT Inspector plugin. | — |

The three `.bat` files here are shortcuts to the real ones in `SPTUNITYTESTS\`.

**Elsewhere on the PC** (not moved):
- Unity project: `C:\Users\notso\Desktop\RIP\EFT2\Tools\WTT-SDK-2022` (tools in `Assets\Editor\EFTAutoPrefabber`)
- Unity log: `%LOCALAPPDATA%\Unity\Editor\Editor.log`
- Blender 4.4.3: `G:\G Apps\Blender Builds\stable\blender-4.4.3-stable.802179c51ccc\blender.exe`
- SPT game: `G:\G Games\SPT4.1\SPT4.1 Game`

**Working with Claude**
- *Cowork* (this PC): connect this `SPTModdingTools` folder. Claude edits the repo, commits, and you run `SYNC_TO_MY_PC.bat`.
- *Cloud* (no PC): Claude works on GitHub. Run `SYNC_TO_MY_PC.bat` to get its work, `SEND_RESULTS_TO_CLAUDE.bat` to send yours.
- Both read `SPTUNITYTESTS\CLAUDE.md` first.
