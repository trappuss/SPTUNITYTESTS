// Photo mode (raid / hideout): an orbit camera around your own character, fixed angle presets and studio lights,
// so screenshots of an outfit are taken the same way every time.
// The camera recipe follows the open-source SPT Freecam mod (github.com/acidphantasm/SPT-Freecam, FreecamController.cs):
//   Player.PointOfView = ThirdPerson; PlayerBody.PointOfView.Value = FreeCamera; PlayerCameraController.UpdatePointOfView();
//   GamePlayerOwner.enabled = false (the player takes no input); CameraManager.ForceSetPosition blocked (it snaps the camera back).
// Safeguards from CineKit (github.com/Hysocs/cinekit-spt, Client/Plugin.cs ShowLocalThirdPersonBody): the camera's
// culling mask gets the layers of the visible body renderers, shadows-only body renderers are drawn, the previous
// point of view is restored on exit.
// 0.7.0: background (isolate the character: every other renderer / terrain gets forceRenderingOff, the camera clears to
// a solid colour; fog / sky / post effects that would tint it can be switched off), character yaw / aim pitch, resets.
// Everything changed is recorded with its old value and put back on Exit / reset.
// All game types are reached by name (Game.cs) and every step logs what it could not find.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace COD2EFTInspector
{
    internal sealed class PhotoMode
    {
        public bool Active { get; private set; }
        public float Yaw, Pitch = 8f, Distance = 3.6f, Height = 1.0f, Fov = 35f;   // yaw 0 = in front of the character
        public float LightStrength = 1f;
        public bool Lights = true, LightsFollowCamera = true;
        // character: yaw relative to its facing when photo mode started, aim pitch (0 = level; sign as the game's Rotation.y)
        public float CharYaw, CharPitch;
        public bool InvertAimDrag;
        // background: Isolate = only the character (and what it wears / holds), on a solid colour
        public bool Isolate, NoFog = true, NoPost, WorldLightsOff;
        public bool Ortho;
        // play mode (0.9.0): the player controls the character (walk, shoot, reload, inspect) while the camera keeps
        // orbiting it; the camera follows the character's facing, and the plugin gives input back (InputBlock)
        public bool Play;
        public bool DoubleClicked;   // set by HandleMouse, read and cleared by the plugin
        float _lastClick = -1f;   // orthographic camera, size = distance * tan(fov / 2) so the framing matches
        bool _oldOrtho;
        float _oldOrthoSize;
        public Color BgColor = DefaultBg;
        public Color? BgOverride;   // transparent capture: black / white passes
        public static readonly Color DefaultBg = new Color(0f, 177f / 255f, 64f / 255f);   // chroma green
        public const float DefYaw = 0f, DefPitch = 8f, DefDistance = 3.6f, DefHeight = 1.0f, DefFov = 35f;

        Component _player, _pcc;
        Camera _cam;
        float _oldFov;
        float _baseYaw;
        Vector2? _startRot;
        string _rotWay;
        CameraClearFlags _oldClear;
        Color _oldBg;
        bool _oldFog, _isoOn;
        float _nextIso;
        UnityEngine.Rendering.AmbientMode _oldAmbMode;
        Color _oldAmb;
        float _oldAmbInt;
        readonly Dictionary<Renderer, bool> _isoHidden = new Dictionary<Renderer, bool>();
        // terrains by reflection (UnityEngine.TerrainModule isn't referenced): terrain -> old drawHeightmap / drawTreesAndFoliage
        readonly Dictionary<Component, KeyValuePair<bool, bool>> _isoTerrain = new Dictionary<Component, KeyValuePair<bool, bool>>();

        static void TerrainDraw(Component t, bool heightmap, bool trees)
        {
            t.GetType().GetProperty("drawHeightmap")?.SetValue(t, heightmap, null);
            t.GetType().GetProperty("drawTreesAndFoliage")?.SetValue(t, trees, null);
        }

        static IEnumerable<Component> Terrains()
        {
            var tt = Game.FindType("UnityEngine.Terrain");
            var all = tt?.GetProperty("activeTerrains", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null) as Array;
            if (all == null) yield break;
            foreach (var o in all) { var c = o as Component; if (c != null) yield return c; }
        }
        readonly Dictionary<Behaviour, bool> _isoFx = new Dictionary<Behaviour, bool>();
        readonly Dictionary<Light, bool> _isoLights = new Dictionary<Light, bool>();
        int _oldMask;
        object _oldPov;
        Component _body;
        float _nextBodyFix;
        readonly Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode> _shadowFix = new Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode>();
        readonly List<Light> _lights = new List<Light>();
        static bool _blockForceSet;

        public static readonly (string Name, float Yaw)[] Angles = { ("Front", 0f), ("3/4", 35f), ("Left", 90f), ("Back", 180f), ("Right", 270f) };
        public static readonly (string Name, float Height, float Distance)[] Framings = { ("Full body", 1.0f, 3.6f), ("Upper body", 1.35f, 1.8f), ("Head", 1.62f, 0.75f) };

        /// <summary>Harmony prefix on CameraManager.ForceSetPosition: skip it while photo mode is on.</summary>
        static bool ForceSetPrefix() => !_blockForceSet;

        public static void InstallPatch(string harmonyId)
        {
            try
            {
                MethodInfo target = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Assembly-CSharp"))
                {
                    Type[] types;
                    try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    foreach (var t in types.Where(t => t.Name == "CameraManager"))
                        target = target ?? t.GetMethod("ForceSetPosition", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (target == null) { InspectorPlugin.Log.LogWarning("Photo mode: CameraManager.ForceSetPosition not found; the game may snap the camera back to the player."); return; }
                new HarmonyLib.Harmony(harmonyId).Patch(target, prefix: new HarmonyLib.HarmonyMethod(typeof(PhotoMode).GetMethod(nameof(ForceSetPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                InspectorPlugin.Log.LogInfo($"Photo mode: patched {target.DeclaringType.FullName}.{target.Name}");
            }
            catch (Exception e) { InspectorPlugin.Log.LogError("Photo mode: patching ForceSetPosition failed: " + e); }
        }

        static bool SetEnumProperty(object o, string prop, string value)
        {
            var p = o?.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || !p.CanWrite || !p.PropertyType.IsEnum) return false;
            p.SetValue(o, Enum.Parse(p.PropertyType, value), null);
            return true;
        }

        /// <summary>Sets BindableState&lt;EPointOfView&gt;.Value without running the player's own view switch.</summary>
        static bool SetBindable(object state, string value)
        {
            var p = state?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || !p.CanWrite || !p.PropertyType.IsEnum) return false;
            p.SetValue(state, Enum.Parse(p.PropertyType, value), null);
            return true;
        }

        public string Enter(Component player)
        {
            if (Active) return null;
            if (player == null) return "No local player (photo mode works in raid and the hideout; in the menu the preview already shows the character).";
            var log = InspectorPlugin.Log;
            _player = player;
            var pccT = Game.FindType("EFT.CameraControl.PlayerCameraController");
            _pcc = pccT != null ? player.GetComponent(pccT) : null;
            _cam = Game.Get(_pcc, "Camera") as Camera;
            if (_cam == null) { _cam = Camera.main; log.LogWarning($"Photo mode: PlayerCameraController.Camera not found ({(pccT == null ? "type missing" : _pcc == null ? "component missing" : "no Camera member")}); using Camera.main"); }
            if (_cam == null) return "No camera found.";

            try
            {
                _oldPov = Game.Get(player, "PointOfView");
                if (!SetEnumProperty(player, "PointOfView", "ThirdPerson")) log.LogWarning("Photo mode: Player.PointOfView not settable");
                var body = Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody");
                _body = body as Component;
                if (!SetBindable(Game.Get(body, "PointOfView"), "FreeCamera")) log.LogWarning("Photo mode: PlayerBody.PointOfView.Value not settable");
                var upd = _pcc?.GetType().GetMethod("UpdatePointOfView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (upd != null) upd.Invoke(_pcc, null); else log.LogWarning("Photo mode: PlayerCameraController.UpdatePointOfView() not found");
            }
            catch (Exception e) { log.LogError("Photo mode: switching the view failed: " + e); }

            // (player input is switched off by the plugin through InputBlock while photo mode is on)
            _oldFov = _cam.fieldOfView;
            _oldMask = _cam.cullingMask;
            _oldClear = _cam.clearFlags;
            _oldOrtho = _cam.orthographic;
            _oldOrthoSize = _cam.orthographicSize;
            _oldBg = _cam.backgroundColor;
            _baseYaw = player.transform.eulerAngles.y;
            _startRot = ReadRot();
            CharYaw = 0f; CharPitch = 0f;   // aim level, facing as now (0.6.0 kept the last look direction)
            _nextBodyFix = 0f;
            _nextIso = 0f;
            _blockForceSet = true;
            Active = true;
            if (Lights) MakeLights();
            log.LogInfo($"Photo mode on: camera '{_cam.name}', character rotation {(_startRot.HasValue ? _startRot.Value.ToString() : "not readable")}");
            LogCameraOnce();
            return null;
        }

        void LogCameraOnce()
        {
            if (_cam == null) return;
            Game.LogOnce("camcomps", "Photo mode: camera components (for the background options): " + string.Join(", ",
                _cam.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name + (c is Behaviour ? (((Behaviour)c).enabled ? "" : " (off)") : "") +
                    (IsImageEffect(c) ? " [image effect]" : ""))), false);
        }

        // ------------------------------------------------------------------ character yaw / aim pitch

        Vector2? ReadRot()
        {
            var v = Game.Get(_player, "Rotation");
            if (v is Vector2) return (Vector2)v;
            v = Game.Get(Game.Get(_player, "MovementContext"), "Rotation");
            if (v is Vector2) return (Vector2)v;
            Game.LogOnce("norot", "Photo mode: Player.Rotation / MovementContext.Rotation not readable; character yaw / aim sliders do nothing");
            return null;
        }

        /// <summary>Turns the character to this look rotation: Player.Rotate(delta) (the path mouse input takes), else a writable Rotation.</summary>
        void WriteRot(Vector2 cur, Vector2 want)
        {
            var delta = new Vector2(Mathf.DeltaAngle(cur.x, want.x), want.y - cur.y);
            if (delta.sqrMagnitude < 1e-4f) return;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var rot = _player.GetType().GetMethods(inst).FirstOrDefault(m => m.Name == "Rotate" && m.GetParameters().Length >= 1 &&
                    m.GetParameters()[0].ParameterType == typeof(Vector2) && m.GetParameters().Skip(1).All(p => p.HasDefaultValue || p.ParameterType == typeof(bool)));
                if (rot != null)
                {
                    var args = new object[rot.GetParameters().Length];
                    args[0] = delta;
                    for (int i = 1; i < args.Length; i++) { var pi = rot.GetParameters()[i]; args[i] = pi.HasDefaultValue ? pi.DefaultValue : (object)false; }
                    rot.Invoke(_player, args);
                    Way("Player.Rotate(Vector2" + (args.Length > 1 ? ", ..." : "") + ")");
                    return;
                }
                foreach (var o in new[] { (object)_player, Game.Get(_player, "MovementContext") })
                {
                    var p = o?.GetType().GetProperty("Rotation", inst);
                    if (p != null && p.CanWrite && p.PropertyType == typeof(Vector2)) { p.SetValue(o, want, null); Way(o.GetType().Name + ".Rotation ="); return; }
                }
                Game.LogOnce("norotw", "Photo mode: no Player.Rotate(Vector2) and no writable Rotation; character yaw / aim sliders do nothing");
            }
            catch (Exception e) { Game.LogOnce("rotfail", "Photo mode: turning the character failed: " + (e.InnerException ?? e)); }
        }

        void Way(string w) { if (_rotWay == w) return; _rotWay = w; InspectorPlugin.Log.LogInfo("Photo mode: character turned with " + w); }

        /// <summary>Every frame while photo mode is on: keeps the character at CharYaw / CharPitch.</summary>
        public void Update()
        {
            if (!Active || Play || !Game.Alive(_player) || !_startRot.HasValue) return;
            var cur = ReadRot();
            if (cur.HasValue) WriteRot(cur.Value, new Vector2(_startRot.Value.x + CharYaw, CharPitch));
        }

        // ------------------------------------------------------------------ background

        static readonly string[] TintFx = { "fog", "scatter", "sky", "tod_", "haze", "atmos", "cloud", "volumetric", "sunshaft", "godray" };
        static readonly string[] KeepFx = { "ssaa", "upscal", "dlss", "fsr", "xess", "optic", "listener", "cameracontroller" };

        static bool IsImageEffect(Component c) =>
            c is MonoBehaviour && c.GetType().GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;

        bool ShouldDisable(Behaviour b)
        {
            if (b == null || b is Camera) return false;
            string n = b.GetType().Name.ToLowerInvariant();
            if (KeepFx.Any(n.Contains)) return false;
            if (NoFog && TintFx.Any(n.Contains)) return true;
            return NoPost && (IsImageEffect(b) || n.Contains("postprocess") || n.Contains("prism") || n.StartsWith("cc_"));
        }

        /// <summary>Isolation on/off and its settings, applied from LateUpdate; everything touched is restored by IsolateOff.</summary>
        void IsolateTick()
        {
            if (!Isolate) { if (_isoOn) IsolateOff(); return; }
            if (!_isoOn)
            {
                _isoOn = true;
                _oldFog = RenderSettings.fog;
                _oldAmbMode = RenderSettings.ambientMode; _oldAmb = RenderSettings.ambientLight; _oldAmbInt = RenderSettings.ambientIntensity;
                _nextIso = 0f;
            }
            _cam.clearFlags = CameraClearFlags.SolidColor;
            var bg = BgOverride ?? BgColor;
            _cam.backgroundColor = new Color(bg.r, bg.g, bg.b, BgOverride.HasValue ? 0f : 1f);
            if (NoFog) RenderSettings.fog = false; else RenderSettings.fog = _oldFog;
            if (Time.unscaledTime < _nextIso) return;
            _nextIso = Time.unscaledTime + 2f;   // new objects (bots, effects) are caught within 2 s

            var root = _player.transform;
            int hidden = 0;
            // the character's layers (body, gear, weapon): anything on them near the character is kept, because held / slung
            // items are not always children of the player object (0.8.0 hid the gun)
            int charLayers = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r != null) charLayers |= 1 << r.gameObject.layer;
            foreach (var n in new[] { "Player", "Weapon", "PlayerSpiritAura" }) { int l = LayerMask.NameToLayer(n); if (l >= 0) charLayers |= 1 << l; }
            var centre = root.position + Vector3.up;
            var near = new List<string>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || _isoHidden.ContainsKey(r) || r.transform.IsChildOf(root) || r.GetComponent<Light>() != null) continue;
                bool close = (r.bounds.center - centre).sqrMagnitude < 2.5f * 2.5f;
                if (close && (charLayers & (1 << r.gameObject.layer)) != 0) continue;
                if (close && near.Count < 25) near.Add($"{BodyScan.PathOf(r.transform)} [layer {LayerMask.LayerToName(r.gameObject.layer)}]");
                _isoHidden[r] = r.forceRenderingOff;
                r.forceRenderingOff = true;
                hidden++;
            }
            foreach (var kv in _isoHidden) if (kv.Key != null && !kv.Key.forceRenderingOff) kv.Key.forceRenderingOff = true;   // the game switched one back
            try
            {
                foreach (var t in Terrains())
                {
                    if (!_isoTerrain.ContainsKey(t))
                        _isoTerrain[t] = new KeyValuePair<bool, bool>(Game.Get(t, "drawHeightmap") as bool? ?? true, Game.Get(t, "drawTreesAndFoliage") as bool? ?? true);
                    TerrainDraw(t, false, false);
                }
            }
            catch (Exception e) { Game.LogOnce("terrain", "Photo mode: hiding terrain failed: " + (e.InnerException ?? e).Message); }
            // camera effects: off when they tint the background, back on when the option is cleared
            foreach (var b in _cam.GetComponents<Behaviour>())
            {
                bool off = ShouldDisable(b);
                if (off && !_isoFx.ContainsKey(b)) { _isoFx[b] = b.enabled; b.enabled = false; }
                else if (!off && _isoFx.ContainsKey(b)) { b.enabled = _isoFx[b]; _isoFx.Remove(b); }
                else if (off && b.enabled) b.enabled = false;
            }
            // world lights (studio lights only)
            if (WorldLightsOff)
            {
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l != null && l.enabled && !_lights.Contains(l) && !_isoLights.ContainsKey(l)) { _isoLights[l] = true; l.enabled = false; }
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.12f, 0.12f, 0.12f);
            }
            else if (_isoLights.Count > 0 || RenderSettings.ambientMode != _oldAmbMode)
            {
                foreach (var kv in _isoLights) if (kv.Key != null) kv.Key.enabled = kv.Value;
                _isoLights.Clear();
                RenderSettings.ambientMode = _oldAmbMode; RenderSettings.ambientLight = _oldAmb; RenderSettings.ambientIntensity = _oldAmbInt;
            }
            if (near.Count > 0)
                InspectorPlugin.Log.LogInfo("Photo mode: isolate hid these objects within 2.5 m of the character (if one is your gear, tell Claude): " + string.Join("; ", near));
            if (hidden > 0)
                InspectorPlugin.Log.LogInfo($"Photo mode: isolate: {hidden} more renderer(s) hidden ({_isoHidden.Count} in all), {_isoTerrain.Count} terrain(s), " +
                    $"camera effects off: {(_isoFx.Count == 0 ? "none" : string.Join(", ", _isoFx.Keys.Where(b => b != null).Select(b => b.GetType().Name)))}" +
                    (WorldLightsOff ? $", {_isoLights.Count} world light(s) off" : ""));
        }

        void IsolateOff()
        {
            if (!_isoOn) return;
            _isoOn = false;
            int n = 0;
            foreach (var kv in _isoHidden) if (kv.Key != null) { kv.Key.forceRenderingOff = kv.Value; n++; }
            _isoHidden.Clear();
            foreach (var kv in _isoTerrain) if (kv.Key != null) try { TerrainDraw(kv.Key, kv.Value.Key, kv.Value.Value); } catch { }
            _isoTerrain.Clear();
            foreach (var kv in _isoFx) if (kv.Key != null) kv.Key.enabled = kv.Value;
            _isoFx.Clear();
            foreach (var kv in _isoLights) if (kv.Key != null) kv.Key.enabled = kv.Value;
            _isoLights.Clear();
            RenderSettings.fog = _oldFog;
            RenderSettings.ambientMode = _oldAmbMode; RenderSettings.ambientLight = _oldAmb; RenderSettings.ambientIntensity = _oldAmbInt;
            if (_cam != null) { _cam.clearFlags = _oldClear; _cam.backgroundColor = _oldBg; }
            InspectorPlugin.Log.LogInfo($"Photo mode: isolate off, {n} renderer(s) and the camera / fog / lights restored");
        }

        public bool Isolated => Active && _isoOn;

        /// <summary>Leaving play mode: the character's current facing / aim become the new reference (no snap back).</summary>
        public void Rebase()
        {
            if (!Active || !Game.Alive(_player)) return;
            _baseYaw = _player.transform.eulerAngles.y;
            _startRot = ReadRot();
            CharYaw = 0f;
            CharPitch = _startRot.HasValue ? Mathf.Clamp(_startRot.Value.y, -60f, 60f) : 0f;
        }

        // ------------------------------------------------------------------ resets

        public void ResetCamera() { Ortho = false; Yaw = CharYaw + DefYaw; Pitch = DefPitch; Distance = DefDistance; Height = DefHeight; Fov = DefFov; }
        public void ResetCharacter() { CharYaw = 0f; CharPitch = 0f; }
        public void ResetLights() { SetLights(true); LightsFollowCamera = true; }   // strength: the plugin's config value
        public void ResetBackground() { Isolate = false; NoFog = true; NoPost = false; WorldLightsOff = false; BgColor = DefaultBg; BgOverride = null; }

        /// <summary>Camera yaw that looks at the character from this angle (0 = front), whatever way it's turned.</summary>
        public float YawFor(float angle) => Mathf.Repeat(CharYaw + angle, 360f);

        public void Exit()
        {
            if (!Active) return;
            try { IsolateOff(); } catch (Exception e) { InspectorPlugin.Log.LogError("Photo mode: restoring the background failed: " + e); }
            try { if (!Play && _startRot.HasValue && Game.Alive(_player)) { var cur = ReadRot(); if (cur.HasValue) WriteRot(cur.Value, _startRot.Value); } }
            catch (Exception e) { InspectorPlugin.Log.LogError("Photo mode: turning the character back failed: " + e); }
            Active = false;
            Play = false;
            _blockForceSet = false;
            BgOverride = null;
            foreach (var l in _lights) if (l != null) UnityEngine.Object.Destroy(l.gameObject);
            _lights.Clear();
            try
            {
                if (_cam != null) { _cam.fieldOfView = _oldFov; _cam.cullingMask = _oldMask; _cam.clearFlags = _oldClear; _cam.backgroundColor = _oldBg;
                                  _cam.orthographic = _oldOrtho; _cam.orthographicSize = _oldOrthoSize; }
                foreach (var kv in _shadowFix) if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
                _shadowFix.Clear();
                string back = _oldPov != null && _oldPov.ToString() != "FreeCamera" ? _oldPov.ToString() : "FirstPerson";
                if (Game.Alive(_player) && !SetEnumProperty(_player, "PointOfView", back))
                    InspectorPlugin.Log.LogWarning("Photo mode: could not switch back to " + back);
            }
            catch (Exception e) { InspectorPlugin.Log.LogError("Photo mode: leaving failed: " + e); }
            InspectorPlugin.Log.LogInfo("Photo mode off");
        }

        public void SetLights(bool on)
        {
            Lights = on;
            if (!Active) return;
            if (on && _lights.Count == 0) MakeLights();
            if (!on) { foreach (var l in _lights) if (l != null) UnityEngine.Object.Destroy(l.gameObject); _lights.Clear(); }
        }

        // key / fill / rim, placed relative to the camera (or to the character's front when not following the camera)
        static readonly (string Name, float Yaw, float Pitch, float Intensity, bool Shadows)[] Rig =
            { ("Key", -45f, 35f, 1.6f, true), ("Fill", 55f, 10f, 0.6f, false), ("Rim", 180f, 40f, 1.1f, false) };

        void MakeLights()
        {
            foreach (var r in Rig)
            {
                var go = new GameObject("COD2EFT_StudioLight_" + r.Name);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot;
                l.spotAngle = 55f;
                l.range = 15f;
                l.color = Color.white;
                l.shadows = r.Shadows ? LightShadows.Soft : LightShadows.None;
                _lights.Add(l);
            }
        }

        static Vector3 Dir(float yaw, float pitch) => Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(-pitch, 0f, 0f) * Vector3.forward;

        Vector3 _lastMouse;
        readonly bool[] _dragOk = new bool[2];

        /// <summary>Right drag orbits the camera, left drag turns the character (yaw) and its aim (pitch), the wheel zooms.
        /// A drag counts only when it started outside the panel.</summary>
        public void HandleMouse(bool overPanel)
        {
            var m = Input.mousePosition;
            for (int b = 0; b < 2; b++) if (Input.GetMouseButtonDown(b)) _dragOk[b] = !overPanel;
            if (Input.GetMouseButtonDown(0) && !overPanel)
            {
                if (Time.unscaledTime - _lastClick < 0.3f) { DoubleClicked = true; _lastClick = -1f; }
                else _lastClick = Time.unscaledTime;
            }
            var d = m - _lastMouse;
            if (Input.GetMouseButton(1) && _dragOk[1])
            {
                Yaw = Mathf.Repeat(Yaw + d.x * 0.3f, 360f);
                Pitch = Mathf.Clamp(Pitch - d.y * 0.3f, -60f, 80f);
            }
            if (Input.GetMouseButton(0) && _dragOk[0])
            {
                CharYaw = Mathf.Repeat(CharYaw + d.x * 0.3f + 180f, 360f) - 180f;
                CharPitch = Mathf.Clamp(CharPitch + (InvertAimDrag ? d.y : -d.y) * 0.2f, -60f, 60f);
            }
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f && !overPanel) Distance = Mathf.Clamp(Distance * (1f - wheel * 0.08f), 0.3f, 12f);
            _lastMouse = m;
        }

        /// <summary>Makes sure the enabled body renderers are drawn by this camera and cast normal shadows.</summary>
        void FixBody()
        {
            var root = _body != null ? _body : _player;
            if (root == null) return;
            int added = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled) continue;
                int bit = 1 << r.gameObject.layer;
                if ((_cam.cullingMask & bit) == 0) { _cam.cullingMask |= bit; added++; }
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && !_shadowFix.ContainsKey(r))
                {
                    _shadowFix[r] = r.shadowCastingMode;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
            }
            if (added > 0) Game.LogOnce("photo-mask", $"Photo mode: added {added} body layer(s) to the camera's culling mask", false);
            if (_shadowFix.Count > 0) Game.LogOnce("photo-shadow", $"Photo mode: {_shadowFix.Count} shadows-only body renderer(s) made visible", false);
        }

        public void LateUpdate()
        {
            if (!Active) return;
            if (!Game.Alive(_player) || _cam == null) { InspectorPlugin.Log.LogInfo("Photo mode: player or camera gone"); Exit(); return; }
            var pt = _player.transform;
            // world facing at photo-mode start: turning the character doesn't turn the camera; in play mode the camera follows
            float baseYaw = Play ? pt.eulerAngles.y : _baseYaw;
            var target = pt.position + Vector3.up * Height;
            var camPos = target + Dir(baseYaw + Yaw, Pitch) * Distance;
            _cam.transform.position = camPos;
            _cam.transform.rotation = Quaternion.LookRotation(target - camPos, Vector3.up);
            _cam.fieldOfView = Fov;
            _cam.orthographic = Ortho;
            if (Ortho) _cam.orthographicSize = Mathf.Max(0.05f, Distance * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad));
            if (Time.unscaledTime >= _nextBodyFix) { _nextBodyFix = Time.unscaledTime + 0.5f; FixBody(); }
            IsolateTick();
            float lightYaw = LightsFollowCamera ? baseYaw + Yaw : baseYaw + CharYaw;
            for (int i = 0; i < _lights.Count && i < Rig.Length; i++)
            {
                var l = _lights[i];
                if (l == null) continue;
                var p = target + Dir(lightYaw + Rig[i].Yaw, Rig[i].Pitch) * 3f;
                l.transform.position = p;
                l.transform.rotation = Quaternion.LookRotation(target - p, Vector3.up);
                l.intensity = Rig[i].Intensity * LightStrength;
            }
        }
    }
}
