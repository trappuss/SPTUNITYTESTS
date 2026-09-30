# COD2EFT Inspector changelog

The version is `InspectorPlugin.Version` in `Plugin.cs` (the build script passes it to the DLL). It shows in the
panel title and in the BepInEx log (`COD2EFT Inspector v… loaded`).

## 0.9.1 (2026-09-30)
User: 0.9.0 checked in game and working (isolate with gear, hands, catalog order, play mode).
- **Play mode: camera turns with the character** (Photo tab under *Play mode*, and F12 *5. Photo mode*; on by default). Off:
  the camera keeps its angle in the world while you walk and turn. Changing it in F12 applies at once, even during play.
- **Esc from play mode** now always brings the panel back. 0.9.0 reopened it only if it had been open when play mode
  started, so a double-click with the panel closed left you with no panel.
- Leaving play mode keeps the camera exactly where it was (the orbit angle is re-based with the character's new facing).

## 0.9.0 (2026-09-30)
User feedback on 0.8.0 (no new log; compiled with mcs only, untested in game):
- **Isolate hid the gun / gear**: held and slung items aren't always children of the player object. Isolate now also keeps
  every renderer on the character's layers (the layers its own meshes use, plus `Player` / `Weapon`) within 2.5 m. Any other
  object within 2.5 m that it hides is logged by path and layer, so a missing piece can be named from one log.
- **Tops didn't change the hands**: a top only found its hands through a mod set's name, so vanilla tops brought none. The
  catalog now reads the pairing from the suites in `customization.json` (`_props.Body` + `_props.Hands`) and from WTT
  `topId` / `handsId`.
- **Hands on their own**: hands entries now have *Wear*. Option *Tops bring their hands* (Catalog tab / F12 *4. Outfits*,
  on by default): off means a top leaves the hands alone.
- **Catalog keeps its order**: wearing something no longer moves it to the top (your place in the list stays). What you
  wear is pinned in a *Wearing now* box above the list and still tagged WORN in it.
- **Play mode** (photo mode): a double-click outside the panel (or the *Play mode* button) gives full control of the
  character (walk, shoot, reload, inspect) while the photo camera keeps orbiting it and follows its facing. The panel
  hides; a banner says "PLAY MODE · Esc to leave". Esc (or F9) goes back to photo mode, and the character keeps where
  it faces. Setting *5. Photo mode / Double-click to play* (on). Esc may also reach the game's own menu: not checked.

## 0.8.0 (2026-09-30)
Panel redesign (user: hard to see and navigate). No change to what the buttons do. Compiled with mcs only; the look is unchecked in game.
- New `Ui.cs`: one dark theme, bigger text (13 px base), flat buttons, accent colour for the main action, cards for sections.
- Title bar: where you are (Hideout / Raid / Main menu, PHOTO MODE), **A- / A+** text size, **×** close. Drag it by the title
  bar; resize from the bottom-right corner. The panel stays on screen.
- Four tabs: **Try on** (opens first: what Wear acts on, Restore, open photo mode, the newest mod outfits as rows with a Wear
  button and a WORN tag; head saving and diagnostics folded), **Photo**, **Meshes**, **Catalog** (search box, part buttons,
  source selector; entries show name, part · source · id and WORN / BUNDLE MISSING tags; Reload / Write to file below).
- Photo tab: Start / Screenshot / Reset all always on top, sections as cards you can fold (Camera, Character, Lights,
  Background, Captures), each with *Reset*. Angle, framing, pose and background colour are button rows with the current
  one highlighted. Sliders show their value and have an *R* reset.
- Status bar at the bottom: hover help for the control under the mouse, otherwise the last message in green / amber / red.
  Click it for the full text.
- Default panel size 600 x 720.

## 0.7.1 (2026-09-30)
From the 0.7.0 log (from_pc/20260929-195957):
- **Wear, the real cause**: the plugin found only `EFT.ObjectsFactory.LoadBundlesAndCreatePools(Pools, List<PoolResourceInfo>,
  AssemblyType, YieldDelegate, IProgress, CancellationToken)` and passed `null` for `pools`, `resources` and `yield`. The resource
  list was built only as an array, so a `List<>` parameter got null ("Value cannot be null, parameter 'source'").
  New `BundleLoader.cs`: builds every argument by type. Resources: a `List<T>` or array of T made from the bundle path (T's `path`
  member, a constructor that takes a path / ResourceKey, or a ResourceKey member). Class / delegate arguments come from the
  game's own static instances (found by type, each choice logged). Every loader found is tried in turn. The first use logs the
  members of `PoolResourceInfo`, `Pools` and `YieldDelegate`, so a wrong guess is fixable from one log. If loading still fails
  but every bundle is already loaded (e.g. the outfit you own and wear), the body is rebuilt anyway. Untested in game.
- Hideout vs menu, answered by the body log: your hideout character already wears the RCTA COD outfit (your profile owns it),
  the same as the main-menu preview. The earlier difference was Wear failing.
- **Panel**: resizable (drag the bottom-right corner; size kept in F12 *3. Panel*), **X** close button, long messages shortened
  (full text in the log).
- **Photo**: *Orthographic* toggle in the Camera section (size follows distance and FOV, so the framing stays; *Reset camera*
  turns it off).
- **Transparent PNG**: worked in 0.7.0 (93 % of the frame transparent, clean edges), but the character came out 2-8 %
  see-through: the white pass bloomed onto it. The second pass is now grey, and alpha above 0.96 counts as solid.
  *All post effects off* gives the exact colours.

## 0.7.0 (2026-09-29)
User feedback on 0.6.0 (compiled with mcs only; nothing below tested in game yet):
- **Wear failed with "Value cannot be null"** (no log of it was sent; the only BepInEx log in `from_pc/` is from 0.2.0).
  Likely cause, a hunch until the next log: the bundle loader's priority argument is a class in EFT 0.16
  (`JobPriorityClass.Immediate`), not an enum, and 0.6.0 passed `null` for it. It now takes the class's own static
  `Immediate` / `General`. Diagnostics: every loader argument is logged with its type (a null one by name). A catalog entry
  without an id or bundle path is refused, naming it. If loading fails, each bundle is loaded alone and the message
  names the failing ones (part, name, bundle path). The full exception goes to the log, and the panel shows the type,
  the null parameter's name and the first stack frames.
- **Hideout vs menu**: the hideout outfit most likely didn't change because Wear failed there (the hideout loads the
  bundles itself; the menu preview lets the game load them, so it worked). Diagnostics: after every Wear, all
  `PlayerBody` objects are logged (owner: your player / a menu view, active, what each shows, which one the try-on
  targeted). A warning appears if the game rebuilds your body afterwards (try-on overwritten). There is also a *Log all
  bodies* button. A Wear in the hideout now also updates the inventory screen's preview when it is open.
- **Panel blocks game input**: while the panel is open in raid / hideout, the character takes no look, aim, fire or
  walk input (`GamePlayerOwner` off; walking and the trigger are stopped once). The old state comes back on close.
  Option *3. Panel / Block game input while open* (on).
- **Photo mode background** (*Background* section): *Isolate character* hides every other renderer and terrain
  (`forceRenderingOff`, restored exactly) and clears the camera to a solid colour (colour picker, default chroma
  green, kept in config). Options: fog / sky / scattering camera effects off (on by default), all post effects off,
  world lights off (studio lights only). *Transparent PNG*: the same frame on black and on white (time stopped for the
  2 frames), alpha from the difference. It doesn't rely on EFT's post stack keeping alpha (unknown; not checked).
  The first photo mode logs the camera's components.
- **Character turn and aim**: left-drag turns the character (yaw) and its aim (pitch, ±60°); right-drag still orbits.
  *Turn* / *Aim up/down* sliders; aim is levelled when photo mode starts; the camera no longer turns with the
  character; angle presets and turntables stay relative to the character's front. Uses `Player.Rotate(Vector2)` (the path
  mouse input takes), else a writable `Rotation`; which one is logged. Up/down sign is a hunch: *Invert aim drag* in F12.
- **Resets**: *R* on each slider, *Reset camera / character / lights / background* per section, *Reset all* (photo
  mode off with everything restored, meshes shown, pose stand, defaults, outfit restored / menu preview re-shown).

## 0.6.0 (2026-09-29)
- **Try-on in the main menu**: *Wear* also works on the Character / Inventory screen preview. A Harmony prefix records the
  game's own `PlayerModelView.Show(...)` call; try-on puts the ids into that profile, calls the same Show again (the game
  loads the bundles itself), then puts the real ids back. If the view skips an identical subject it is closed and shown
  again. *Restore my outfit* shows the real outfit again. Approach as in Improved Customization UI.
- **Poses** (Photo tab): Stand, Crouch, Low crouch, Prone, Aim, the game's own animations driven through the player's
  movement state (`MovementContext.SetPoseLevel`, prone, `HandsController` aiming). *Pose turntables*: 4 angles in each
  of the 5 poses, for clipping checks; file names carry the pose. The member names are hunches: the first use logs the
  members that exist, so a wrong guess is fixable from one log.

## 0.5.1 (2026-09-29)
Review fixes (found by reading; nothing tested in game yet):
- Try-on no longer leaves the try-on ids in your profile: as soon as the body is rebuilt (or on any failure) the
  profile's `Customization` gets its real ids back, so a try-on can't reach the server (SPT sends profile data back at
  raid end: hunch, not checked, but not worth the risk). The try-on is tracked by the plugin; reports mark it
  "(try-on, not saved; real: …)". The original outfit is remembered per profile (PMC / scav / new session).
- Photo mode that ends by itself (hideout or raid left while it was on) now brings back the UI canvases it hid; some of
  them belong to the menu, which could otherwise stay unclickable.
- `tools/contact_sheet.py`: turns a send's screenshots into one labelled grid (turntables as rows), for cheap review.

## 0.5.0 (2026-09-29)
- **Material checks**: the material report starts with a *Checks* section and the panel shows the count. Flags a missing
  shader (pink), a non-EFT shader (e.g. Standard left in), the wrong shader / `_StencilType` for body parts (SMap_Decal, 1)
  and first-person hands (SMap, 2), empty `_MainTex` / `_BumpMap` / `_SpecMap`, Unity default textures, non power-of-two
  or over-4096 textures, empty material slots, skinned meshes with 0 bones. Values like `_Glossness` are not judged
  (they depend on enc=2 / enc=3). Unit-tested with mono against the shapes of the real valeria report.
- **A/B in one click** (Photo tab): a turntable (4 screenshots) of every outfit of the newest mod (e.g. valeria enc=2 and
  enc=3), then of your own outfit as the reference, all with the same camera and lights.
- **solo** button per mesh: hides every other mesh of the character (*Show all* brings them back).
- `SEND_RESULTS_TO_CLAUDE.bat`: screenshots over 4 MB are sent as JPEG copies (max 2560 px wide, quality 90); the PNGs
  stay on the PC. A 5K PNG is ~20 MB, a turntable 80 MB, so the repo would otherwise grow fast.

## 0.4.0 (2026-09-29)
- **Try on** (Outfits / try on tab, now the tab the panel opens on): in the hideout or a raid, *Wear* puts any top, pants
  or head from the catalog on your own character, live and client-side only (nothing saved; a reload shows your real
  outfit). Tops bring their first-person hands. Mod outfits are grouped into sets (top + pants + head + hands by name,
  as the Mod Builder names them), **newest mod first**, one *Wear* click each. *Restore my outfit* puts the original back.
  Recipe from SkinService (github.com/kmyuhkyuk/SkinService): customization ids → `LoadBundlesAndCreatePools` →
  `PlayerBody.Init` (8 parameters, as in the SPT 4.1.6 log) → `UpdatePlayerRenders`. The loader is found by name and
  its signature logged.
- Option *4. Outfits / Auto-wear newest mod outfit in the hideout* (off by default).
- **Save this head to my profile**: keeps a head after restart through the WTT HeadVoiceSelector server mod's route
  (github.com/sgtlaggy/spt-HeadVoiceSelector-server, `/WTT/WTTChangeHead`), if that mod is installed.

## 0.3.1 (2026-09-29)
- Photo mode safeguards taken from CineKit (github.com/Hysocs/cinekit-spt): the camera's culling mask gets the layers of
  the visible body renderers, shadows-only body renderers are drawn, and leaving restores the previous point of view
  (not always first person), the culling mask and the shadows.

## 0.3.0 (2026-09-29)
- First PC run (from_pc/20260929-071416) worked: build clean, every game type found, raid (Factory) + hideout + menu previews
  listed, catalog 568 entries from `SPT_Runtime`, the screenshot `.txt` names mod / id / bundle of each worn part.
- **Photo mode** (Photo tab, raid / hideout): the camera orbits your own character (third person), the character takes no
  input, the HUD is hidden. Angle presets (front, 3/4, left, back, right), framing presets (full body, upper body, head),
  sliders for yaw / pitch / distance / height / FOV, right-mouse drag to orbit and wheel to zoom.
  **Studio lights**: key / fill / rim spot lights that follow the camera (or stay fixed to the character), strength in the config.
  **Turntable**: 4 screenshots (front, left, back, right) with one click. The screenshot `.txt` records the camera and light settings.
  Camera recipe from the open-source SPT Freecam (third-person POV, `GamePlayerOwner` off, `CameraManager.ForceSetPosition` blocked with Harmony).
- **F12 buttons**: *0. Inspector* in ConfigurationManager has *Open Inspector panel* and *Photo mode ON/OFF*, so the hotkeys
  are optional (set them to None).
- Catalog: a mod bundle not at `<mod>\bundles\<path>` is also looked for by file name anywhere in the mod folder
  (c11-tn-4 showed 8 "missing" bundles that are probably just stored elsewhere).
- The scan is logged once per distinct result (the first log repeated it on every switch).

## 0.2.0 (2026-09-29)
- **Outfits tab** (read-only): every top / pants / head / hands the SPT server knows, from vanilla
  (`SPT_Data\database\templates\customization.json`) and from each mod's WTT `db/CustomClothing` and `db/CustomHeads`.
  Filter by text, part and source (vanilla / one mod / mods only); what the character wears is listed first as WORN;
  entries whose bundle file is missing are flagged. *Write catalog to file* saves the full list.
  The server folder is searched next to / inside the game folder; set it in the config (*4. Outfits / Server folder*) if not found.
- Screenshot `.txt` and material report: each worn part now names its catalog entry (mod, name, id, bundle).
- Own JSON reader (no dependency on the game's Newtonsoft). JSON + catalog unit-tested with mono
  (`tests/run_tests.sh`; the mod files in the test are written by the Mod Builder's own `EFTModBuilderCore`).
- `tests/compile_check.sh`: the cloud compile check (mcs against public Unity/BepInEx reference DLLs).
- Switching outfits is still not done (next stage; plan in `docs/SPT_INSPECTOR.md`).

## 0.1.0 (2026-09-29)
- First version (stage 1). Panel (F9): the character's renderers grouped as Head / Top / Pants / Hands (from
  `PlayerBody.BodySkins`, inactive armor/vest/face-cover meshes included) and gear by item; a checkbox per mesh and
  per group, *Show all*, *Hide gear*. Hiding uses `Renderer.forceRenderingOff`, so the game's own LOD / armor-mesh
  switching doesn't undo it.
- Screenshot (F10 or the button): PNG at supersize 1–4× with the panel and (in raid / hideout) the HUD hidden, plus a
  `.txt` with the profile customization ids, the skin objects and the loaded bundles they came from, and the hidden meshes.
- Material report: renderer → material → shader, every shader property (textures with size/format), key values marked.
- Diagnostics for the first run: game/Unity version, which game types were found, the scan per character, and a one-time
  dump of `PlayerBody`, `PlayerModelView` and `TacticalClothingView` members (for the stage-2 outfit browser).
- Checked by compiling with mono `mcs` against Unity 2021.3 reference modules (NuGet `UnityEngine.Modules`) and
  BepInEx 5.4.23.2. Not compiled against the real SPT 4.1.6 DLLs and not run in game yet.
