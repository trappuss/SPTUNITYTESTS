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
| F9 | panel (frees the mouse in raid / hideout; since 0.7.0 the character takes no look / aim / fire / walk input while it is open: *3. Panel / Block game input while open*) |
| F10 | screenshot |

**Panel (0.8.0):** title bar (location, A- / A+ text size, × close; drag by the title, resize from the bottom-right corner),
tabs *Try on* · *Photo* · *Meshes* · *Catalog*, a status bar at the bottom (hover help, last message coloured, click for the
full text). Styles in `Ui.cs`.

Meshes tab: `◄` `►` switch between characters found (you, menu previews; bots only with *Include other players*).
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

**Try on (0.4.0, Outfits / try on tab):** in the hideout or a raid, *Wear* puts a catalog entry (or a whole mod outfit
set, newest mod first) on your character, client-side only, not saved; *Restore my outfit* undoes it. The intended flow:
install the mod built by the Mod Builder → start the game → hideout → open the panel (F12 → *0. Inspector* or F9) → the
newest mod's outfit is at the top → *Wear* → Photo tab. Option *Auto-wear newest mod outfit in the hideout* skips the
click. *Save this head to my profile* keeps a head after restart when the WTT HeadVoiceSelector server mod is installed.
Tops / pants are not saved: SPT only lets a profile wear suits it owns (the trader), so saving them is a separate question.

**Menu try-on and poses (0.6.0):** *Wear* also works on the main menu's Character / Inventory preview (re-shows the
game's own preview call with the try-on ids). The Photo tab has pose buttons (stand, crouch, low crouch, prone, aim; the
game's animations) and *Pose turntables* (5 poses x 4 angles) for clipping checks.

**Photo tab (0.7.0):** sections *Camera*, *Character*, *Lights*, *Background*, *Captures*, each with a reset button, *R* on each
slider, *Reset all* at the top (photo mode off with everything restored, meshes shown, pose stand, outfit restored).
Mouse outside the panel: right drag orbits the camera, left drag turns the character (yaw) and its aim (pitch ±60°), wheel zooms.
The camera stays fixed in the world while the character turns; angle presets / turntables are relative to the character's front.
*Background → Isolate character*: every renderer not under your player (and terrain) gets `forceRenderingOff`, the camera clears
to a solid colour (picker; default chroma green #00B140). Camera effects whose names suggest fog / sky / scattering are switched
off (option), optionally all post effects and all world lights (studio lights only). Everything touched is recorded and restored.
*Transparent PNG*: difference matting (black pass + white pass with `Time.timeScale = 0` between them, alpha from the difference,
background levels read from the image corners), so it doesn't depend on EFT's post-processing keeping alpha, which is unknown.
Supersize is capped at 2x for it.
Hunches to check in the log: the camera component list (which effects exist), `Player.Rotate(Vector2)` for the character turn,
the up/down sign of the aim (F12 *Invert aim drag*).

**Bundle loading (0.7.1, `BundleLoader.cs`):** SPT 4.1.6 has `EFT.ObjectsFactory.LoadBundlesAndCreatePools(Pools pools,
List<PoolResourceInfo> resources, AssemblyType assemblyType, YieldDelegate yield, IProgress progress, CancellationToken ct)`
(0.7.0 log). Arguments are built by type and every choice is logged. If loading fails but every bundle is already loaded, Init
runs anyway. Verified in game so far: photo mode, isolate and transparent PNG (0.7.0 screenshots). The hideout body = the profile's
outfit (the body log).

**Play mode (0.9.0):** in photo mode a double-click outside the panel (or the *Play mode* button) gives the character back
to the player (`InputBlock` released, cursor locked). The photo camera keeps orbiting and follows the character's facing.
Esc / F9 returns to photo mode and re-bases the character's facing (`PhotoMode.Rebase`). Hands: `Catalog.HandsOfTop`
(vanilla suites `_props.Body` -> `_props.Hands`, WTT topId -> handsId).

**Materials tab (0.10.0, `MaterialTuner.cs`):** shader properties via `Shader.GetPropertyCount/Name/Type/RangeLimits`, edits on the
loaded material with the old value stored at the first change. *Save tuning* -> `<time>_<outfit>_material_tuning.txt` in
`COD2EFT_Screenshots` (sent by SEND_RESULTS). This is the in-game route for tuning the values in `COD2EFT_TEXTURE_SPEC.md`
(copy the numbers into the converter / EFT Tools; not automatic). Channel view = unlit copies with one texture slot as `_MainTex`.
**Photo tab (0.10.0):** *Time* (F7 freeze / F8 slow motion, `Time.timeScale`, restored on exit), *Presets*
(`BepInEx\config\COD2EFTInspector_photo_presets.txt`, one `name|key=value;...` line each). A/B writes `<time>_AB_sheet_<mod>.png` (`Sheet.cs`).

**Try-on diagnostics (0.7.0):** a Wear logs its target body, the loader arguments with types, per-bundle results on failure, and
every `PlayerBody` in the scene (owner, active, what it shows). If the game rebuilds your body afterwards, a warning says so.
*Log all bodies* button in the try-on section.

**A/B (0.5.0, Photo tab):** one click wears every outfit of the newest mod in turn and takes a turntable of each, then
one of your own outfit as the reference: same camera, same lights. The material report's *Checks* section flags
shader / stencil / texture-slot problems automatically.

**Photo mode (0.3.0, Photo tab or F12 → *0. Inspector*):** in raid or the hideout the camera orbits your own character
(third person, the character takes no input, HUD hidden). Angle and framing presets, sliders, right-mouse drag / wheel,
studio lights (key / fill / rim; follow the camera or stay fixed to the character), and *Turntable* (4 screenshots).
For the same light every time use the hideout with the studio lights. The recipe is the open-source SPT Freecam's
(`github.com/acidphantasm/SPT-Freecam`): `Player.PointOfView = ThirdPerson`, `PlayerBody.PointOfView.Value = FreeCamera`,
`PlayerCameraController.UpdatePointOfView()`, `GamePlayerOwner.enabled = false`, `CameraManager.ForceSetPosition` blocked.
Don't use it at the same time as another camera mod (Freecam, CineKit): they take over the same camera.

**F12:** ConfigurationManager → *COD2EFT Inspector* → *0. Inspector* has *Open Inspector panel* and *Photo mode* buttons;
the hotkeys can be set to None.

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
- `tools/contact_sheet.py from_pc/<time>/COD2EFT_Screenshots`: one labelled grid of a send's screenshots (needs Pillow).
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

**Update 2026-09-29, from the first PC log and Improved Customization UI** (github.com/hjal-dev/Improved-Customization-UI, MIT,
the mod the user linked; commit aa520af):
- SPT 4.1.6 signatures (0.1.0 research dump): `PlayerBody.Init(BodyCustomization, int layer, EPlayerSide)` (a short
  overload), `PlayerBody.BodyCustomization`, `PlayerModelView.Show(Profile, InventoryController, Action, float, Vector3?, bool)`
  and `Show(PlayerVisualRepresentation, …)`, `TacticalClothingView.UpdateCustomization(EBodyModelPart, string id)` / `OnTestFit`.
- That mod adds a CUSTOMIZATION tab to the inventory screen and previews suits **client-side**: it copies a suite into a
  preview profile (`UpperBodySuit/LowerBodySuit.SetClothingsToProfile(previewProfile.Customization)`) and calls
  `PlayerProfilePreview.Show(previewProfile)` (the character-creation `HeadSelectionState` preview, with its own camera,
  lights and animations). The server is only called on Save. So a client-only preview is confirmed possible.
- Its lists come from `solver.GetAvailableSuites(side)` / `GetAvailableHeads`: **only what the profile already owns**.
  For testing converted outfits the user can use that mod as it is (buy/unlock the COD outfit once), or the Inspector's
  browser can feed catalog entries (any id) into the same preview path. Preferred next step: reuse its approach for
  catalog entries, not a separate preview scene. Its preview lighting (`PreviewLighting.cs`: scales the preview's own
  light rig and adds one directional light) is the "controlled lighting" reference for the menu.

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
