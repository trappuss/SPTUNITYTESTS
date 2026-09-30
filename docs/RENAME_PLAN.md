# Rename plan: COD2EFT → Character Studio (project-wide)

Status: **documented for later, parked.** Nothing gets renamed until the user is fully satisfied with the COD pipeline
(user, 2026-09-30). Until then, new work follows the "Build with this in mind" rules in `CLAUDE.md`.
The aim: slowly turn the pipeline into a general tool for converting and checking characters, not only COD ones.

## Names (user's direction 2026-09-30: prefix *SPT*; "Studio" in every part may not make sense)
The umbrella / GitHub collection is **SPT Character Studio Suite**. The parts are named by what they do, without
"Studio" each time (suggested; the final pick is the user's when the rename starts):

| Part | Now | Proposed | Alternative |
|---|---|---|---|
| GitHub collection / project | COD2EFT (repo `sptunitytests`) | **SPT Character Studio Suite** | SPT Character Studio |
| Blender add-on | COD2EFT Porter (`cod2eft_*.py`, operators `cod2eft.*`, `Scene.cod2eft`) | **SPT Character Porter** (`sptchar_*.py`, `sptchar.*`, `Scene.sptchar`) | SPT Character Studio Porter |
| Unity editor tools | EFT Tools / EFT Auto Prefabber, EFT Mod Builder | **SPT Character Builder** (menu *SPT Character Studio / Auto Prefabber*, */ Mod Builder*) | keep "EFT Auto Prefabber" / "EFT Mod Builder" under the new menu |
| In-game plugin | COD2EFT Inspector (`COD2EFTInspector.dll`, GUID `com.cod2eft.inspector`) | **SPT Character Inspector** (`SPTCharacterInspector.dll`, GUID `com.sptcharacterstudio.inspector`; no `_`) | SPT Character Studio Inspector |
| Contract | `COD2EFT_TEXTURE_SPEC.md` | `TEXTURE_SPEC.md` | |
| Output folder (game) | `COD2EFT_Screenshots` | `SPTCharacterStudio_Output` | |
| Source profile for today's work | (everything) | **COD** profile (first of several) | |

The "SPT" prefix also avoids the clash with Autodesk 3ds Max's *Character Studio* (Biped) feature.

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

## Open questions (for when the rename starts, not now)
1. Final part names: the "Proposed" or the "Alternative" column above.
2. Unity: keep the folder and class names (recommended), or rename and delete the old folder once?
3. Any Blender keymaps / quick favourites on COD2EFT operators?
4. The GitHub side: rename this repo, or start the suite as a new repo / organisation and move the parts over?
