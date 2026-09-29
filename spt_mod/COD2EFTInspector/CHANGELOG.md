# COD2EFT Inspector changelog

The version is `InspectorPlugin.Version` in `Plugin.cs` (the build script passes it to the DLL). It shows in the
panel title and in the BepInEx log (`COD2EFT Inspector v… loaded`).

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
