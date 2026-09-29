# COD2EFT Inspector (SPT client plugin)

Source: `spt_mod/COD2EFTInspector/`. Changelog there. Queue item 10 in `docs/PROJECT_CONTEXT.md`.

## Install / update
1. `SYNC_TO_MY_PC.bat` (pulls the source; the plugin itself is not deployed by the sync).
2. `BUILD_SPT_INSPECTOR.bat`: asks once for the SPT game folder (kept as `SPT_GAME` in `pc\config.local.txt`),
   installs the .NET 8 SDK with winget if there is none, builds against the PC's own
   `EscapeFromTarkov_Data\Managed\UnityEngine*.dll` and `BepInEx\core\BepInEx.dll`, and copies
   `COD2EFTInspector.dll` to `<SPT game>\BepInEx\plugins\COD2EFTInspector\`. Build log: `pc\_build\inspector_build.log`.
3. After playing: `SEND_RESULTS_TO_CLAUDE.bat` also sends `BepInEx\LogOutput.log`, the build log and every file in
   `<SPT game>\COD2EFT_Screenshots\` that is new since the last send (files over 90 MB are skipped: use a lower supersize).

## Use
| Key (default; change in F12 ConfigurationManager → *COD2EFT Inspector*) | |
|---|---|
| F9 | panel (frees the mouse in raid / hideout; clicks still reach the game, so lower your weapon) |
| F10 | screenshot |

Panel: `<` `>` switch between characters found (you, menu previews; bots only with *Include other players*).
Groups: Head, Top, Pants, Hands (first-person), then `Gear: <item>` and `Other: …`. The checkbox on a group line
shows/hides the whole group; `+`/`-` folds it. *Hide gear* hides everything that is not a body part.
Flags: `[inactive]` = the game has this mesh switched off right now (e.g. the top's armor/vest alternative mesh),
`[off]` = renderer disabled, `[shadow only]` = drawn only into shadows (first-person body).

**Outfits tab (0.2.0, read-only):** every top / pants / head / hands from vanilla and from each mod (WTT
`db/CustomClothing`, `db/CustomHeads`), filter by text / part / source. WORN = on the shown character. BUNDLE MISSING =
the bundle file isn't where the server will look (mods: `<mod>\bundles\<path>`; vanilla: `StreamingAssets\Windows\<path>`).
*Write catalog to file* saves the list with notes. The server folder is found automatically (the game folder, one or two
levels inside it, or next to it); otherwise set *4. Outfits / Server folder* in F12.

Output in `<SPT game>\COD2EFT_Screenshots\`: `<time>_<top+pants+head>.png` + `.txt`, and `<time>_…_materials.txt`.

**Seeing your own body in raid:** EFT is first-person only; the panel hides meshes, it doesn't move the camera.
Use the hideout, the menu character preview, or a freecam mod (not bundled; check the SPT Forge for one that
supports 4.1.x). A fixed photo camera is stage 4.

## How it finds things (and what can break)
All game classes are reached by reflection by name (`Game.cs`); the project references only Unity and BepInEx, so
an EFT update can't break the build, only a lookup, which is logged once as a warning.
- Local player: `Comfort.Common.Singleton<EFT.GameWorld>.Instance.MainPlayer` (as SPT's own modules do). Works in raid
  and, per SPT's `MergeScavPmcQuestsOnInventoryLoadPatch` (`MainPlayer.Location == "hideout"`), in the hideout.
- Characters: every active `EFT.PlayerBody` component. One with no `EFT.Player` above it is a menu preview
  (`EFT.UI.PlayerModelView`). **Menu preview: designed to work, unverified.**
- Body parts: `PlayerBody.BodySkins` (dictionary part → `EFT.Visual.LoddedSkin`); renderers = the skin's children plus
  its `_lods` (vanilla prefab layout, see `EFTAutoPrefabCore.cs` header). Gear: all other renderers under the player,
  grouped by the top-most object named `item_…` / `weapon_…` / `…equipment…` (a hunch about EFT's item object names;
  the first log shows the real names).
- Bundle of an outfit: loaded `AssetBundle`s that contain a prefab named like the skin object (without `(Clone)`).

## Testing in the cloud (no game, no Unity)
- `spt_mod/COD2EFTInspector/tests/compile_check.sh`: compiles the plugin with mono `mcs` against public reference DLLs
  (Unity 2021.3 modules from NuGet, BepInEx 5.4.23.2). Needs `apt-get install mono-mcs`. mcs has no C# 7 type
  patterns (`x is T t`), so the plugin avoids them.
- `spt_mod/COD2EFTInspector/tests/run_tests.sh`: runs the Unity-free parts (JSON reader, outfit catalog) under mono.

## Stage 2 research: outfit / head browser
Question: can the client switch to and preview any top / pants / head (sorted by the mod it comes from) without a
server round-trip?

**Findings (from public sources, not yet checked in game):**
- The data is local. The SPT 4.x server keeps vanilla customization in `<server>\SPT_Data\database\templates\customization.json`
  (the Mod Builder already reads it for hands; `<server>` = the grandparent of `user\mods`); WTT-CommonLib mods add
  theirs from each mod's `db/CustomClothing` and `db/CustomHeads` (`<server>\user\mods\<mod>\db\…`). Mod vs vanilla = which file an id comes from. A browser can read
  these files directly (the server and client run on the same PC); no server call is needed to *list* them.
- The client already previews suits it doesn't own: the trader tailoring screen (`EFT.UI.TacticalClothingView.Show`)
  renders the menu character in a suit before purchase. EFT.Transmog (github.com/szszss/EFT.Transmog, SPT 3.x/4.x)
  shows the menu preview is `EFT.UI.PlayerModelView.Show(LastPlayerStateClass(info, customization, equipment), …)`
  (6 parameters) and the in-raid body is built by `PlayerBody.Init(…)` (8 parameters: equipment is the 2nd, the profile id
  the 6th). Both take a customization set, so a client-only preview is plausible: call `PlayerModelView.Show` with a
  copy of the profile's customization where one part is swapped.
- Resources: the game only instantiates prefabs whose bundles it has loaded. Transmog adds its extra items'
  `ResourceKey`s by postfixing `Profile.GetAllPrefabPaths`. For suits, the prefab path is in the customization
  template's `_props.Prefab.path` (the bundle key), so the browser must load that bundle first (the game's
  `EasyAssets`/`PoolManager` path, or `AssetBundle.LoadFromFile` on the SPT bundle — untested).
- In raid, re-running `PlayerBody.Init` on the live player is riskier (skeleton, hands, armor-mesh state). The menu
  preview is the realistic target; in-raid swapping is a later step if needed.

**Plan (not built; needs Assembly-CSharp references or more reflection, and in-game checks):**
1. v0.1.0 logs the members of `PlayerBody`, `PlayerModelView` and `TacticalClothingView` on the first panel open;
   read them from the first `LogOutput.log` to pin the exact signatures for SPT 4.1.6.
2. ~~Read `customization.json` + every mod's `db/CustomClothing` / `db/CustomHeads` into a list~~ (0.2.0, Outfits tab).
   The vanilla file's layout (`_props.BodyPart`, `_props.Prefab.path`) is assumed from the Mod Builder's reader; the first
   catalog file from the PC confirms it.
3. In the menu: pick an entry → load its bundle → `PlayerModelView.Show` with the swapped customization. Filter by mod / vanilla.
4. Only then consider raid / hideout (Harmony patch on `PlayerBody.Init`, as Transmog does).

Sources: `github.com/sp-tarkov/modules` (commit 8dea32a: `Directory.Build.props` netstandard2.1, BepInEx 5.4.21,
`Singleton<GameWorld>.Instance.MainPlayer`), `github.com/szszss/EFT.Transmog` (commit 96688fb: `PlayerBody.Init`,
`PlayerBody.SlotViews`, `PlayerModelView.Show`, `TacticalClothingView`, `Profile.GetAllPrefabPaths`).
