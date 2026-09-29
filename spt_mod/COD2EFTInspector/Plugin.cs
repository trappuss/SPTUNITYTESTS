// COD2EFT Inspector: an in-game panel for checking converted characters.
//  - lists the character's renderers by part (head / top / pants / hands / gear) with show/hide checkboxes
//  - screenshot (PNG, supersize 1-4x, panel and HUD hidden) + a .txt of the outfit and hidden meshes
//  - material report (renderer -> material -> shader, textures, values)
// Output: <SPT game>\COD2EFT_Screenshots\. Log lines go to BepInEx\LogOutput.log ("COD2EFT Inspector").
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace COD2EFTInspector
{
    [BepInPlugin(Guid, PluginName, Version)]   // GUID without '_' (an '_' in any SPT mod GUID stops all mods loading)
    public class InspectorPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.cod2eft.inspector";
        public const string PluginName = "COD2EFT Inspector";
        public const string Version = "0.6.0";

        internal static ManualLogSource Log;
        internal static InspectorPlugin Instance;

        ConfigEntry<KeyboardShortcut> _panelKey, _shotKey;
        ConfigEntry<int> _supersize;
        ConfigEntry<bool> _hideHud, _others, _unlockCursor;
        ConfigEntry<float> _scale;
        ConfigEntry<string> _serverDir;
        ConfigEntry<bool> _autoWear;
        Component _autoFor;
        float _autoAt;
        bool _allSets;

        bool _open, _capturing;
        Rect _win = new Rect(40, 40, 540, 660);
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
            _tab = 2;
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
                if (_panelKey.Value.IsDown()) TogglePanel();
                if (_shotKey.Value.IsDown()) TakeScreenshot();
                if (Time.unscaledTime >= _nextTick)
                {
                    _nextTick = Time.unscaledTime + 1f;
                    Reassert();
                    AutoWearTick();
                    if (_open) Refresh(false);
                }
                if ((_open || _photo.Active) && _unlockCursor.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                if (_photo.Active) _photo.HandleMouse(MouseOverPanel());
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
            if (!_open || _capturing) return;
            if (_unlockCursor.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            var m = GUI.matrix;
            float s = Mathf.Clamp(_scale.Value, 0.75f, 2.5f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            _win = GUILayout.Window(0x0C0D2EF, _win, DrawWindow, $"{PluginName} v{Version}");
            GUI.matrix = m;
        }

        void DrawWindow(int id)
        {
            try { DrawContents(); }
            catch (Exception e) { GUILayout.Label("Panel error (see BepInEx log): " + e.Message); Game.LogOnce("gui:" + e.Message, "Panel error: " + e); }
            GUI.DragWindow();
        }

        int _tab = 1;   // 0 meshes, 1 outfits / try on (opens here), 2 photo

        void DrawContents()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_tab == 0, " Meshes", GUILayout.Width(90))) _tab = 0;
            if (GUILayout.Toggle(_tab == 2, " Photo", GUILayout.Width(80))) _tab = 2;
            if (GUILayout.Toggle(_tab == 1, " Outfits / try on", GUILayout.Width(190))) _tab = 1;
            GUILayout.EndHorizontal();
            if (_tab == 1) { DrawOutfits(); return; }
            if (_tab == 2) { DrawPhoto(); return; }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(28)) && _targets.Count > 0) { _ti = (_ti + _targets.Count - 1) % _targets.Count; _scan = Run(_targets[_ti]); if (_loggedScans.Add(_scan.Signature)) LogScan(); }
            GUILayout.Label(_targets.Count > 0 ? $"{_ti + 1}/{_targets.Count}  {_targets[_ti].Label}" : "No character found");
            if (GUILayout.Button(">", GUILayout.Width(28)) && _targets.Count > 0) { _ti = (_ti + 1) % _targets.Count; _scan = Run(_targets[_ti]); if (_loggedScans.Add(_scan.Signature)) LogScan(); }
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) Refresh(true);
            GUILayout.EndHorizontal();
            if (_targets.Count == 0)
                GUILayout.Label("In raid or the hideout this lists your character. In the menu, open the Character or Inventory screen, then press Refresh.");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Show all")) ShowAll();
            if (GUILayout.Button("Hide gear")) HideGear();
            if (GUILayout.Button($"Screenshot ({_shotKey.Value})")) TakeScreenshot();
            if (GUILayout.Button("Material report")) MaterialReport();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Supersize:", GUILayout.Width(70));
            for (int i = 1; i <= 4; i++)
                if (GUILayout.Toggle(_supersize.Value == i, $"{i}x", GUILayout.Width(40)) && _supersize.Value != i) _supersize.Value = i;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);

            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_scan != null)
            {
                foreach (var g in _scan.Groups)
                {
                    var live = g.Entries.Where(e => e.R != null).ToList();
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(g.Open ? "-" : "+", GUILayout.Width(22)))
                    {
                        g.Open = !g.Open;
                        if (!_flipped.Remove(g.Name)) _flipped.Add(g.Name);
                    }
                    bool allShown = live.All(e => !IsHidden(e.R));
                    bool nv = GUILayout.Toggle(allShown, $" {g.Name}  ({live.Count})");
                    if (nv != allShown) foreach (var e in live) SetHidden(e.R, !nv);
                    GUILayout.EndHorizontal();
                    if (!g.Open) continue;
                    foreach (var e in live)
                    {
                        bool shown = !IsHidden(e.R);
                        string flags = (e.R.gameObject.activeInHierarchy ? "" : "  [inactive]") + (e.R.enabled ? "" : "  [off]") +
                                       (e.R.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly ? "  [shadow only]" : "");
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(30);
                        if (GUILayout.Button("solo", GUILayout.Width(40))) Solo(e.R);
                        bool n2 = GUILayout.Toggle(shown, " " + e.Path + flags);
                        if (n2 != shown) SetHidden(e.R, !n2);
                        GUILayout.EndHorizontal();
                    }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.Label($"Files: {_outDir}");
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
            Log.LogInfo($"A/B: {runs.Count} outfit(s) of [{newest.Source}] + your own outfit");
            int done = 0;
            for (int i = 0; i <= runs.Count; i++)
            {
                List<Outfit> items;
                string label;
                if (i < runs.Count) { items = runs[i].Value; label = runs[i].Key; }
                else { items = Wearer.OriginalOutfit(cat); label = "your own outfit (reference)"; }
                if (items.Count == 0) continue;
                _status = $"A/B {i + 1}/{runs.Count + 1}: {label}";
                string msg = null;
                yield return StartCoroutine(Wearer.Wear(player, items, m => msg = m));
                if (msg == null || msg.StartsWith("Try-on failed")) { Log.LogWarning($"A/B: skipped '{label}': {msg}"); continue; }
                yield return new WaitForSecondsRealtime(1.5f);   // textures stream in
                Refresh(true);
                yield return StartCoroutine(Turntable());
                done++;
            }
            _status = $"A/B done: {done} turntable(s) of 4 screenshots in {_outDir}";
            Log.LogInfo(_status);
        }

        /// <summary>Try-on is possible: your character (raid / hideout) or a menu preview that the game has shown.</summary>
        bool CanWear() => !Wearer.Busy && !MenuTryOn.Busy && (Game.MainPlayer() != null || MenuTryOn.Available);

        void WearItems(List<Outfit> items)
        {
            if (!CanWear() || items == null || items.Count == 0) return;
            var all = WithHands(items);
            _status = "Loading " + string.Join(", ", all.Select(o => o.Name)) + " ...";
            Action<string> done = msg => { _status = msg; _lastFilter = null; try { Refresh(true); } catch { } };
            if (Game.MainPlayer() != null) StartCoroutine(Wearer.Wear(Game.MainPlayer(), all, done));
            else StartCoroutine(MenuTryOn.Wear(_scan?.Target?.Player == null ? _scan?.Target?.Body : null, all, done));
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

        void DrawTryOn(Catalog cat)
        {
GUILayout.Label("Try on (hideout / raid: your character; menu: the Character or Inventory screen preview). Not saved. Newest mod first:");
            var sets = cat.ModSets();
            bool can = CanWear();
            int n = 0;
            foreach (var set in sets)
            {
                if (!_allSets && n++ >= 6) break;
                GUILayout.BeginHorizontal();
                GUI.enabled = can;
                if (GUILayout.Button("Wear", GUILayout.Width(55))) WearItems(set.Pieces);
                GUI.enabled = true;
                GUILayout.Label($"[{set.Source}] {set.Name}   " + string.Join(" + ", set.Pieces.Select(o => o.Part)));
                GUILayout.EndHorizontal();
            }
            var head = Worn().Select(id => cat.ById(id)).FirstOrDefault(o => o != null && o.Part == "Head");
            if (Wearer.HasOriginal && head != null)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = cat.HasHeadVoiceSelector && !Wearer.Busy;
                if (GUILayout.Button("Save this head to my profile", GUILayout.Width(200)))
                    StartCoroutine(Wearer.SaveHead(head.Id, msg => _status = msg));
                GUI.enabled = true;
                GUILayout.Label(cat.HasHeadVoiceSelector ? $"{head.Name} (kept after restart)" : "needs the WTT HeadVoiceSelector server mod");
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (sets.Count > 6) _allSets = GUILayout.Toggle(_allSets, $" all {sets.Count} mod outfits");
            bool inMenu = Game.MainPlayer() == null;
            GUI.enabled = can && (inMenu || Wearer.HasOriginal);
            if (GUILayout.Button("Restore my outfit", GUILayout.Width(140)))
            {
                if (inMenu) StartCoroutine(MenuTryOn.Reshow(msg => { _status = msg; Refresh(true); }));
                else WearItems(Wearer.OriginalOutfit(cat));
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!can && !Wearer.Busy && !MenuTryOn.Busy) GUILayout.Label("(Wear needs your character (hideout / raid) or, in the menu, the Character or Inventory screen opened once.)");
        }

        void DrawOutfits()
        {
            var cat = GetCatalog();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload", GUILayout.Width(70))) cat = GetCatalog(true);
            if (GUILayout.Button("Write catalog to file", GUILayout.Width(160))) WriteCatalog(cat);
            GUILayout.Label($"{cat.Items.Count} entries, {cat.Items.Count(o => o.BundleFound == false)} missing bundle");
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            DrawTryOn(cat);
            foreach (var n in cat.Notes.Where(n => n.StartsWith("Server") || n.Contains("not found") || n.Contains("failed") || n.Contains("not readable")).Take(4))
                GUILayout.Label(n);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter:", GUILayout.Width(45));
            _filter = GUILayout.TextField(_filter ?? "", GUILayout.Width(170));
            foreach (var p in new[] { "All", "Top", "Pants", "Head", "Hands" })
                if (GUILayout.Toggle(_part == p, p, GUILayout.Width(52)) && _part != p) { _part = p; _lastFilter = null; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            var sources = new List<string> { "All", "Mods only" };
            sources.AddRange(cat.Sources);
            int si = Math.Max(0, sources.IndexOf(_source));
            if (GUILayout.Button("<", GUILayout.Width(28))) { _source = sources[(si + sources.Count - 1) % sources.Count]; _lastFilter = null; }
            GUILayout.Label("From: " + _source);
            if (GUILayout.Button(">", GUILayout.Width(28))) { _source = sources[(si + 1) % sources.Count]; _lastFilter = null; }
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
                    .OrderByDescending(IsWorn).ThenBy(o => o.Source == "vanilla").ThenBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(o => o.Part).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            GUILayout.Label($"{_shown.Count} shown" + (_shown.Count > 300 ? " (first 300; narrow the filter)" : "") +
                            ". Wear = try on (tops bring their hands).");
            _scroll2 = GUILayout.BeginScrollView(_scroll2);
            bool canWear = CanWear();
            foreach (var o in _shown.Take(300))
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = canWear && o.Part != "Hands" && o.BundleFound != false;
                if (GUILayout.Button("Wear", GUILayout.Width(50))) WearItems(new List<Outfit> { o });
                GUI.enabled = true;
                GUILayout.Label($"{(IsWorn(o) ? "WORN  " : "")}[{o.Source}] {o.Part}: {o.Name}   {o.Id}" + (o.BundleFound == false ? "   BUNDLE MISSING" : ""));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
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
            if (GUILayout.Button(ph.Active ? "Photo mode OFF (back to first person)" : "Photo mode ON")) TogglePhoto();
            if (GUILayout.Button($"Screenshot ({_shotKey.Value})", GUILayout.Width(150))) TakeScreenshot();
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            if (!ph.Active)
            {
                GUILayout.Label("Photo mode puts the camera around your own character (raid or hideout) and stops the character taking input. " +
                                "For the same light every time, use the hideout and the studio lights. In the main menu, use the game's own character preview.");
                return;
            }
            GUILayout.Label("Right mouse drag = orbit, wheel = zoom (outside this panel).");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Angle:", GUILayout.Width(50));
            foreach (var a in PhotoMode.Angles) if (GUILayout.Button(a.Name)) ph.Yaw = a.Yaw;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Framing:", GUILayout.Width(60));
            foreach (var f in PhotoMode.Framings) if (GUILayout.Button(f.Name)) { ph.Height = f.Height; ph.Distance = f.Distance; }
            GUILayout.EndHorizontal();
            ph.Yaw = Slider("Yaw", ph.Yaw, 0f, 360f);
            ph.Pitch = Slider("Pitch", ph.Pitch, -60f, 80f);
            ph.Distance = Slider("Distance", ph.Distance, 0.3f, 12f);
            ph.Height = Slider("Height", ph.Height, 0f, 2.2f);
            ph.Fov = Slider("FOV", ph.Fov, 10f, 90f);
            GUILayout.BeginHorizontal();
            bool li = GUILayout.Toggle(ph.Lights, " Studio lights (key / fill / rim)");
            if (li != ph.Lights) ph.SetLights(li);
            ph.LightsFollowCamera = GUILayout.Toggle(ph.LightsFollowCamera, " follow the camera");
            GUILayout.EndHorizontal();
            _lightStrength.Value = Slider("Light strength", _lightStrength.Value, 0f, 4f);
            if (GUILayout.Button("Turntable: 4 screenshots (front, left, back, right)")) { if (!_capturing) StartCoroutine(Turntable()); }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Pose:", GUILayout.Width(40));
            foreach (var pn in Poses.Names)
            {
                GUI.enabled = _pose != pn;
                if (GUILayout.Button(pn)) SetPose(pn);
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Pose turntables: 4 angles in each pose (clipping check)")) { if (!_capturing && !Wearer.Busy) StartCoroutine(PoseTurntables()); }
            var newest = GetCatalog().ModSets().FirstOrDefault();
            if (newest != null && GUILayout.Button($"A/B: turntable every outfit of [{newest.Source}] + your own outfit"))
                { if (!_capturing && !Wearer.Busy) StartCoroutine(CompareBatch()); }
        }

        static float Slider(string label, float v, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label} {v:0.##}", GUILayout.Width(120));
            v = GUILayout.HorizontalSlider(v, min, max);
            GUILayout.EndHorizontal();
            return v;
        }

        IEnumerator Turntable() { return Turntable(""); }

        IEnumerator Turntable(string tag)
        {
            float yaw = _photo.Yaw;
            foreach (var a in new[] { ("front", 0f), ("left", 90f), ("back", 180f), ("right", 270f) })
            {
                _photo.Yaw = a.Item2;
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
            try
            {
                Refresh(false);
                Directory.CreateDirectory(_outDir);
                png = Path.Combine(_outDir, $"{Stamp()}_{BodyScan.OutfitNames(_scan)}{suffix}.png");
                if (_hideHud.Value && Game.GameWorld() != null)
                    foreach (var c in FindObjectsOfType<Canvas>())
                        if (c != null && c.enabled && c.isRootCanvas) { c.enabled = false; canvases.Add(c); }
            }
            catch (Exception e) { err = "prepare: " + e.Message; Log.LogError("Screenshot prepare failed: " + e); }

            yield return null;                      // one frame without the panel and HUD
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                if (err == null)
                {
                    tex = ScreenCapture.CaptureScreenshotAsTexture(ss);
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
                                        $"fov {_photo.Fov:0.#}, studio lights {(_photo.Lights ? "on x" + _photo.LightStrength.ToString("0.##") : "off")}" +
                                        (_photo.LightsFollowCamera ? " (follow camera)" : " (fixed to character)")
                                      : "Photo mode: off"));
                    _status = $"Saved {Path.GetFileName(png)} ({w}x{h})";
                    Log.LogInfo($"Screenshot saved: {png} ({w}x{h}, supersize {ss}, {canvases.Count} HUD canvases hidden, {_hidden.Count} meshes hidden)");
                }
                catch (Exception e) { _status = "Screenshot info failed: " + e.Message; Log.LogError("Screenshot .txt failed: " + e); }
            }
            else _status = "Screenshot failed: " + err;
        }

        void OnDestroy() { try { ShowAll(); _photo.Exit(); Camera.onPreCull -= OnPreCullCamera; } catch { } }
    }
}
