// COD2EFT Inspector: an in-game panel for checking converted characters.
//  - lists the character's renderers by part (head / top / pants / hands / gear) with show/hide checkboxes
//  - screenshot (PNG, supersize 1-4x, panel and HUD hidden) + a .txt of the outfit and hidden meshes
//  - material report (renderer -> material -> shader, textures, values)
// Output: <SPT game>\COD2EFT_Screenshots\. Log lines go to BepInEx\LogOutput.log ("COD2EFT Inspector").
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace COD2EFTInspector
{
    [BepInPlugin(Guid, PluginName, Version)]   // GUID without '_' (an '_' in any SPT mod GUID stops all mods loading)
    public partial class InspectorPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.cod2eft.inspector";
        public const string PluginName = "COD2EFT Inspector";
        public const string Version = "0.12.0";

        internal static ManualLogSource Log;
        internal static InspectorPlugin Instance;

        ConfigEntry<KeyboardShortcut> _panelKey, _shotKey, _freezeKey, _slowKey;
        ConfigEntry<int> _supersize;
        ConfigEntry<bool> _hideHud, _others, _unlockCursor;
        ConfigEntry<float> _scale;
        ConfigEntry<string> _serverDir;
        ConfigEntry<bool> _autoWear;
        Component _autoFor;
        float _autoAt;

        bool _open, _capturing;
        Rect _win = new Rect(40, 40, 600, 720);
        List<Target> _targets = new List<Target>();
        int _ti;
        Scan _scan;
        string _lastSignature = "", _status = "";
        float _nextTick;
        string _outDir;
        CursorLockMode _prevLock;
        bool _prevVisible;
        // renderers hidden by us -> their forceRenderingOff before we touched them
        // (forceRenderingOff, not enabled: the game switches LODs / armor meshes with enabled and active, and would undo it)
        readonly Dictionary<Renderer, bool> _hidden = new Dictionary<Renderer, bool>();
        readonly PhotoMode _photo = new PhotoMode();
        ConfigEntry<float> _lightStrength;
        ConfigEntry<bool> _escSteps, _blockInput, _invertAim, _playOnDoubleClick, _topsBringHands, _playCamTurns;
        ConfigEntry<float> _winW, _winH, _winX, _winY;
        bool _resizing;
        ConfigEntry<string> _bgColor;
        bool _transparent;
        readonly List<Canvas> _photoHud = new List<Canvas>();
        readonly HashSet<string> _loggedScans = new HashSet<string>();

        void Awake()
        {
            Log = Logger;
            Instance = this;
            // F12 (ConfigurationManager) buttons, so the panel and photo mode don't need hotkeys
            Config.Bind("0. Inspector", "Buttons", "", new ConfigDescription("Open the panel / photo mode from here.", null,
                new ConfigurationManagerAttributes { CustomDrawer = DrawF12Buttons, HideDefaultButton = true, Order = 100 }));
            _panelKey = Config.Bind("1. Hotkeys", "Open panel", new KeyboardShortcut(KeyCode.F9),
                "Shows / hides the inspector panel. Set to None to use only the F12 button.");
            _shotKey = Config.Bind("1. Hotkeys", "Screenshot", new KeyboardShortcut(KeyCode.F10),
                "Saves a screenshot (panel and HUD hidden). Set to None to use only the panel button.");
            _freezeKey = Config.Bind("1. Hotkeys", "Freeze (photo mode)", new KeyboardShortcut(KeyCode.F7), "Stops / restarts the game while photo mode is on (also in play mode).");
            _slowKey = Config.Bind("1. Hotkeys", "Slow motion (photo mode)", new KeyboardShortcut(KeyCode.F8), "Cycles 1x / 0.5x / 0.25x / 0.1x while photo mode is on.");
            _supersize = Config.Bind("2. Screenshot", "Supersize", 2,
                new ConfigDescription("Resolution multiplier (1 = screen size).", new AcceptableValueRange<int>(1, 4)));
            _hideHud = Config.Bind("2. Screenshot", "Hide HUD", true,
                "Hide the game's UI canvases during the capture (raid / hideout only; in the menu the character preview is itself UI).");
            _others = Config.Bind("3. Panel", "Include other players", false, "Also list bots / other players in raid.");
            _unlockCursor = Config.Bind("3. Panel", "Free the mouse", true, "Unlock the mouse cursor while the panel is open (raid / hideout).");
            _scale = Config.Bind("3. Panel", "Scale", 1f, new ConfigDescription("Panel size.", new AcceptableValueRange<float>(0.75f, 2.5f)));
            _serverDir = Config.Bind("4. Outfits", "Server folder", "",
                "SPT server folder (the one with user\\mods and SPT_Data). Empty = search next to / inside the game folder.");
            _autoWear = Config.Bind("4. Outfits", "Auto-wear newest mod outfit in the hideout", false,
                "When the hideout loads, put the newest mod's outfit on your character (try-on, not saved).");
            _lightStrength = Config.Bind("5. Photo mode", "Studio light strength", 1f,
                new ConfigDescription("Multiplier for the key / fill / rim lights.", new AcceptableValueRange<float>(0f, 4f)));
            _blockInput = Config.Bind("3. Panel", "Block game input while open", true,
                "While the panel is open (raid / hideout) the character takes no input: no look, aim, fire or walk. Given back on close.");
            _playOnDoubleClick = Config.Bind("5. Photo mode", "Double-click to play", true,
                "In photo mode, a double-click outside the panel gives you full control of the character (walk, shoot, reload, inspect) " +
                "while the photo camera keeps orbiting. Esc goes back.");
            _playCamTurns = Config.Bind("5. Photo mode", "Play mode: camera turns with the character", true,
                "On: the camera stays behind / around the character as it turns. Off: the camera keeps its angle in the world (the character turns in front of it).");
            _topsBringHands = Config.Bind("4. Outfits", "Tops bring their hands", true,
                "Wearing a top also puts on its first-person hands (the suite's pairing). Off: hands only change when you wear hands.");
            _escSteps = Config.Bind("3. Panel", "Esc steps back", true,
                "Esc leaves one level at a time: play mode -> photo mode with the panel -> panel closed -> photo mode off.");
            _invertAim = Config.Bind("5. Photo mode", "Invert aim drag", false, "Left-drag up/down turns the aim the other way.");
            _bgColor = Config.Bind("5. Photo mode", "Background colour", "#00B140", "Solid background colour for Isolate character (HTML colour; default chroma green).");
            _winW = Config.Bind("3. Panel", "Width", 470f, "Panel width (drag the bottom-right corner of the panel to change it).");
            _winH = Config.Bind("3. Panel", "Height", 660f, "Panel height (drag the bottom-right corner of the panel to change it).");
            _winX = Config.Bind("3. Panel", "Position X", -1f, "Panel position (drag the title bar). -1 = docked to the right edge.");
            _winY = Config.Bind("3. Panel", "Position Y", 60f, "Panel position from the top (drag the title bar).");
            _win.width = Mathf.Max(MinW, _winW.Value); _win.height = Mathf.Max(MinH, _winH.Value);
            _win.x = _winX.Value; _win.y = _winY.Value;
            _outDir = Path.Combine(Paths.GameRootPath, "COD2EFT_Screenshots");
            // 0.12.0: the panel's sliders (size, light strength, colour) set config values every frame while dragged; BepInEx
            // wrote the .cfg file on each change. Now it is written at most once a second (Update) and on quit.
            Config.SaveOnConfigSet = false;
            Config.SettingChanged += (o, e) => _configDirty = true;
            Log.LogInfo($"{PluginName} v{Version} loaded. Panel {_panelKey.Value}, screenshot {_shotKey.Value}, output {_outDir}");
            try { Game.LogStartup(); } catch (Exception e) { Log.LogError("Startup check failed: " + e); }
            PhotoMode.InstallPatch(Guid);
            MenuTryOn.InstallPatch(Guid);
            Camera.onPreCull += OnPreCullCamera;
        }

        // the camera is placed again right before it renders, after every LateUpdate, so the game can't move it back
        void OnPreCullCamera(Camera c) { if (_photo.Active) try { _photo.LateUpdate(); } catch { } }

        void LateUpdate()
        {
            try { _photo.LightStrength = _lightStrength.Value; _photo.LateUpdate(); }
            catch (Exception e) { Game.LogOnce("photo:" + e.Message, "Photo mode update failed: " + e); }
        }

        static void DrawF12Buttons(ConfigEntryBase entry)
        {
            var p = Instance;
            if (p == null) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(p._open ? "Close Inspector panel" : "Open Inspector panel")) p.TogglePanel();
            if (GUILayout.Button(p._photo.Active ? "Photo mode OFF" : "Photo mode ON")) p.TogglePhoto();
            GUILayout.EndHorizontal();
        }

        void TogglePhoto()
        {
            if (_photo.Active)
            {
                _photo.Exit();
                foreach (var c in _photoHud) if (c != null) c.enabled = true;
                _photoHud.Clear();
                if (!_open && _unlockCursor.Value) { Cursor.lockState = _prevLock; Cursor.visible = _prevVisible; }
                return;
            }
            _photo.LightStrength = _lightStrength.Value;
            var err = _photo.Enter(Game.MainPlayer());
            if (err != null) { _status = err; Log.LogWarning("Photo mode: " + err); return; }
            if (_hideHud.Value)
                foreach (var c in FindObjectsOfType<Canvas>())
                    if (c != null && c.enabled && c.isRootCanvas) { c.enabled = false; _photoHud.Add(c); }
            if (!_open) TogglePanel();
            _page = PagePhoto;
        }

        bool MouseOverPanel()
        {
            if (!_open) return false;
            float s = Mathf.Clamp(_scale.Value, 0.75f, 2.5f);
            var m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
            return _win.Contains(m);
        }

        void Update()
        {
            try
            {
                TimeTick();
                _photo.PlayCameraTurns = _playCamTurns.Value;   // F12 changes apply at once, also during play mode
                // play mode (photo mode with full control): Esc or the panel key goes back
                bool esc = Input.GetKeyDown(KeyCode.Escape);
                if (_photo.Play && (!_photo.Active || esc || _panelKey.Value.IsDown())) SetPlay(false);
                else if (_panelKey.Value.IsDown()) TogglePanel();
                // 0.11.0: Esc steps back one level: play mode -> photo mode + panel -> photo mode -> off
                else if (esc && _escSteps.Value) { if (_open) TogglePanel(); else if (_photo.Active) TogglePhoto(); }
                if (_shotKey.Value.IsDown()) TakeScreenshot();
                if (Time.unscaledTime >= _nextTick)
                {
                    _nextTick = Time.unscaledTime + 1f;
                    if (_configDirty) { _configDirty = false; try { Config.Save(); } catch (Exception e) { Game.LogOnce("cfgsave", "Saving the config failed: " + e.Message); } }
                    Reassert();
                    AutoWearTick();
                    var ow = Wearer.CheckOverwritten();
                    if (ow != null) { Log.LogWarning("Wear: " + ow); _status = "Note: " + ow; try { BodyScan.LogBodies(null); } catch { } }
                    if (_open) Tick();
                }
                if (_photo.Play) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
                else if ((_open || _photo.Active) && _unlockCursor.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                // panel open or photo mode: the character takes no input (look / aim / fire / walk); given back as it was
                InputBlock.Set(Game.MainPlayer(), !_photo.Play && (_photo.Active || (_open && _blockInput.Value)));
                if (_photo.Active && !_photo.Play)
                {
                    _photo.InvertAimDrag = _invertAim.Value;
                    _photo.HandleMouse(MouseOverPanel());
                    _photo.Update();
                    if (_photo.DoubleClicked) { _photo.DoubleClicked = false; if (_playOnDoubleClick.Value) SetPlay(true); }
                }
                // photo mode can end by itself (player gone: hideout / raid left); its hidden UI must come back, some of it is the menu's
                if (!_photo.Active && _photoHud.Count > 0)
                {
                    foreach (var c in _photoHud) if (c != null) c.enabled = true;
                    _photoHud.Clear();
                    Log.LogInfo("Photo mode ended; UI canvases restored");
                }
            }
            catch (Exception e) { Game.LogOnce("update:" + e.GetType().Name + e.Message, "Update failed: " + e); }
        }


        /// <summary>Play mode on / off: full control of the character inside photo mode (the panel hides; Esc comes back).</summary>
        void SetPlay(bool on)
        {
            if (on == _photo.Play || (on && !_photo.Active)) return;
            _photo.Play = on;
            if (on)
            {
                _open = false;
                _photo.PlayCameraTurns = _playCamTurns.Value;
                InputBlock.Release();
                Log.LogInfo("Photo mode: play mode on (full control; Esc to leave)");
            }
            else
            {
                _photo.Rebase();   // the character keeps where it now faces / aims
                if (!_open) TogglePanel();   // always back to the panel (0.9.0 kept it closed if it was closed before)
                _status = "Back from play mode";
                Log.LogInfo("Photo mode: play mode off");
            }
        }

        void TogglePanel()
        {
            _open = !_open;
            if (_open)
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                try { Game.LogResearchOnce(); } catch (Exception e) { Log.LogError("Research dump failed: " + e); }
                Refresh(true);
            }
            else if (_unlockCursor.Value && !_photo.Active) { Cursor.lockState = _prevLock; Cursor.visible = _prevVisible; }
        }

        // ------------------------------------------------------------------ scanning / hiding

        /// <summary>Full search: every character on screen, then the shown one's meshes. Costs a FindObjectsOfType and a
        /// GetComponentsInChildren, so the panel only calls it when opened, after a Wear, on the refresh button, and from
        /// Tick when the shown character is gone (0.11 did it every second while the panel was open: a hitch per second).</summary>
        void Refresh(bool force)
        {
            var old = _ti < _targets.Count ? _targets[_ti].Body ?? (Component)_targets[_ti].Player : null;
            _targets = BodyScan.FindTargets(_others.Value);
            _ti = Math.Max(0, _targets.FindIndex(t => (t.Body ?? (Component)t.Player) == old));
            _scan = _targets.Count > 0 ? Run(_targets[_ti]) : null;
            AfterScan(force);
        }

        /// <summary>Only the shown character's meshes again (outfit changed under it).</summary>
        void RescanTarget()
        {
            if (_scan == null) { Refresh(false); return; }
            _scan = Run(_scan.Target);
            AfterScan(false);
        }

        void SelectTarget(int i)
        {
            if (_targets.Count == 0) return;
            _ti = (i % _targets.Count + _targets.Count) % _targets.Count;
            _scan = Run(_targets[_ti]);
            AfterScan(false);
        }

        void AfterScan(bool force)
        {
            string sig = _scan?.Signature ?? "none";
            if (sig != _lastSignature || force)
            {
                if (sig != _lastSignature && _loggedScans.Add(sig)) LogScan();
                _lastSignature = sig;
            }
            _skinsSig = _scan?.Target?.Body != null ? BodyScan.Skins(_scan.Target.Body) : "";
            InvalidateView();
        }

        int _ticks;
        string _skinsSig = "";

        /// <summary>Once a second while the panel is open: cheap checks, a rescan only when something changed.</summary>
        void Tick()
        {
            _ticks++;
            var t = _scan?.Target;
            bool gone = t == null || !Game.Alive(t.Root) || (t.Body != null && !Game.Alive(t.Body));
            if (gone) { if (_scan != null || _ticks % 3 == 0) Refresh(false); return; }   // nothing found yet: look again every 3 s
            string skins = t.Body != null ? BodyScan.Skins(t.Body) : "";
            if (skins != _skinsSig) RescanTarget();                                         // the game or a Wear changed the outfit
            else if (_page == PageInspect && _ticks % 5 == 0) RescanTarget();               // gear picked up / dropped
            else if (_ticks % 10 == 0) Refresh(false);                                      // new characters / menu previews
        }

        // body parts start unfolded, gear folded; groups the user flipped stay flipped across rescans
        readonly HashSet<string> _flipped = new HashSet<string>();

        Scan Run(Target t)
        {
            var scan = BodyScan.Run(t);
            foreach (var g in scan.Groups) g.Open = (g.Kind == "body") != _flipped.Contains(g.Name);
            return scan;
        }

        void LogScan()
        {
            if (_scan == null)
            {
                Log.LogInfo($"Scan: no character found (scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}, " +
                            $"GameWorld {(Game.GameWorld() != null ? "present" : "absent")}).");
                return;
            }
            Log.LogInfo($"Scan: {_targets.Count} character(s): {string.Join("; ", _targets.Select(t => t.Label))}. Showing '{_scan.Target.Label}':");
            foreach (var g in _scan.Groups)
                Log.LogInfo($"  {g.Name} [{g.Kind}] {g.Entries.Count} renderer(s): " +
                            string.Join(", ", g.Entries.Take(12).Select(e => e.Path + (e.R != null && !e.R.gameObject.activeInHierarchy ? " (inactive)" : ""))) +
                            (g.Entries.Count > 12 ? ", ..." : ""));
        }

        bool IsHidden(Renderer r) => _hidden.ContainsKey(r);

        void SetHidden(Renderer r, bool hide)
        {
            if (r == null) return;
            if (hide)
            {
                if (!_hidden.ContainsKey(r)) _hidden[r] = r.forceRenderingOff;
                r.forceRenderingOff = true;
            }
            else if (_hidden.TryGetValue(r, out var was))
            {
                r.forceRenderingOff = was;
                _hidden.Remove(r);
            }
        }

        void Reassert()
        {
            foreach (var r in _hidden.Keys.ToList())
            {
                if (r == null) _hidden.Remove(r);
                else if (!r.forceRenderingOff) r.forceRenderingOff = true;
            }
        }

        void ShowAll() { foreach (var r in _hidden.Keys.ToList()) SetHidden(r, false); _hidden.Clear(); }

        /// <summary>Hides every mesh of the character except this one (Show all brings them back).</summary>
        void Solo(Renderer keep)
        {
            if (_scan == null) return;
            foreach (var g in _scan.Groups) foreach (var e in g.Entries) SetHidden(e.R, e.R != keep);
        }

        void HideGear()
        {
            if (_scan == null) return;
            foreach (var g in _scan.Groups.Where(g => g.Kind != "body"))
                foreach (var e in g.Entries) SetHidden(e.R, true);
        }


        // ------------------------------------------------------------------ outfit catalog (stage 2 groundwork)

        internal static Catalog Cat;

        internal Catalog GetCatalog(bool reload = false)
        {
            if (Cat != null && !reload) return Cat;
            try
            {
                Cat = Catalog.Load(_serverDir.Value, Paths.GameRootPath);
                Log.LogInfo($"Outfit catalog: {Cat.Items.Count} entries. " + string.Join(" | ", Cat.Notes));
            }
            catch (Exception e) { Log.LogError("Outfit catalog failed: " + e); Cat = new Catalog(); Cat.Notes.Add("failed: " + e.Message); }
            InvalidateView();
            return Cat;
        }

        /// <summary>Ids and bundle-name stems of what the shown character wears.</summary>
        HashSet<string> Worn()
        {
            var w = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_scan?.Target?.Player != null)
                foreach (var line in Wearer.WithTryOn(Game.Customization(_scan.Target.Player))) { int i = line.IndexOf(" = ", StringComparison.Ordinal); if (i > 0) w.Add(line.Substring(i + 3).Split(' ')[0]); }
            if (_scan != null) foreach (var g in _scan.Body) w.Add(g.Source);
            return w;
        }

        bool IsWorn(Outfit o) => _worn.Contains(o.Id ?? "") ||
            (o.Bundle != null && _worn.Contains(Path.GetFileNameWithoutExtension(o.Bundle.Replace('\\', '/').Split('/').Last())));

        List<Outfit> WithHands(List<Outfit> items)
        {
            var cat = GetCatalog();
            var all = new List<Outfit>(items);
            if (!_topsBringHands.Value) return all;
            foreach (var top in items.Where(o => o.Part == "Top").ToList())
                if (!all.Any(o => o.Part == "Hands")) { var h = cat.HandsFor(top); if (h != null) all.Add(h); }
            return all;
        }

        /// <summary>A/B in one click: a turntable of every outfit of the newest mod, then of your own outfit as reference.</summary>
        IEnumerator CompareBatch()
        {
            var cat = GetCatalog();
            var newest = cat.ModSets().FirstOrDefault();
            if (newest == null) { _status = "A/B: no mod outfit in the catalog"; yield break; }
            if (!_photo.Active) TogglePhoto();
            if (!_photo.Active) yield break;
            var sets = cat.ModSets().Where(x => x.Source == newest.Source).ToList();
            var runs = sets.Select(x => new KeyValuePair<string, List<Outfit>>(x.Name, WithHands(x.Pieces))).ToList();
            var player = Game.MainPlayer();
            // your own outfit last, as the reference (known once something was worn)
            string versus = string.Join("  vs  ", runs.Select(r => r.Key)) + "  vs  your own outfit";
            Log.LogInfo($"A/B: {runs.Count} outfit(s) of [{newest.Source}] + your own outfit: {versus}");
            int done = 0;
            var sheetRows = new List<KeyValuePair<string, List<string>>>();
            for (int i = 0; i <= runs.Count; i++)
            {
                List<Outfit> items;
                string label;
                if (i < runs.Count) { items = runs[i].Value; label = runs[i].Key; }
                else { items = Wearer.OriginalOutfit(cat); label = "your own outfit (reference)"; }
                if (items.Count == 0) continue;
                _status = $"A/B {i + 1}/{runs.Count + 1}: {label}   ({versus})";
                string msg = null;
                yield return StartCoroutine(Wearer.Wear(player, items, m => msg = m));
                if (msg == null || msg.StartsWith("Try-on failed")) { Log.LogWarning($"A/B: skipped '{label}': {msg}"); continue; }
                yield return new WaitForSecondsRealtime(1.5f);   // textures stream in
                Refresh(true);
                int before = _captured.Count;
                yield return StartCoroutine(Turntable());
                string pieces = string.Join(" + ", items.Where(o => o.Part != "Hands").Select(o => o.Part + ": " + o.Name));
                sheetRows.Add(new KeyValuePair<string, List<string>>(i < runs.Count ? $"{label}  ({pieces})" : label, _captured.Skip(before).ToList()));
                done++;
            }
            _status = $"A/B done: {done} turntable(s) of 4 screenshots in {_outDir}";
            _status += " | making the comparison sheet ...";
            yield return null;
            try
            {
                var sheet = Sheet.Make(Path.Combine(_outDir, $"{Stamp()}_AB_sheet_{newest.Source}.png"), $"A/B  {versus}  {DateTime.Now:yyyy-MM-dd HH:mm}", sheetRows);
                _status = $"A/B done: {done} turntable(s); sheet: {(sheet != null ? Path.GetFileName(sheet) : "none")}";
            }
            catch (Exception e) { _status = "A/B done; the comparison sheet failed: " + e.Message; Log.LogError("Sheet failed: " + e); }
            Log.LogInfo(_status);
        }

        /// <summary>Try-on is possible: your character (raid / hideout) or a menu preview that the game has shown.</summary>
        bool CanWear() => !Wearer.Busy && !MenuTryOn.Busy && (Game.MainPlayer() != null || MenuTryOn.Available);

        void WearItems(List<Outfit> items)
        {
            if (!CanWear() || items == null || items.Count == 0) return;
            var all = WithHands(items);
            // 0.11.0: wearing one part keeps the other parts tried on before (an upper, then a lower = both on)
            var cat = GetCatalog();
            bool menu = Game.MainPlayer() == null;
            foreach (var part in new[] { "Top", "Pants", "Head", "Hands" })
            {
                if (all.Any(o => o.Part == part)) continue;
                var prev = cat.ById(menu ? MenuTryOn.TryOnId(part) : Wearer.TryOnId(Wearer.BodyKey(part)));
                if (prev != null) all.Add(prev);
            }
            _tuner.ClearView();   // the body is rebuilt: the view's material swap would point at old renderers
            _status = "Loading " + string.Join(", ", all.Select(o => o.Name)) + " ...";
            Action<string> done = msg => { _status = msg; try { Refresh(true); } catch { } };
            if (Game.MainPlayer() != null) StartCoroutine(WearPlayerAndPreview(all, done));
            else StartCoroutine(MenuTryOn.Wear(_scan?.Target?.Player == null ? _scan?.Target?.Body : null, all, done));
        }

        /// <summary>Hideout / raid: your character, then the inventory screen's preview too if it is open, so both show the same.</summary>
        IEnumerator WearPlayerAndPreview(List<Outfit> all, Action<string> done)
        {
            string m1 = null, m2 = null;
            yield return StartCoroutine(Wearer.Wear(Game.MainPlayer(), all, m => m1 = m));
            if (m1 != null && !m1.StartsWith("Try-on failed") && MenuTryOn.Available)
            {
                yield return StartCoroutine(MenuTryOn.Wear(null, all, m => m2 = m));
                m1 += " | " + m2;
            }
            done(m1 ?? "?");
        }

        /// <summary>Back to how it was before photo mode / try-on: meshes, pose, character, camera, lights, background, outfit.</summary>
        void ResetAll()
        {
            SetPlay(false);
            ShowAll();
            if (_photo.Active)
            {
                if (_pose != "Stand") SetPose("Stand");
                TogglePhoto();   // exit restores camera, culling, background, lights, character rotation, view
            }
            _pose = "Stand";
            _photo.ResetCamera(); _photo.ResetCharacter(); _photo.ResetLights(); _photo.ResetBackground();
            _lightStrength.Value = 1f;
            _transparent = false;
            ResetTime();
            _tuner.ClearView();
            string outfit = "";
            if (Game.MainPlayer() != null && Wearer.HasTryOn && CanWear()) { WearItems(Wearer.OriginalOutfit(GetCatalog())); outfit = ", outfit being restored"; }
            else if (Game.MainPlayer() == null && MenuTryOn.Available && !MenuTryOn.Busy) { StartCoroutine(MenuTryOn.Reshow(m => _status = m)); outfit = ", menu preview re-shown"; }
            _status = "Reset all: photo mode off, meshes shown, settings back to default" + outfit;
            Log.LogInfo(_status);
        }

        void AutoWearTick()
        {
            if (!_autoWear.Value || Wearer.Busy) return;
            var p = Game.MainPlayer();
            if (p == null || Game.Location(p) != "hideout") { _autoFor = null; return; }
            if (p != _autoFor) { _autoFor = p; _autoAt = Time.unscaledTime + 5f; return; }   // let the hideout finish loading
            if (_autoAt <= 0f || Time.unscaledTime < _autoAt) return;
            _autoAt = 0f;
            var set = GetCatalog().ModSets().FirstOrDefault();
            if (set == null) { Log.LogInfo("Auto-wear: no mod outfit in the catalog"); return; }
            Log.LogInfo($"Auto-wear: [{set.Source}] {set.Name}");
            WearItems(set.Pieces);
        }


        void WriteCatalog(Catalog cat)
        {
            try
            {
                Directory.CreateDirectory(_outDir);
                var f = Path.Combine(_outDir, $"{Stamp()}_outfit_catalog.txt");
                File.WriteAllText(f, $"COD2EFT Inspector v{Version} - outfit catalog\r\n\r\n" + cat.Report());
                _status = "Catalog: " + Path.GetFileName(f);
                Log.LogInfo("Outfit catalog written: " + f);
            }
            catch (Exception e) { _status = "Catalog failed: " + e.Message; Log.LogError("Catalog write failed: " + e); }
        }

        // ------------------------------------------------------------------ output

        string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

        void MaterialReport()
        {
            try
            {
                Refresh(false);
                Directory.CreateDirectory(_outDir);
                var f = Path.Combine(_outDir, $"{Stamp()}_{BodyScan.OutfitNames(_scan)}_materials.txt");
                GetCatalog();
                File.WriteAllText(f, Reports.Materials(_scan));
                var problems = Reports.Check(_scan);
                _status = $"Material report: {Path.GetFileName(f)} - " +
                          (problems.Count == 0 ? "checks all fine" : $"{problems.Count(x => x.StartsWith("ERROR"))} error(s), {problems.Count(x => x.StartsWith("WARN"))} warning(s): " + problems[0]);
                foreach (var pr in problems) Log.LogWarning("Check: " + pr);
                Log.LogInfo("Material report written: " + f);
            }
            catch (Exception e) { _status = "Material report failed: " + e.Message; Log.LogError("Material report failed: " + e); }
        }


        bool _colourLoaded;

        IEnumerator Turntable() { return Turntable(""); }

        IEnumerator Turntable(string tag)
        {
            float yaw = _photo.Yaw;
            foreach (var a in new[] { ("front", 0f), ("left", 90f), ("back", 180f), ("right", 270f) })
            {
                _photo.Yaw = _photo.YawFor(a.Item2);
                for (int i = 0; i < 3; i++) yield return null;
                yield return StartCoroutine(Capture(tag + "_" + a.Item1));
            }
            _photo.Yaw = yaw;
        }

        string _pose = "Stand";

        void SetPose(string pose)
        {
            var err = Poses.Apply(Game.MainPlayer(), pose);
            _pose = pose;
            // prone: aim the camera lower so the body stays in frame
            if (pose == "Prone") { _photo.Height = 0.35f; _photo.Pitch = Mathf.Max(_photo.Pitch, 20f); }
            else if (_photo.Height < 0.5f) _photo.Height = 1.0f;
            _status = err == null ? "Pose: " + pose : "Pose " + pose + ": " + err;
        }

        /// <summary>Clipping check: a turntable in each pose (the game's own animations).</summary>
        IEnumerator PoseTurntables()
        {
            foreach (var pose in new[] { "Stand", "Crouch", "Low crouch", "Prone", "Aim" })
            {
                SetPose(pose);
                yield return new WaitForSecondsRealtime(pose == "Prone" ? 2.5f : 1.2f);   // let the transition animation finish
                yield return StartCoroutine(Turntable("_" + pose.ToLowerInvariant().Replace(' ', '-')));
            }
            SetPose("Stand");
            _status = "Pose turntables done (5 poses x 4 angles)";
        }

        void TakeScreenshot()
        {
            if (!_capturing) StartCoroutine(Capture(""));
        }

        IEnumerator Capture(string suffix)
        {
            _capturing = true;
            var canvases = new List<Canvas>();
            string png = null, err = null;
            int ss = Mathf.Clamp(_supersize.Value, 1, 4), w = 0, h = 0;
            bool alpha = _transparent && _photo.Isolated;
            if (alpha) ss = Mathf.Min(ss, 2);   // two captures + a matte in memory
            try
            {
                Refresh(false);
                Directory.CreateDirectory(_outDir);
                png = Path.Combine(_outDir, $"{Stamp()}_{BodyScan.OutfitNames(_scan)}{suffix}{(alpha ? "_alpha" : "")}.png");
                if (_hideHud.Value && Game.GameWorld() != null)
                    foreach (var c in FindObjectsOfType<Canvas>())
                        if (c != null && c.enabled && c.isRootCanvas) { c.enabled = false; canvases.Add(c); }
            }
            catch (Exception e) { err = "prepare: " + e.Message; Log.LogError("Screenshot prepare failed: " + e); }

            yield return null;                      // one frame without the panel and HUD
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            if (alpha && err == null)
            {
                // Transparent PNG without relying on the post-processing stack keeping alpha: the same frame on a black and
                // on a white background (time stopped in between), alpha from the difference (difference matting).
                Texture2D black = null, white = null;
                float oldScale = Time.timeScale;
                Time.timeScale = 0f;
                try
                {
                    _photo.BgOverride = Color.black;
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    try { black = ScreenCapture.CaptureScreenshotAsTexture(ss); } catch (Exception e) { err = "capture (black): " + e.Message; }
                    _photo.BgOverride = new Color(0.5f, 0.5f, 0.5f);   // grey, not white: a white background blooms onto the character
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    try { if (err == null) { white = ScreenCapture.CaptureScreenshotAsTexture(ss); tex = Matte.Make(black, white); } }
                    catch (Exception e) { err = "matte: " + e.Message; Log.LogError("Transparent capture failed: " + e); }
                }
                finally
                {
                    _photo.BgOverride = null;
                    Time.timeScale = oldScale;
                    if (black != null) Destroy(black);
                    if (white != null) Destroy(white);
                }
            }
            try
            {
                if (err == null)
                {
                    if (tex == null) tex = ScreenCapture.CaptureScreenshotAsTexture(ss);
                    w = tex.width; h = tex.height;
                    File.WriteAllBytes(png, tex.EncodeToPNG());
                }
            }
            catch (Exception e) { err = "capture: " + e.Message; Log.LogError("Screenshot capture failed: " + e); }
            finally
            {
                if (tex != null) Destroy(tex);
                foreach (var c in canvases) if (c != null) c.enabled = true;
                _capturing = false;
            }

            if (err == null)
            {
                try
                {
                    GetCatalog();
                    File.WriteAllText(Path.ChangeExtension(png, ".txt"), Reports.ScreenshotInfo(_scan, png, ss, w, h, _hidden.Keys,
                        _photo.Active ? $"Photo mode: yaw {_photo.Yaw:0.#} pitch {_photo.Pitch:0.#} distance {_photo.Distance:0.##} height {_photo.Height:0.##} " +
                                        $"fov {_photo.Fov:0.#}{(_photo.Ortho ? " orthographic" : "")}, studio lights {(_photo.Lights ? "on x" + _photo.LightStrength.ToString("0.##") : "off")}" +
                                        (_photo.LightsFollowCamera ? " (follow camera)" : " (fixed to character)")
                                      : "Photo mode: off"));
                    _status = $"Saved {Path.GetFileName(png)} ({w}x{h})";
                    _captured.Add(png);
                    Log.LogInfo($"Screenshot saved: {png} ({w}x{h}, supersize {ss}, {canvases.Count} HUD canvases hidden, {_hidden.Count} meshes hidden)");
                }
                catch (Exception e) { _status = "Screenshot info failed: " + e.Message; Log.LogError("Screenshot .txt failed: " + e); }
            }
            else _status = "Screenshot failed: " + err;
        }

        readonly List<string> _captured = new List<string>();   // every screenshot saved this session (turntables -> sheet)

        // ------------------------------------------------------------------ Materials tab (0.10.0)

        readonly MaterialTuner _tuner = new MaterialTuner();
        Material _selMat;
        bool _matGear, _matSameShader;
        readonly Dictionary<string, Vector2> _ranges = new Dictionary<string, Vector2>();

        List<Renderer> TunerRenderers() =>
            _scan == null ? new List<Renderer>() :
            _scan.Groups.Where(g => _matGear || g.Kind == "body").SelectMany(g => g.Entries).Select(e => e.R).Where(r => r != null).Distinct().ToList();

        /// <summary>renderer -> its real materials (the channel view swaps them for unlit copies).</summary>
        IEnumerable<Material> RealMaterials(Renderer r) => _tuner.Saved(r) ?? r.sharedMaterials;

        string UsedBy(Material m) =>
            string.Join(", ", TunerRenderers().Where(r => RealMaterials(r).Contains(m)).Select(r => r.name).Distinct().Take(4));


        void SaveTuning()
        {
            try
            {
                Directory.CreateDirectory(_outDir);
                var f = Path.Combine(_outDir, $"{Stamp()}_{BodyScan.OutfitNames(_scan)}_material_tuning.txt");
                File.WriteAllText(f, _tuner.Report(BodyScan.OutfitNames(_scan), UsedBy));
                _status = "Material tuning saved: " + Path.GetFileName(f);
                Log.LogInfo("Material tuning written: " + f);
            }
            catch (Exception e) { _status = "Saving the tuning failed: " + e.Message; Log.LogError("Tuning save failed: " + e); }
        }

        // ------------------------------------------------------------------ photo presets + time (0.10.0)

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        string PresetFile => Path.Combine(Paths.ConfigPath, "COD2EFTInspector_photo_presets.txt");
        List<KeyValuePair<string, string>> _presets;
        string _presetName = "";

        List<KeyValuePair<string, string>> Presets()
        {
            if (_presets != null) return _presets;
            _presets = new List<KeyValuePair<string, string>>();
            try
            {
                if (File.Exists(PresetFile))
                    foreach (var line in File.ReadAllLines(PresetFile))
                    {
                        int i = line.IndexOf('|');
                        if (i > 0) _presets.Add(new KeyValuePair<string, string>(line.Substring(0, i), line.Substring(i + 1)));
                    }
            }
            catch (Exception e) { Log.LogWarning("Photo presets not readable: " + e.Message); }
            return _presets;
        }

        void SavePresets()
        {
            try { File.WriteAllLines(PresetFile, _presets.Select(kv => kv.Key + "|" + kv.Value).ToArray()); }
            catch (Exception e) { _status = "Saving presets failed: " + e.Message; }
        }

        string PresetString()
        {
            var p = _photo;
            Func<float, string> f = x => x.ToString("0.####", Inv);
            return string.Join(";", new[]
            {
                "Yaw=" + f(p.Yaw - p.CharYaw), "Pitch=" + f(p.Pitch), "Distance=" + f(p.Distance), "Height=" + f(p.Height), "Fov=" + f(p.Fov),
                "Ortho=" + p.Ortho, "CharYaw=" + f(p.CharYaw), "CharPitch=" + f(p.CharPitch), "Pose=" + _pose,
                "Lights=" + p.Lights, "Follow=" + p.LightsFollowCamera, "Strength=" + f(_lightStrength.Value),
                "Isolate=" + p.Isolate, "Bg=" + ColorUtility.ToHtmlStringRGB(p.BgColor), "NoFog=" + p.NoFog, "NoPost=" + p.NoPost,
                "WorldOff=" + p.WorldLightsOff, "Transparent=" + _transparent, "Speed=" + f(_speed),
            });
        }

        void ApplyPreset(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in s.Split(';')) { int i = kv.IndexOf('='); if (i > 0) d[kv.Substring(0, i)] = kv.Substring(i + 1); }
            Func<string, float, float> F = (k, def) => { string v; float x; return d.TryGetValue(k, out v) && float.TryParse(v, NumberStyles.Float, Inv, out x) ? x : def; };
            Func<string, bool, bool> B = (k, def) => { string v; bool x; return d.TryGetValue(k, out v) && bool.TryParse(v, out x) ? x : def; };
            var p = _photo;
            p.CharYaw = F("CharYaw", p.CharYaw); p.CharPitch = F("CharPitch", p.CharPitch);
            p.Yaw = Mathf.Repeat(F("Yaw", 0f) + p.CharYaw, 360f); p.Pitch = F("Pitch", p.Pitch); p.Distance = F("Distance", p.Distance);
            p.Height = F("Height", p.Height); p.Fov = F("Fov", p.Fov); p.Ortho = B("Ortho", p.Ortho);
            p.SetLights(B("Lights", p.Lights)); p.LightsFollowCamera = B("Follow", p.LightsFollowCamera); _lightStrength.Value = F("Strength", 1f);
            p.Isolate = B("Isolate", p.Isolate); p.NoFog = B("NoFog", p.NoFog); p.NoPost = B("NoPost", p.NoPost); p.WorldLightsOff = B("WorldOff", p.WorldLightsOff);
            _transparent = B("Transparent", false); _speed = Mathf.Clamp(F("Speed", 1f), 0.05f, 1f);
            string bg; Color c;
            if (d.TryGetValue("Bg", out bg) && ColorUtility.TryParseHtmlString("#" + bg, out c)) { p.BgColor = c; _bgColor.Value = "#" + bg; _colourLoaded = true; }
            string pose;
            if (d.TryGetValue("Pose", out pose) && Poses.Names.Contains(pose) && pose != _pose) SetPose(pose);
        }


        float _speed = 1f, _timeOrig = 1f;
        bool _frozen, _timeTouched;
        static readonly float[] SlowSteps = { 1f, 0.5f, 0.25f, 0.1f };

        /// <summary>Photo mode time: slow motion / freeze (also in play mode, by hotkey). Given back when photo mode ends.</summary>
        void TimeTick()
        {
            if (_photo.Active)
            {
                if (_freezeKey.Value.IsDown()) { _frozen = !_frozen; _status = _frozen ? "Frozen" : "Running"; }
                if (_slowKey.Value.IsDown()) { int i = Array.FindIndex(SlowSteps, x => Mathf.Approximately(x, _speed)); _speed = SlowSteps[(i + 1) % SlowSteps.Length]; _frozen = false; _status = $"Speed {_speed:0.##}x"; }
                if (_capturing) return;   // the transparent capture stops time itself
                float want = _frozen ? 0f : _speed;
                if (!Mathf.Approximately(Time.timeScale, want))
                {
                    if (!_timeTouched) { _timeOrig = Time.timeScale; _timeTouched = true; }
                    Time.timeScale = want;
                }
            }
            else if (_timeTouched) { Time.timeScale = _timeOrig; _timeTouched = false; _frozen = false; _speed = 1f; }
        }

        void ResetTime() { _frozen = false; _speed = 1f; if (_timeTouched) { Time.timeScale = _timeOrig; _timeTouched = false; } }


        bool _configDirty;

        void OnApplicationQuit() { if (_configDirty) try { Config.Save(); } catch { } }

        void OnDestroy() { try { if (_configDirty) Config.Save(); } catch { } try { ShowAll(); _tuner.ClearView(); _tuner.ResetAll(); ResetTime(); _photo.Exit(); InputBlock.Release(); Camera.onPreCull -= OnPreCullCamera; } catch { } }
    }
}
