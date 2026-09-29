// Photo mode (raid / hideout): an orbit camera around your own character, fixed angle presets and studio lights,
// so screenshots of an outfit are taken the same way every time.
// The camera recipe follows the open-source SPT Freecam mod (github.com/acidphantasm/SPT-Freecam, FreecamController.cs):
//   Player.PointOfView = ThirdPerson; PlayerBody.PointOfView.Value = FreeCamera; PlayerCameraController.UpdatePointOfView();
//   GamePlayerOwner.enabled = false (the player takes no input); CameraManager.ForceSetPosition blocked (it snaps the camera back).
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

        Component _player, _pcc;
        Camera _cam;
        Behaviour _owner;
        float _oldFov;
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
                if (!SetEnumProperty(player, "PointOfView", "ThirdPerson")) log.LogWarning("Photo mode: Player.PointOfView not settable");
                var body = Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody");
                if (!SetBindable(Game.Get(body, "PointOfView"), "FreeCamera")) log.LogWarning("Photo mode: PlayerBody.PointOfView.Value not settable");
                var upd = _pcc?.GetType().GetMethod("UpdatePointOfView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (upd != null) upd.Invoke(_pcc, null); else log.LogWarning("Photo mode: PlayerCameraController.UpdatePointOfView() not found");
            }
            catch (Exception e) { log.LogError("Photo mode: switching the view failed: " + e); }

            var ownerT = Game.FindType("EFT.GamePlayerOwner");
            _owner = ownerT != null ? player.GetComponentInChildren(ownerT) as Behaviour : null;
            if (_owner != null) _owner.enabled = false; else log.LogWarning("Photo mode: GamePlayerOwner not found; the character still takes input");

            _oldFov = _cam.fieldOfView;
            _blockForceSet = true;
            Active = true;
            if (Lights) MakeLights();
            log.LogInfo($"Photo mode on: camera '{_cam.name}', player input {(_owner != null ? "off" : "still on")}");
            return null;
        }

        public void Exit()
        {
            if (!Active) return;
            Active = false;
            _blockForceSet = false;
            foreach (var l in _lights) if (l != null) UnityEngine.Object.Destroy(l.gameObject);
            _lights.Clear();
            try
            {
                if (_owner != null) _owner.enabled = true;
                if (_cam != null) _cam.fieldOfView = _oldFov;
                if (Game.Alive(_player) && !SetEnumProperty(_player, "PointOfView", "FirstPerson"))
                    InspectorPlugin.Log.LogWarning("Photo mode: could not switch back to first person");
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

        /// <summary>Right mouse drag orbits, the wheel zooms (only while the pointer is not over the panel).</summary>
        public void HandleMouse(bool overPanel)
        {
            var m = Input.mousePosition;
            if (Input.GetMouseButton(1) && !overPanel)
            {
                var d = m - _lastMouse;
                Yaw = Mathf.Repeat(Yaw + d.x * 0.3f, 360f);
                Pitch = Mathf.Clamp(Pitch - d.y * 0.3f, -60f, 80f);
            }
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f && !overPanel) Distance = Mathf.Clamp(Distance * (1f - wheel * 0.08f), 0.3f, 12f);
            _lastMouse = m;
        }

        public void LateUpdate()
        {
            if (!Active) return;
            if (!Game.Alive(_player) || _cam == null) { InspectorPlugin.Log.LogInfo("Photo mode: player or camera gone"); Exit(); return; }
            var pt = _player.transform;
            float baseYaw = pt.eulerAngles.y;
            var target = pt.position + Vector3.up * Height;
            var camPos = target + Dir(baseYaw + Yaw, Pitch) * Distance;
            _cam.transform.position = camPos;
            _cam.transform.rotation = Quaternion.LookRotation(target - camPos, Vector3.up);
            _cam.fieldOfView = Fov;
            float lightYaw = LightsFollowCamera ? baseYaw + Yaw : baseYaw;
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
