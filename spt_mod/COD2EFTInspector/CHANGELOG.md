# COD2EFT Inspector changelog

The version is `InspectorPlugin.Version` in `Plugin.cs` (the build script passes it to the DLL). It shows in the
panel title and in the BepInEx log (`COD2EFT Inspector v… loaded`).

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
