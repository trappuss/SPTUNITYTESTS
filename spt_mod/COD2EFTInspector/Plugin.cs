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
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        ConfigEntry<KeyboardShortcut> _panelKey, _shotKey;
        ConfigEntry<int> _supersize;
        ConfigEntry<bool> _hideHud, _others, _unlockCursor;
        ConfigEntry<float> _scale;

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

        void Awake()
        {
            Log = Logger;
            _panelKey = Config.Bind("1. Hotkeys", "Open panel", new KeyboardShortcut(KeyCode.F9), "Shows / hides the inspector panel.");
            _shotKey = Config.Bind("1. Hotkeys", "Screenshot", new KeyboardShortcut(KeyCode.F10), "Saves a screenshot (panel and HUD hidden).");
            _supersize = Config.Bind("2. Screenshot", "Supersize", 2,
                new ConfigDescription("Resolution multiplier (1 = screen size).", new AcceptableValueRange<int>(1, 4)));
            _hideHud = Config.Bind("2. Screenshot", "Hide HUD", true,
                "Hide the game's UI canvases during the capture (raid / hideout only; in the menu the character preview is itself UI).");
            _others = Config.Bind("3. Panel", "Include other players", false, "Also list bots / other players in raid.");
            _unlockCursor = Config.Bind("3. Panel", "Free the mouse", true, "Unlock the mouse cursor while the panel is open (raid / hideout).");
            _scale = Config.Bind("3. Panel", "Scale", 1f, new ConfigDescription("Panel size.", new AcceptableValueRange<float>(0.75f, 2.5f)));
            _outDir = Path.Combine(Paths.GameRootPath, "COD2EFT_Screenshots");
            Log.LogInfo($"{PluginName} v{Version} loaded. Panel {_panelKey.Value}, screenshot {_shotKey.Value}, output {_outDir}");
            try { Game.LogStartup(); } catch (Exception e) { Log.LogError("Startup check failed: " + e); }
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
                    if (_open) Refresh(false);
                }
                if (_open && _unlockCursor.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
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
            else if (_unlockCursor.Value) { Cursor.lockState = _prevLock; Cursor.visible = _prevVisible; }
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
                if (sig != _lastSignature) LogScan();
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

        void DrawContents()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(28)) && _targets.Count > 0) { _ti = (_ti + _targets.Count - 1) % _targets.Count; _scan = Run(_targets[_ti]); LogScan(); }
            GUILayout.Label(_targets.Count > 0 ? $"{_ti + 1}/{_targets.Count}  {_targets[_ti].Label}" : "No character found");
            if (GUILayout.Button(">", GUILayout.Width(28)) && _targets.Count > 0) { _ti = (_ti + 1) % _targets.Count; _scan = Run(_targets[_ti]); LogScan(); }
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
                        bool n2 = GUILayout.Toggle(shown, " " + e.Path + flags);
                        if (n2 != shown) SetHidden(e.R, !n2);
                        GUILayout.EndHorizontal();
                    }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.Label($"Files: {_outDir}");
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
                File.WriteAllText(f, Reports.Materials(_scan));
                _status = "Material report: " + Path.GetFileName(f);
                Log.LogInfo("Material report written: " + f);
            }
            catch (Exception e) { _status = "Material report failed: " + e.Message; Log.LogError("Material report failed: " + e); }
        }

        void TakeScreenshot()
        {
            if (!_capturing) StartCoroutine(Capture());
        }

        IEnumerator Capture()
        {
            _capturing = true;
            var canvases = new List<Canvas>();
            string png = null, err = null;
            int ss = Mathf.Clamp(_supersize.Value, 1, 4), w = 0, h = 0;
            try
            {
                Refresh(false);
                Directory.CreateDirectory(_outDir);
                png = Path.Combine(_outDir, $"{Stamp()}_{BodyScan.OutfitNames(_scan)}.png");
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
                    File.WriteAllText(Path.ChangeExtension(png, ".txt"), Reports.ScreenshotInfo(_scan, png, ss, w, h, _hidden.Keys));
                    _status = $"Saved {Path.GetFileName(png)} ({w}x{h})";
                    Log.LogInfo($"Screenshot saved: {png} ({w}x{h}, supersize {ss}, {canvases.Count} HUD canvases hidden, {_hidden.Count} meshes hidden)");
                }
                catch (Exception e) { _status = "Screenshot info failed: " + e.Message; Log.LogError("Screenshot .txt failed: " + e); }
            }
            else _status = "Screenshot failed: " + err;
        }

        void OnDestroy() { try { ShowAll(); } catch { } }
    }
}
