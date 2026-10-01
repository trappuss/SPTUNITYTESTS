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
    public class InspectorPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.cod2eft.inspector";
        public const string PluginName = "COD2EFT Inspector";
        public const string Version = "0.11.0";

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
        bool _allSets;

        bool _open, _capturing;
        Rect _win = new Rect(40, 40, 600, 720);
        Vector2 _scroll;
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
        ConfigEntry<float> _winW, _winH;
        bool _resizing;
        ConfigEntry<string> _bgColor;
        bool _transparent;
        Vector2 _scroll3;
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
            _winW = Config.Bind("3. Panel", "Width", 600f, "Panel width (drag the bottom-right corner of the panel to change it).");
            _winH = Config.Bind("3. Panel", "Height", 720f, "Panel height (drag the bottom-right corner of the panel to change it).");
            _win.width = Mathf.Max(360f, _winW.Value); _win.height = Mathf.Max(240f, _winH.Value);
            _outDir = Path.Combine(Paths.GameRootPath, "COD2EFT_Screenshots");
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
            _tab = 1;
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
                    Reassert();
                    AutoWearTick();
                    var ow = Wearer.CheckOverwritten();
                    if (ow != null) { Log.LogWarning("Wear: " + ow); _status = "Note: " + ow; try { BodyScan.LogBodies(null); } catch { } }
                    if (_open) Refresh(false);
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

        void Refresh(bool force)
        {
            var old = _ti < _targets.Count ? _targets[_ti].Body ?? (Component)_targets[_ti].Player : null;
            _targets = BodyScan.FindTargets(_others.Value);
            _ti = Math.Max(0, _targets.FindIndex(t => (t.Body ?? (Component)t.Player) == old));
            _scan = _targets.Count > 0 ? Run(_targets[_ti]) : null;
            string sig = _scan?.Signature ?? "none";
            if (sig != _lastSignature || force)
            {
                if (sig != _lastSignature && _loggedScans.Add(sig)) LogScan();
                _lastSignature = sig;
            }
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

        // ------------------------------------------------------------------ panel

        void OnGUI()
        {
            if (_photo.Play && !_capturing)
            {
                Ui.Init();
                var r = new Rect(Screen.width / 2f - 170f, 18f, 340f, 30f);
                GUI.DrawTexture(r, Ui.Tex1(new Color(0f, 0f, 0f, 0.6f)));
                GUI.Label(r, "PLAY MODE  ·  Esc to leave" + (_frozen ? "  ·  FROZEN" : _speed < 1f ? $"  ·  {_speed:0.##}x" : ""), Ui.Banner);
                return;
            }
            if (!_open || _capturing) return;
            if (_unlockCursor.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            Ui.Init();
            var m = GUI.matrix;
            float s = Mathf.Clamp(_scale.Value, 0.75f, 2.5f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            if (_resizing)
            {
                if (!Input.GetMouseButton(0)) { _resizing = false; _winW.Value = _win.width; _winH.Value = _win.height; }
                else
                {
                    var mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
                    _win.width = Mathf.Clamp(mp.x - _win.x + 6f, 420f, Mathf.Max(420f, Screen.width / s));
                    _win.height = Mathf.Clamp(mp.y - _win.y + 6f, 320f, Mathf.Max(320f, Screen.height / s));
                }
            }
            // keep the panel on screen (a smaller resolution or a bigger scale can push it off)
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, Screen.width / s - 80f));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, Screen.height / s - 40f));
            _win = GUILayout.Window(0x0C0D2EF, _win, DrawWindow, GUIContent.none, Ui.Window, GUILayout.Width(_win.width), GUILayout.Height(_win.height));
            GUI.matrix = m;
        }

        // ------------------------------------------------------------------ panel frame: title bar, tabs, status bar

        static readonly string[] TabNames = { "Try on", "Photo", "Materials", "Meshes", "Catalog" };
        int _tab = 0;   // 0 try on (opens here), 1 photo, 2 meshes, 3 catalog
        bool _statusOpen;

        /// <summary>Where we are, in a few words (title bar): decides what Wear / photo mode act on.</summary>
        string Where()
        {
            var p = Game.MainPlayer();
            if (p != null) { var loc = Game.Location(p); return loc == "hideout" ? "Hideout" : "Raid" + (string.IsNullOrEmpty(loc) ? "" : " · " + loc); }
            return MenuTryOn.Available ? "Main menu · preview ready" : "Main menu";
        }

        void DrawWindow(int id)
        {
            try
            {
                // title bar (drag area)
                GUILayout.BeginHorizontal();
                GUILayout.Label("COD2EFT Inspector", Ui.Title, GUILayout.ExpandWidth(false));
                GUILayout.Label($"v{Version}   ●  {Where()}" + (_photo.Active ? "  ·  PHOTO MODE" : ""), Ui.Chip, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("A-", "Smaller text / panel"), Ui.Tiny, GUILayout.Width(28))) _scale.Value = Mathf.Clamp(_scale.Value - 0.1f, 0.75f, 2.5f);
                if (GUILayout.Button(new GUIContent("A+", "Bigger text / panel"), Ui.Tiny, GUILayout.Width(28))) _scale.Value = Mathf.Clamp(_scale.Value + 0.1f, 0.75f, 2.5f);
                if (GUILayout.Button(new GUIContent("×", $"Close ({_panelKey.Value})"), Ui.Tiny, GUILayout.Width(26))) TogglePanel();
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                // tabs
                GUILayout.BeginHorizontal();
                for (int i = 0; i < TabNames.Length; i++)
                    if (GUILayout.Button(TabNames[i], i == _tab ? Ui.TabOn : Ui.Tab, GUILayout.ExpandWidth(false))) _tab = i;
                GUILayout.EndHorizontal();
                Ui.Rule();
                GUILayout.Space(4);
                switch (_tab)
                {
                    case 1: DrawPhoto(); break;
                    case 2: DrawMaterials(); break;
                    case 3: DrawMeshes(); break;
                    case 4: DrawCatalog(); break;
                    default: DrawTryOnTab(); break;
                }
                DrawStatusBar();
            }
            catch (Exception e) { GUILayout.Label("Panel error (see BepInEx log): " + e.Message, Ui.Label); Game.LogOnce("gui:" + e.Message, "Panel error: " + e); }
            // resize grip, bottom-right corner (the size is kept in the config)
            var grip = new Rect(_win.width - 16f, _win.height - 16f, 16f, 16f);
            GUI.Label(grip, "//", Ui.Small);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && grip.Contains(Event.current.mousePosition)) { _resizing = true; Event.current.Use(); }
            GUI.DragWindow(new Rect(0, 0, _win.width, 30));
        }

        /// <summary>Bottom line: what the mouse is over (tooltip), else the last message coloured by outcome; click for the full text.</summary>
        void DrawStatusBar()
        {
            GUILayout.Space(4);
            string tip = GUI.tooltip;
            bool busy = Wearer.Busy || MenuTryOn.Busy || _capturing;
            string text; Color c;
            if (!string.IsNullOrEmpty(tip)) { text = tip; c = Ui.Muted; }
            else if (string.IsNullOrEmpty(_status)) { text = busy ? "Working ..." : "Ready. Hover over a control for help."; c = busy ? Ui.Warn : Ui.Muted; }
            else
            {
                string low = _status.ToLowerInvariant();
                c = busy ? Ui.Warn : low.Contains("fail") || low.Contains("error") || low.Contains("not found") ? Ui.Bad
                  : low.StartsWith("note") || low.Contains("warning") ? Ui.Warn : Ui.Good;
                int max = _statusOpen ? 2000 : 170;
                text = _status.Length <= max ? _status : _status.Substring(0, max) + (_statusOpen ? " ..." : "  [click: more]");
                if (_statusOpen && _status.Length > 170) text += "\n(full text also in BepInEx\\LogOutput.log)";
            }
            Ui.Status.normal.textColor = Ui.Status.hover.textColor = Ui.Status.active.textColor = c;
            if (GUILayout.Button(new GUIContent("●  " + text), Ui.Status, GUILayout.ExpandWidth(true))) _statusOpen = !_statusOpen;
        }

        // ------------------------------------------------------------------ small layout helpers

        readonly Dictionary<string, bool> _folds = new Dictionary<string, bool>();

        /// <summary>A card with a clickable title (folds it) and an optional Reset button. Returns false when folded (card already closed).</summary>
        bool BeginSection(string title, Action reset, bool openByDefault = true, string tip = null)
        {
            bool open;
            if (!_folds.TryGetValue(title, out open)) open = openByDefault;
            GUILayout.BeginVertical(Ui.CardBox);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent((open ? "▼  " : "►  ") + title, tip ?? (open ? "Fold" : "Unfold")), Ui.SectionHead)) _folds[title] = open = !open;
            GUILayout.FlexibleSpace();
            if (reset != null && GUILayout.Button(new GUIContent("Reset", "Back to the defaults of " + title.ToLowerInvariant()), Ui.Tiny, GUILayout.Width(52))) reset();
            GUILayout.EndHorizontal();
            if (!open) GUILayout.EndVertical();
            return open;
        }

        static void EndSection() => GUILayout.EndVertical();

        /// <summary>A row of buttons, the selected one highlighted. Returns the clicked index or -1.</summary>
        static int Segmented(string label, string[] items, Func<int, bool> selected)
        {
            int hit = -1;
            GUILayout.BeginHorizontal();
            if (label != null) GUILayout.Label(label, Ui.Small, GUILayout.Width(64));
            for (int i = 0; i < items.Length; i++)
                if (GUILayout.Button(items[i], selected(i) ? Ui.SegOn : Ui.Seg)) hit = i;
            GUILayout.EndHorizontal();
            return hit;
        }

        /// <summary>A slider row: label, slider, value; with a default a small R puts it back.</summary>
        static float Slider(string label, float v, float min, float max, float? def = null, string fmt = "0.##", string tip = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tip), Ui.Label, GUILayout.Width(104));
            v = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(80));
            GUILayout.Label(v.ToString(fmt), Ui.Value, GUILayout.Width(46));
            if (def.HasValue) { if (GUILayout.Button(new GUIContent("R", "Reset " + label.Trim().ToLowerInvariant()), Ui.Tiny, GUILayout.Width(22))) v = def.Value; }
            else GUILayout.Space(28);
            GUILayout.EndHorizontal();
            return v;
        }

        static bool Toggle(bool v, string text, string tip = null) => GUILayout.Toggle(v, new GUIContent(" " + text, tip), Ui.Toggle);

        void SupersizeRow()
        {
            int hit = Segmented("Supersize", new[] { "1x", "2x", "3x", "4x" }, i => _supersize.Value == i + 1);
            if (hit >= 0) _supersize.Value = hit + 1;
        }

        // ------------------------------------------------------------------ Meshes tab

        void DrawMeshes()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("◄", "Previous character"), Ui.Button, GUILayout.Width(32)) && _targets.Count > 0) { _ti = (_ti + _targets.Count - 1) % _targets.Count; _scan = Run(_targets[_ti]); if (_loggedScans.Add(_scan.Signature)) LogScan(); }
            GUILayout.Label(_targets.Count > 0 ? $"<b>{_targets[_ti].Label}</b>   <color=#9aa0a8>{_ti + 1} of {_targets.Count}</color>" : "No character found", Ui.Label);
            if (GUILayout.Button(new GUIContent("►", "Next character"), Ui.Button, GUILayout.Width(32)) && _targets.Count > 0) { _ti = (_ti + 1) % _targets.Count; _scan = Run(_targets[_ti]); if (_loggedScans.Add(_scan.Signature)) LogScan(); }
            if (GUILayout.Button(new GUIContent("Refresh", "Scan for characters again"), Ui.Button, GUILayout.Width(76))) Refresh(true);
            GUILayout.EndHorizontal();
            if (_targets.Count == 0)
                GUILayout.Label("In raid or the hideout this lists your character. In the menu, open the Character or Inventory screen, then press Refresh.", Ui.Small);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Show all", "Show every mesh you hid"), Ui.Button)) ShowAll();
            if (GUILayout.Button(new GUIContent("Hide gear", "Hide everything that is not a body part"), Ui.Button)) HideGear();
            if (GUILayout.Button(new GUIContent("Material report", "Writes renderer -> material -> shader / textures to a .txt and checks for problems"), Ui.Button)) MaterialReport();
            if (GUILayout.Button(new GUIContent($"Screenshot ({_shotKey.Value})", "PNG + .txt of the outfit, panel and HUD hidden"), Ui.Primary)) TakeScreenshot();
            GUILayout.EndHorizontal();
            SupersizeRow();
            GUILayout.Label("Checkbox = show / hide.  solo = show only that mesh.  [inactive] = switched off by the game,  [shadow only] = first-person body.", Ui.Small);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (_scan != null)
            {
                foreach (var g in _scan.Groups)
                {
                    var live = g.Entries.Where(e => e.R != null).ToList();
                    GUILayout.BeginVertical(Ui.CardBox);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(g.Open ? "▼" : "►", Ui.Tiny, GUILayout.Width(24)))
                    {
                        g.Open = !g.Open;
                        if (!_flipped.Remove(g.Name)) _flipped.Add(g.Name);
                    }
                    bool allShown = live.All(e => !IsHidden(e.R));
                    bool nv = GUILayout.Toggle(allShown, new GUIContent($" {g.Name}", "Show / hide the whole group"), Ui.Toggle);
                    if (nv != allShown) foreach (var e in live) SetHidden(e.R, !nv);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{live.Count} mesh{(live.Count == 1 ? "" : "es")}", Ui.Small, GUILayout.ExpandWidth(false));
                    GUILayout.EndHorizontal();
                    if (g.Open)
                        foreach (var e in live)
                        {
                            bool shown = !IsHidden(e.R);
                            string flags = (e.R.gameObject.activeInHierarchy ? "" : "  [inactive]") + (e.R.enabled ? "" : "  [off]") +
                                           (e.R.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly ? "  [shadow only]" : "");
                            GUILayout.BeginHorizontal();
                            GUILayout.Space(26);
                            if (GUILayout.Button(new GUIContent("solo", "Show only this mesh (Show all brings the rest back)"), Ui.Tiny, GUILayout.Width(40))) Solo(e.R);
                            bool n2 = GUILayout.Toggle(shown, " " + e.Path + flags, Ui.Toggle);
                            if (n2 != shown) SetHidden(e.R, !n2);
                            GUILayout.EndHorizontal();
                        }
                    GUILayout.EndVertical();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.Label("Files: " + _outDir, Ui.Small);
        }

        // ------------------------------------------------------------------ outfit catalog (stage 2 groundwork)

        internal static Catalog Cat;
        string _filter = "", _lastFilter = null, _part = "All", _source = "All";
        List<Outfit> _shown = new List<Outfit>();
        HashSet<string> _worn = new HashSet<string>();
        Vector2 _scroll2;

        internal Catalog GetCatalog(bool reload = false)
        {
            if (Cat != null && !reload) return Cat;
            try
            {
                Cat = Catalog.Load(_serverDir.Value, Paths.GameRootPath);
                Log.LogInfo($"Outfit catalog: {Cat.Items.Count} entries. " + string.Join(" | ", Cat.Notes));
            }
            catch (Exception e) { Log.LogError("Outfit catalog failed: " + e); Cat = new Catalog(); Cat.Notes.Add("failed: " + e.Message); }
            _lastFilter = null;
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
            Action<string> done = msg => { _status = msg; _lastFilter = null; try { Refresh(true); } catch { } };
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

        // ------------------------------------------------------------------ Try on tab

        void DrawTryOnTab()
        {
            var cat = GetCatalog();
            bool can = CanWear();
            bool inMenu = Game.MainPlayer() == null;
            _scroll3 = GUILayout.BeginScrollView(_scroll3, GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(Ui.CardBox);
            GUILayout.Label(inMenu ? (MenuTryOn.Available ? "Wear dresses the <b>Character / Inventory screen preview</b>." : "Open the <b>Character or Inventory screen</b> once, then Wear dresses its preview.")
                                   : "Wear dresses <b>your character</b> (and the inventory preview, if open).", Ui.Label);
            GUILayout.Label("Client-side only, nothing is saved: the next load shows your real outfit. Tops bring their first-person hands.", Ui.Small);
            GUILayout.BeginHorizontal();
            GUI.enabled = can && (inMenu || Wearer.HasOriginal);
            if (GUILayout.Button(new GUIContent("Restore my outfit", "Put your real outfit back"), Ui.Button))
            {
                if (inMenu) StartCoroutine(MenuTryOn.Reshow(msg => { _status = msg; Refresh(true); }));
                else WearItems(Wearer.OriginalOutfit(cat));
            }
            GUI.enabled = !inMenu;
            if (GUILayout.Button(new GUIContent(_photo.Active ? "Photo mode: on" : "Open photo mode", "Orbit camera and studio lights around your character"), Ui.Button))
            { if (!_photo.Active) TogglePhoto(); _tab = 1; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            var sets = cat.ModSets();
            if (BeginSection($"Mod outfits ({sets.Count}), newest first", null))
            {
                if (sets.Count == 0) GUILayout.Label("No mod outfit found. Build and install the mod with the Mod Builder, then press Reload in the Catalog tab.", Ui.Small);
                int n = 0;
                foreach (var set in sets)
                {
                    if (!_allSets && n++ >= 8) break;
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical();
                    GUILayout.Label($"<b>{set.Name}</b>", Ui.Label);
                    GUILayout.Label($"{set.Source}  ·  {string.Join(" + ", set.Pieces.Select(o => o.Part))}", Ui.Small);
                    GUILayout.EndVertical();
                    GUILayout.FlexibleSpace();
                    var shown = set.Pieces.Where(o => o.Part != "Hands").ToList();   // hands are often shared (game default hands)
                    if (shown.Count > 0 && shown.All(IsWorn)) Ui.Pill("WORN", Ui.Good);
                    else if (shown.Any(IsWorn)) Ui.Pill("PART WORN", Ui.Good);
                    GUI.enabled = can;
                    if (GUILayout.Button(new GUIContent("Wear", "Try this outfit on (not saved)"), Ui.Primary, GUILayout.Width(70))) WearItems(set.Pieces);
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    Ui.Rule();
                }
                if (sets.Count > 8) _allSets = Toggle(_allSets, $"Show all {sets.Count}");
                EndSection();
            }

            var head = Worn().Select(id => cat.ById(id)).FirstOrDefault(o => o != null && o.Part == "Head");
            if (Wearer.HasOriginal && head != null && BeginSection("Keep this head", null, false))
            {
                GUILayout.Label(cat.HasHeadVoiceSelector ? $"Saves <b>{head.Name}</b> to your PMC profile (kept after restart)." : "Needs the WTT HeadVoiceSelector server mod.", Ui.Label);
                GUI.enabled = cat.HasHeadVoiceSelector && !Wearer.Busy;
                if (GUILayout.Button("Save this head to my profile", Ui.Button)) StartCoroutine(Wearer.SaveHead(head.Id, msg => _status = msg));
                GUI.enabled = true;
                EndSection();
            }

            if (BeginSection("Diagnostics", null, false))
            {
                GUILayout.Label("For a report to Claude: writes every character body (owner, what it shows) to the BepInEx log.", Ui.Small);
                if (GUILayout.Button("Log all bodies", Ui.Button)) { BodyScan.LogBodies(null); _status = "Every PlayerBody written to the BepInEx log"; }
                EndSection();
            }
            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ Catalog tab

        void DrawCatalog()
        {
            var cat = GetCatalog();
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Search", "Name, id or bundle path"), Ui.Label, GUILayout.Width(52));
            _filter = GUILayout.TextField(_filter ?? "", Ui.Field, GUILayout.MinWidth(140));
            if (_filter.Length > 0 && GUILayout.Button(new GUIContent("×", "Clear the search"), Ui.Tiny, GUILayout.Width(24))) _filter = "";
            GUILayout.EndHorizontal();
            var parts = new[] { "All", "Top", "Pants", "Head", "Hands" };
            int hp = Segmented("Part", parts, i => _part == parts[i]);
            if (hp >= 0 && _part != parts[hp]) { _part = parts[hp]; _lastFilter = null; }
            var sources = new List<string> { "All", "Mods only" };
            sources.AddRange(cat.Sources);
            int si = Math.Max(0, sources.IndexOf(_source));
            GUILayout.BeginHorizontal();
            GUILayout.Label("From", Ui.Small, GUILayout.Width(64));
            if (GUILayout.Button("◄", Ui.Seg, GUILayout.Width(28))) { _source = sources[(si + sources.Count - 1) % sources.Count]; _lastFilter = null; }
            GUILayout.Label($"<b>{_source}</b>", Ui.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("►", Ui.Seg, GUILayout.Width(28))) { _source = sources[(si + 1) % sources.Count]; _lastFilter = null; }
            GUILayout.EndHorizontal();

            string key = _filter + "|" + _part + "|" + _source;
            if (key != _lastFilter)
            {
                _lastFilter = key;
                _worn = Worn();
                string f = (_filter ?? "").Trim();
                _shown = cat.Items.Where(o =>
                        (_part == "All" || o.Part == _part) &&
                        (_source == "All" || (_source == "Mods only" ? o.Source != "vanilla" : o.Source == _source)) &&
                        (f.Length == 0 || (o.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (o.Id ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (o.Bundle ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(o => o.Source == "vanilla").ThenBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(o => o.Part).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            int missing = cat.Items.Count(o => o.BundleFound == false);
            GUILayout.Label($"{_shown.Count} of {cat.Items.Count} shown" + (_shown.Count > 300 ? " (first 300: narrow the search)" : "") +
                            (missing > 0 ? $"  ·  {missing} with a missing bundle" : ""), Ui.Small);
            foreach (var n in cat.Notes.Where(n => n.Contains("not found") || n.Contains("failed") || n.Contains("not readable")).Take(3))
                GUILayout.Label(n, Ui.Small);

            // what is worn now, pinned above the list (the list itself keeps its order, so Wear doesn't move your place)
            var wearing = cat.Items.Where(IsWorn).OrderBy(o => o.Part == "Head" ? 0 : o.Part == "Top" ? 1 : o.Part == "Pants" ? 2 : 3).ToList();
            if (wearing.Count > 0)
            {
                GUILayout.BeginVertical(Ui.CardBox);
                GUILayout.Label("Wearing now", Ui.Small);
                foreach (var o in wearing) GUILayout.Label($"<b>{o.Part}</b>   {o.Name}   <color=#9aa0a8>{o.Source}</color>", Ui.Label);
                GUILayout.EndVertical();
            }
            _topsBringHands.Value = Toggle(_topsBringHands.Value, "Tops bring their hands", "Off: wear hands separately (Part: Hands)");
            _scroll2 = GUILayout.BeginScrollView(_scroll2, GUILayout.ExpandHeight(true));
            bool canWear = CanWear();
            foreach (var o in _shown.Take(300))
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = canWear && o.BundleFound != false;
                if (GUILayout.Button(new GUIContent("Wear", o.Part == "Top" && _topsBringHands.Value ? "Try on with its hands (not saved)" : "Try on (not saved)"), Ui.Button, GUILayout.Width(58))) WearItems(new List<Outfit> { o });
                GUI.enabled = true;
                GUILayout.BeginVertical();
                GUILayout.Label($"<b>{o.Name}</b>", Ui.Label);
                GUILayout.Label($"{o.Part}  ·  {o.Source}  ·  {o.Id}", Ui.Small);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (IsWorn(o)) Ui.Pill("WORN", Ui.Good);
                if (o.BundleFound == false) Ui.Pill("BUNDLE MISSING", Ui.Bad);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Reload", "Read the server's outfit files again (after installing a mod)"), Ui.Button)) { cat = GetCatalog(true); _lastFilter = null; }
            if (GUILayout.Button(new GUIContent("Write catalog to file", "A .txt of every entry with notes, for a report"), Ui.Button)) WriteCatalog(cat);
            GUILayout.EndHorizontal();
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

        void DrawPhoto()
        {
            var ph = _photo;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(ph.Active ? "Exit photo mode" : "Start photo mode",
                    ph.Active ? "Back to first person; camera, lights, background and character are restored" : "Orbit camera around your character (raid / hideout)"),
                    ph.Active ? Ui.Button : Ui.Primary, GUILayout.MinWidth(150))) TogglePhoto();
            GUI.enabled = ph.Active;
            if (GUILayout.Button(new GUIContent($"Screenshot ({_shotKey.Value})", "PNG + .txt, panel and HUD hidden"), Ui.Primary)) TakeScreenshot();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Reset all", "Photo mode off and everything as before: camera, lights, background, character, pose, meshes, outfit"), Ui.Button)) ResetAll();
            GUILayout.EndHorizontal();
            if (!ph.Active)
            {
                GUILayout.BeginVertical(Ui.CardBox);
                GUILayout.Label("Photo mode puts the camera around your own character (raid or hideout). The character takes no input while it is on.", Ui.Label);
                GUILayout.Label("For the same light every time: the hideout, studio lights on, background isolated. In the main menu, use the game's own preview.", Ui.Small);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                return;
            }
            GUILayout.Label("Mouse outside the panel:  right drag = orbit   ·   left drag = turn character / aim   ·   wheel = zoom" +
                            (_playOnDoubleClick.Value ? "   ·   double-click = play mode (full control, Esc to leave)" : ""), Ui.Small);
            if (GUILayout.Button(new GUIContent("Play mode", "Full control of the character (walk, shoot, reload, inspect) with this camera; Esc to leave"), Ui.Button)) SetPlay(true);
            _playCamTurns.Value = Toggle(_playCamTurns.Value, "Play mode: camera turns with the character", "Off: the camera keeps its world angle while you walk / turn");
            _scrollPhoto = GUILayout.BeginScrollView(_scrollPhoto, GUILayout.ExpandHeight(true));

            if (BeginSection("Camera", ph.ResetCamera))
            {
                int a = Segmented("Angle", PhotoMode.Angles.Select(x => x.Name).ToArray(), i => Mathf.Abs(Mathf.DeltaAngle(ph.Yaw, ph.YawFor(PhotoMode.Angles[i].Yaw))) < 0.5f);
                if (a >= 0) ph.Yaw = ph.YawFor(PhotoMode.Angles[a].Yaw);
                int f = Segmented("Framing", PhotoMode.Framings.Select(x => x.Name).ToArray(),
                                  i => Mathf.Abs(ph.Height - PhotoMode.Framings[i].Height) < 0.01f && Mathf.Abs(ph.Distance - PhotoMode.Framings[i].Distance) < 0.01f);
                if (f >= 0) { ph.Height = PhotoMode.Framings[f].Height; ph.Distance = PhotoMode.Framings[f].Distance; }
                ph.Yaw = Slider("Orbit", ph.Yaw, 0f, 360f, ph.YawFor(0f), "0", "Camera angle around the character (0 = front)");
                ph.Pitch = Slider("Tilt", ph.Pitch, -60f, 80f, PhotoMode.DefPitch, "0", "Camera height angle");
                ph.Distance = Slider("Distance", ph.Distance, 0.3f, 12f, PhotoMode.DefDistance, "0.00", "Metres from the character (wheel)");
                ph.Height = Slider("Aim height", ph.Height, 0f, 2.2f, PhotoMode.DefHeight, "0.00", "The point the camera looks at, metres above the feet");
                ph.Fov = Slider("Field of view", ph.Fov, 10f, 90f, PhotoMode.DefFov, "0", "Lower = more telephoto, less distortion");
                ph.Ortho = Toggle(ph.Ortho, "Orthographic", "No perspective (for reference sheets); the view size follows distance and field of view");
                EndSection();
            }

            if (BeginSection("Character", () => { ph.ResetCharacter(); if (_pose != "Stand") SetPose("Stand"); }))
            {
                int p = Segmented("Pose", Poses.Names, i => _pose == Poses.Names[i]);
                if (p >= 0 && _pose != Poses.Names[p]) SetPose(Poses.Names[p]);
                ph.CharYaw = Slider("Turn", ph.CharYaw, -180f, 180f, 0f, "0", "Turns the character (left drag sideways)");
                ph.CharPitch = Slider("Aim up / down", ph.CharPitch, -60f, 60f, 0f, "0", "Where the character looks / aims (left drag up / down; F12 can invert)");
                EndSection();
            }

            if (BeginSection("Lights", () => { ph.ResetLights(); _lightStrength.Value = 1f; }))
            {
                GUILayout.BeginHorizontal();
                bool li = Toggle(ph.Lights, "Studio lights", "Key, fill and rim spot lights");
                if (li != ph.Lights) ph.SetLights(li);
                ph.LightsFollowCamera = Toggle(ph.LightsFollowCamera, "Follow the camera", "Off: the lights stay fixed to the character's front");
                GUILayout.EndHorizontal();
                _lightStrength.Value = Slider("Strength", _lightStrength.Value, 0f, 4f, 1f, "0.00");
                EndSection();
            }

            if (BeginSection("Background", () => { ph.ResetBackground(); _transparent = false; _bgColor.Value = "#00B140"; }))
            {
                ph.Isolate = Toggle(ph.Isolate, "Isolate character", "Hide the world: only you and what you wear / hold, on a solid colour");
                if (ph.Isolate)
                {
                    Color bc;
                    if (ColorUtility.TryParseHtmlString(_bgColor.Value, out bc) && !_colourLoaded) { ph.BgColor = bc; _colourLoaded = true; }
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Colour", Ui.Small, GUILayout.Width(64));
                    var r = GUILayoutUtility.GetRect(44, 22, GUILayout.Width(44));
                    GUI.DrawTexture(r, Ui.Tex1(new Color(ph.BgColor.r, ph.BgColor.g, ph.BgColor.b, 1f)));
                    foreach (var c in new[] { ("Green", PhotoMode.DefaultBg), ("Blue", new Color(0f, 0.28f, 0.73f)), ("White", Color.white), ("Grey", new Color(0.5f, 0.5f, 0.5f)), ("Black", Color.black) })
                        if (GUILayout.Button(c.Item1, ph.BgColor == c.Item2 ? Ui.SegOn : Ui.Seg)) ph.BgColor = c.Item2;
                    GUILayout.EndHorizontal();
                    float cr = Slider("Red", ph.BgColor.r, 0f, 1f, null, "0.00"), cg = Slider("Green", ph.BgColor.g, 0f, 1f, null, "0.00"), cb = Slider("Blue", ph.BgColor.b, 0f, 1f, null, "0.00");
                    ph.BgColor = new Color(cr, cg, cb);
                    string hex = "#" + ColorUtility.ToHtmlStringRGB(ph.BgColor);
                    if (hex != _bgColor.Value) _bgColor.Value = hex;
                    GUILayout.Label(hex, Ui.Small);
                    ph.NoFog = Toggle(ph.NoFog, "No fog / sky haze", "Switches off camera effects that tint the background");
                    ph.NoPost = Toggle(ph.NoPost, "No post effects", "Exact colours; the character looks less 'in game'");
                    ph.WorldLightsOff = Toggle(ph.WorldLightsOff, "Studio lights only", "Switches the world's lights off");
                    _transparent = Toggle(_transparent, "Transparent PNG", "Screenshots get an alpha channel (2 captures: black + grey; max 2x supersize)");
                }
                EndSection();
            }

            DrawTime();
            DrawPresets();
            if (BeginSection("Captures", null))
            {
                SupersizeRow();
                if (GUILayout.Button(new GUIContent("Turntable", "4 screenshots: front, left, back, right"), Ui.Button)) { if (!_capturing) StartCoroutine(Turntable()); }
                if (GUILayout.Button(new GUIContent("Pose turntables", "4 angles in each of the 5 poses (clipping check)"), Ui.Button)) { if (!_capturing && !Wearer.Busy) StartCoroutine(PoseTurntables()); }
                var newest = GetCatalog().ModSets().FirstOrDefault();
                if (newest != null && GUILayout.Button(new GUIContent($"A/B turntables: {newest.Source}", "A turntable of every outfit of the newest mod, then of your own outfit, same camera and lights"), Ui.Button))
                    { if (!_capturing && !Wearer.Busy) StartCoroutine(CompareBatch()); }
                if (newest != null)
                    GUILayout.Label("Compares:  " + string.Join("   vs   ", GetCatalog().ModSets().Where(x => x.Source == newest.Source).Select(x => x.Name)) +
                                    "   vs   your own outfit", Ui.Small);
                GUILayout.Label("Files: " + _outDir, Ui.Small);
                EndSection();
            }
            GUILayout.EndScrollView();
        }

        bool _colourLoaded;
        Vector2 _scrollPhoto;

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
        Vector2 _scrollMat;
        readonly Dictionary<string, Vector2> _ranges = new Dictionary<string, Vector2>();

        List<Renderer> TunerRenderers() =>
            _scan == null ? new List<Renderer>() :
            _scan.Groups.Where(g => _matGear || g.Kind == "body").SelectMany(g => g.Entries).Select(e => e.R).Where(r => r != null).Distinct().ToList();

        /// <summary>renderer -> its real materials (the channel view swaps them for unlit copies).</summary>
        IEnumerable<Material> RealMaterials(Renderer r) => _tuner.Saved(r) ?? r.sharedMaterials;

        string UsedBy(Material m) =>
            string.Join(", ", TunerRenderers().Where(r => RealMaterials(r).Contains(m)).Select(r => r.name).Distinct().Take(4));

        void DrawMaterials()
        {
            if (_scan == null) { GUILayout.Label("No character. In raid / the hideout this shows your outfit's materials; in the menu open the Character screen and press Refresh on the Meshes tab.", Ui.Label); return; }
            var rends = TunerRenderers();
            var mats = rends.SelectMany(RealMaterials).Where(m => m != null).Distinct().ToList();

            // channel view
            var slots = MaterialTuner.TextureSlots(mats);
            var names = new List<string> { "Normal" };
            names.AddRange(slots.Select(MaterialTuner.Pretty));
            int v = Segmented("View", names.ToArray(), i => i == 0 ? _tuner.View == null : _tuner.View == slots[i - 1]);
            if (v == 0) _tuner.ClearView();
            else if (v > 0) _tuner.SetView(slots[v - 1], rends);
            GUILayout.BeginHorizontal();
            _matGear = Toggle(_matGear, "Gear too", "Also list / view the materials of gear, not only body parts");
            _matSameShader = Toggle(_matSameShader, "Change all with this shader", "A change goes to every listed material using the same shader");
            GUILayout.FlexibleSpace();
            GUI.enabled = _tuner.ChangedCount > 0;
            if (GUILayout.Button(new GUIContent("Reset all", "Every material back to the bundle's values"), Ui.Button)) _tuner.ResetAll();
            if (GUILayout.Button(new GUIContent($"Save tuning ({_tuner.ChangedCount})", "Writes the changed values (new and was) to a .txt for the converter"), Ui.Primary)) SaveTuning();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            _scrollMat = GUILayout.BeginScrollView(_scrollMat, GUILayout.ExpandHeight(true));
            if (!mats.Contains(_selMat)) _selMat = mats.FirstOrDefault();
            foreach (var m in mats)
            {
                bool sel = m == _selMat;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent((sel ? "▼  " : "►  ") + m.name.Replace(" (Instance)", ""), "Used by " + UsedBy(m)), sel ? Ui.SegOn : Ui.Seg)) _selMat = sel ? null : m;
                if (_tuner.Changed(m)) Ui.Pill("CHANGED", Ui.Warn);
                GUILayout.EndHorizontal();
                if (sel) DrawMaterialEditor(m, mats);
            }
            GUILayout.EndScrollView();
        }

        void DrawMaterialEditor(Material m, List<Material> all)
        {
            GUILayout.BeginVertical(Ui.CardBox);
            GUILayout.Label($"shader <b>{m.shader?.name}</b>   ·   render queue {m.renderQueue}   ·   used by {UsedBy(m)}", Ui.Small);
            var targets = _matSameShader ? all.Where(x => x.shader == m.shader).ToList() : new List<Material> { m };
            foreach (var p in MaterialTuner.Props(m))
            {
                string key = m.GetInstanceID() + p.Name;
                switch (p.Type)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float cur = m.GetFloat(p.Name);
                        Vector2 rg;
                        if (!_ranges.TryGetValue(key, out rg))
                        {
                            var o = _tuner.Original(m, p.Name) as float? ?? cur;
                            rg = p.Type == ShaderPropertyType.Range ? new Vector2(p.Min, p.Max) : new Vector2(Mathf.Min(0f, o * 2f), Mathf.Max(1f, o * 2f));
                            _ranges[key] = rg;
                        }
                        float nv = PropSlider(m, p.Name, cur, rg.x, rg.y);
                        if (!Mathf.Approximately(nv, cur)) foreach (var t in targets) if (t.HasProperty(p.Name)) _tuner.SetFloat(t, p.Name, nv);
                        break;
                    }
                    case ShaderPropertyType.Color:
                    {
                        var c = m.GetColor(p.Name);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(new GUIContent(p.Name, m.shader.GetPropertyDescription(m.shader.FindPropertyIndex(p.Name))), _tuner.Changed(m, p.Name) ? Ui.H : Ui.Label, GUILayout.Width(150));
                        var r = GUILayoutUtility.GetRect(28, 18, GUILayout.Width(28));
                        GUI.DrawTexture(r, Ui.Tex1(new Color(c.r, c.g, c.b, 1f)));
                        float max = Mathf.Max(1f, c.maxColorComponent);
                        var n = new Color(GUILayout.HorizontalSlider(c.r, 0f, max, GUILayout.MinWidth(40)), GUILayout.HorizontalSlider(c.g, 0f, max, GUILayout.MinWidth(40)),
                                          GUILayout.HorizontalSlider(c.b, 0f, max, GUILayout.MinWidth(40)), GUILayout.HorizontalSlider(c.a, 0f, 1f, GUILayout.MinWidth(30)));
                        ResetButton(m, p.Name, targets);
                        GUILayout.EndHorizontal();
                        if (n != c) foreach (var t in targets) if (t.HasProperty(p.Name)) _tuner.SetColor(t, p.Name, n);
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        var vv = m.GetVector(p.Name);
                        GUILayout.Label($"{p.Name}   {vv.x:0.###}, {vv.y:0.###}, {vv.z:0.###}, {vv.w:0.###}", Ui.Small);
                        break;
                    }
                    case ShaderPropertyType.Texture:
                    {
                        var tex = m.GetTexture(p.Name);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"{p.Name}   " + (tex != null ? $"<b>{tex.name}</b>  {tex.width}x{tex.height}" : "<color=#9aa0a8>(empty)</color>"), Ui.Small);
                        GUILayout.FlexibleSpace();
                        if (tex != null && GUILayout.Button(new GUIContent("view", "Show this texture slot on the whole outfit"), Ui.Tiny, GUILayout.Width(44))) _tuner.SetView(p.Name, TunerRenderers());
                        GUILayout.EndHorizontal();
                        break;
                    }
                }
            }
            GUILayout.BeginHorizontal();
            GUI.enabled = _tuner.Changed(m);
            if (GUILayout.Button(new GUIContent("Reset this material", "Back to the bundle's values"), Ui.Button)) _tuner.Reset(m);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        float PropSlider(Material m, string prop, float v, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(prop, m.shader.GetPropertyDescription(m.shader.FindPropertyIndex(prop))), _tuner.Changed(m, prop) ? Ui.H : Ui.Label, GUILayout.Width(150));
            v = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(80));
            GUILayout.Label(v.ToString("0.###"), Ui.Value, GUILayout.Width(50));
            ResetButton(m, prop, null);
            GUILayout.EndHorizontal();
            return v;
        }

        void ResetButton(Material m, string prop, List<Material> targets)
        {
            GUI.enabled = _tuner.Changed(m, prop);
            if (GUILayout.Button(new GUIContent("R", "Back to " + (_tuner.Original(m, prop) ?? "?")), Ui.Tiny, GUILayout.Width(22)))
                foreach (var t in (IEnumerable<Material>)targets ?? new[] { m }) _tuner.ResetProp(t, prop);
            GUI.enabled = true;
        }

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

        void DrawPresets()
        {
            if (!BeginSection("Presets", null, true, "Save / load the whole setup: camera, character, pose, lights, background, speed")) return;
            GUILayout.BeginHorizontal();
            _presetName = GUILayout.TextField(_presetName ?? "", Ui.Field, GUILayout.MinWidth(120));
            GUI.enabled = !string.IsNullOrEmpty(_presetName?.Trim());
            if (GUILayout.Button(new GUIContent("Save", "Save the current setup under this name (same name = overwrite)"), Ui.Primary, GUILayout.Width(60)))
            {
                string n = _presetName.Trim().Replace("|", "/");
                Presets().RemoveAll(kv => kv.Key == n);
                _presets.Add(new KeyValuePair<string, string>(n, PresetString()));
                SavePresets();
                _status = "Preset saved: " + n;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            foreach (var kv in Presets().ToList())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Key, Ui.Label);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Load", "Apply this setup"), Ui.Button, GUILayout.Width(60))) { ApplyPreset(kv.Value); _presetName = kv.Key; _status = "Preset loaded: " + kv.Key; }
                if (GUILayout.Button(new GUIContent("×", "Delete this preset"), Ui.Tiny, GUILayout.Width(24))) { _presets.Remove(kv); SavePresets(); }
                GUILayout.EndHorizontal();
            }
            if (Presets().Count == 0) GUILayout.Label("No presets yet. Set up a shot, type a name, Save. Stored in BepInEx\\config\\COD2EFTInspector_photo_presets.txt.", Ui.Small);
            EndSection();
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

        void DrawTime()
        {
            if (!BeginSection("Time", ResetTime, false, "Slow motion / freeze, for catching animations (reload, inspect) mid-motion")) return;
            _speed = Slider("Speed", _speed, 0.05f, 1f, 1f, "0.00", "Game speed while photo mode is on");
            _frozen = Toggle(_frozen, $"Freeze ({_freezeKey.Value})", "Stops the game; the camera still moves");
            GUILayout.Label($"Hotkeys, also in play mode: {_freezeKey.Value} = freeze,  {_slowKey.Value} = 1x / 0.5x / 0.25x / 0.1x.", Ui.Small);
            EndSection();
        }

        void OnDestroy() { try { ShowAll(); _tuner.ClearView(); _tuner.ResetAll(); ResetTime(); _photo.Exit(); InputBlock.Release(); Camera.onPreCull -= OnPreCullCamera; } catch { } }
    }
}
