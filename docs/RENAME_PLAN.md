# Rename plan: COD2EFT → Character Studio (project-wide)

Status: **plan only, nothing renamed yet** (2026-09-30). The user chose the name *Character Studio* and a project-wide scope.
The aim: slowly turn the pipeline into a general tool for converting and checking characters, not only COD ones.

## Names
| Part | Now | Proposed |
|---|---|---|
| Project | COD2EFT | **Character Studio** |
| Blender add-on | COD2EFT Porter (`cod2eft_*.py`, operators `cod2eft.*`, `Scene.cod2eft`) | Character Studio Porter (`charstudio_*.py`, `charstudio.*`, `Scene.charstudio`) |
| Unity editor tools | EFT Tools / EFT Auto Prefabber, EFT Mod Builder | Character Studio Builder (menu *Character Studio / Auto Prefabber*, */ Mod Builder*) |
| SPT plugin | COD2EFT Inspector (`COD2EFTInspector.dll`, GUID `com.cod2eft.inspector`) | Character Studio (`CharacterStudio.dll`, GUID `com.characterstudio.spt`; no `_`) |
| Contract | `COD2EFT_TEXTURE_SPEC.md` | `TEXTURE_SPEC.md` |
| Output folder (game) | `COD2EFT_Screenshots` | `CharacterStudio_Output` |

**Name clash:** Autodesk 3ds Max has a feature called *Character Studio* (Biped). That doesn't matter for private use. For
publishing, *SPT Character Studio* or *Tarkov Character Studio* is easier to search for. **Question for the user.**

## What a rename breaks, and the fix for each
The sync and build never delete, so the old files stay on the PC. The user's rule: list them and ask before removing any.

1. **SPT plugin** (the least risk: one folder).
   - The old `BepInEx\plugins\COD2EFTInspector\` would load next to the new one: two plugins patching the same methods.
     `BUILD_SPT_INSPECTOR.bat` (renamed `BUILD_CHARACTER_STUDIO.bat`) lists the old folder and asks before removing it.
   - F12 settings: on the first start, copy `BepInEx\config\com.cod2eft.inspector.cfg` to the new name if that file is
     missing. Photo presets: read the old file if the new one is missing.
   - `send.ps1` collects the new and the old output folders.
2. **Blender add-on**
   - Renaming `Scene.cod2eft` loses the settings stored in existing `.blend` files. Fix: on load, copy the old ID
     properties (`scene.get("cod2eft")`) into the new group once.
   - Renaming the operator ids (`cod2eft.*`) breaks keymaps and quick favourites (hunch: the user has none; ask).
   - PC-owned files (`cod2eft_bonemap.json`, `cod2eft_presets.json`, `cod2eft_pose_tweaks.json`): read the old name if
     the new one is missing, never overwrite.
   - The live install folder (`COD2EFT_DIR` in `pc\config.local.txt`) keeps its path. The old `cod2eft_*.py` files stay
     in it, unused; list them for the user.
   - `build_addon.py`, the batch runner, `tools/regress.py` and `CLAUDE.md`'s test recipe move to the new module names.
     Regression baselines keep their files; the new version gets a new baseline with identical numbers (a rename must
     not change output: that is the check).
3. **Unity tools** (the biggest risk).
   - Deployed to `Assets\Editor\EFTAutoPrefabber`. A new folder next to the old one = duplicate classes = compile
     errors. **Recommendation: keep the folder and the class names, change only menus, window titles and texts.**
     Otherwise the user deletes the old folder once (listed, asked).
   - Prefabs / bundles already built keep their names; nothing in the game depends on the tool's name.
4. **Docs / scripts**: README, CLAUDE.md, PROJECT_CONTEXT, changelogs (old entries stay as history), the `.bat` titles
   and PC shortcuts (`COD2EFT_Convert.bat`, `COD2EFT_To_Unity.bat`, `Install_COD2EFT_Addon.bat`: new names, old ones left in place).

Versions: a major bump each (Blender 3.0.0, Unity 2.0.0, plugin 1.0.0), recorded together in the compatibility table.

## Phases (each one shipped and checked before the next)
1. **SPT plugin**: full rename with the settings migration and the old-folder check. Self-contained, easy to test.
2. **Display names** in Blender and Unity (panel titles, menus, window titles, log prefixes) plus the docs. No identifiers
   change, so nothing can break.
3. **Identifiers** in Blender (modules, operators, scene properties with migration). A regression run must be identical.
   Unity identifiers only if the user agrees to remove the old folder.
4. **More than COD** (the real "universal" work, independent of the name):
   - **Source profiles**: today the importer, bone map and texture rules assume COD exports (Cast / SEModel via `vendor/`,
     COD bone names, COD gloss / spec packing). Split them into a profile per source: importer, bone map, texture-channel
     rules, material defaults. COD becomes the first profile.
   - A **generic FBX / glTF profile** with a bone-map editor (the `bonemap.json` mechanism exists) for rigs from any game or DCC.
   - The texture spec gets a per-profile section for how the input channels are read. The output side (EFT) stays one contract.
   - The in-game tool is already game-content agnostic, except the WTT catalog reader and the file names.

## Questions for the user before phase 1
1. *Character Studio*, or *SPT Character Studio* (clash with the 3ds Max feature)?
2. Unity: keep the folder and class names (recommended), or rename and delete the old folder once?
3. Any Blender keymaps / quick favourites on COD2EFT operators?
