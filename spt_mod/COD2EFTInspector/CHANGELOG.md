# COD2EFT Inspector changelog

The version is `InspectorPlugin.Version` in `Plugin.cs` (the build script passes it to the DLL). It shows in the
panel title and in the BepInEx log (`COD2EFT Inspector v… loaded`).

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
