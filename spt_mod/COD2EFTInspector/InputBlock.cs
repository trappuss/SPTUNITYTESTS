// Blocks the local player's own input (look, aim, fire, move) while the panel is open or photo mode is on, and gives it
// back exactly as it was. Same switch photo mode used since 0.3.0 (SPT Freecam's recipe): EFT.GamePlayerOwner turns
// keyboard / mouse into player commands; disabled, the character takes no input. The panel's own mouse (IMGUI) and the
// plugin's hotkeys are read by the plugin itself, so they keep working.
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class InputBlock
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Behaviour _owner;
        static Component _player;
        static bool _wasEnabled;

        public static bool Blocked => _owner != null;

        /// <summary>Call every frame with the wanted state; cheap when nothing changes.</summary>
        public static void Set(Component player, bool block)
        {
            if (_owner != null && (!block || !Game.Alive(_player) || player != _player)) Release();
            if (!block || player == null) return;
            if (_owner != null) { if (_owner.enabled) _owner.enabled = false; return; }   // the game switched it back on
            var ownerT = Game.FindType("EFT.GamePlayerOwner");
            var owner = ownerT != null ? player.GetComponentInChildren(ownerT, true) as Behaviour : null;
            if (owner == null) { Game.LogOnce("noowner", "Input: EFT.GamePlayerOwner not found; the character still takes input while the panel is open"); return; }
            _owner = owner; _player = player; _wasEnabled = owner.enabled;
            owner.enabled = false;
            StopActions(player);
            InspectorPlugin.Log.LogInfo($"Input: player input off (GamePlayerOwner was {(_wasEnabled ? "on" : "off")})");
        }

        public static void Release()
        {
            if (_owner == null) return;
            try { if (_owner != null) _owner.enabled = _wasEnabled; } catch { }
            InspectorPlugin.Log.LogInfo("Input: player input back " + (_wasEnabled ? "on" : "off"));
            _owner = null; _player = null;
        }

        /// <summary>No input also means no "key released": stop walking and release the trigger once (names are hunches, logged if missing).</summary>
        static void StopActions(Component player)
        {
            try
            {
                var move = player.GetType().GetMethods(Inst).FirstOrDefault(m => m.Name == "Move" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Vector2));
                if (move != null) move.Invoke(player, new object[] { Vector2.zero });
                else Game.LogOnce("nomove", "Input: Player.Move(Vector2) not found (a held movement key may keep the character walking)", false);
                var hc = Game.Get(player, "HandsController");
                var trig = hc?.GetType().GetMethods(Inst).FirstOrDefault(m => m.Name == "SetTriggerPressed" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
                if (trig != null) trig.Invoke(hc, new object[] { false });
            }
            catch (Exception e) { Game.LogOnce("stopactions", "Input: stopping movement / trigger failed: " + (e.InnerException ?? e).Message); }
        }
    }
}
