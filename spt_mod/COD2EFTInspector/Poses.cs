// Poses for clipping checks (photo mode, raid / hideout): the game's own animations, driven through the player's
// movement state the way input or bots would (stand / crouch / low crouch / prone / aim). No animation is faked.
// Member names are hunches from common EFT mod usage (MovementContext.SetPoseLevel, IsInPronePose, HandsController
// .IsAiming / SetAim); every one is looked up by name, and the first use logs the members that exist, so a wrong
// guess shows up in the log with the right candidates next to it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace COD2EFTInspector
{
    internal static class Poses
    {
        public static readonly string[] Names = { "Stand", "Crouch", "Low crouch", "Prone", "Aim" };
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static void Warn(string m) => InspectorPlugin.Log.LogWarning("Pose: " + m);

        static void DumpOnce(object o, string label, params string[] words)
        {
            if (o == null) return;
            var names = new List<string>();
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var m in t.GetMembers(Inst | BindingFlags.DeclaredOnly))
                    if (words.Any(w => m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        var mi = m as MethodInfo;
                        names.Add(mi != null ? $"{mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name))})" : m.Name);
                    }
            Game.LogOnce("posedump:" + label, $"Pose: {label} ({o.GetType().FullName}) members: " + string.Join(", ", names.Distinct()), false);
        }

        /// <summary>Calls the first method with one of these names whose parameters fit (missing trailing ones use defaults).</summary>
        static bool Call(object o, string[] names, params object[] args)
        {
            if (o == null) return false;
            foreach (var n in names)
                foreach (var m in o.GetType().GetMethods(Inst).Where(m => m.Name == n))
                {
                    var ps = m.GetParameters();
                    if (ps.Length < args.Length || ps.Skip(args.Length).Any(p => !p.HasDefaultValue)) continue;
                    bool fits = true;
                    for (int i = 0; i < args.Length; i++) if (!ps[i].ParameterType.IsInstanceOfType(args[i])) fits = false;
                    if (!fits) continue;
                    var full = args.Concat(ps.Skip(args.Length).Select(p => p.DefaultValue)).ToArray();
                    try { m.Invoke(o, full); return true; }
                    catch (Exception e) { Warn($"{n} failed: {(e.InnerException ?? e).Message}"); }
                }
            return false;
        }

        static bool SetProp(object o, string name, object value)
        {
            var p = o?.GetType().GetProperty(name, Inst);
            if (p == null || !p.CanWrite) return false;
            try { p.SetValue(o, value, null); return true; } catch (Exception e) { Warn($"{name} = {value} failed: {(e.InnerException ?? e).Message}"); return false; }
        }

        static bool IsProne(object mc)
        {
            var v = Game.Get(mc, "IsInPronePose");
            return v is bool && (bool)v;
        }

        static void SetProne(object player, object mc, bool on)
        {
            if (IsProne(mc) == on) return;
            if (SetProp(mc, "IsInPronePose", on)) return;
            if (Call(player, new[] { "ToggleProne" })) return;
            if (Call(mc, new[] { "SetProneStateForce", "SetProne" }, on)) return;
            Warn("no way to " + (on ? "lie down" : "stand up") + " found (see the member list above)");
        }

        static void SetAim(object player, bool on)
        {
            var hc = Game.Get(player, "HandsController");
            DumpOnce(hc, "HandsController", "Aim");
            if (SetProp(hc, "IsAiming", on)) return;
            if (Call(hc, new[] { "SetAim" }, on)) return;
            if (on) Warn("no way to aim found (needs a weapon in hands; see the member list above)");
        }

        /// <summary>Puts the character in a pose. Returns null or a short problem for the panel.</summary>
        public static string Apply(object player, string pose)
        {
            if (player == null) return "no local player";
            var mc = Game.Get(player, "MovementContext");
            if (mc == null) return "Player.MovementContext not found";
            DumpOnce(mc, "MovementContext", "Pose", "Prone", "Sprint", "Tilt", "Lean");
            SetAim(player, false);
            if (pose != "Prone") SetProne(player, mc, false);
            float level = pose == "Crouch" ? 0.5f : pose == "Low crouch" ? 0f : 1f;
            switch (pose)
            {
                case "Prone": SetProne(player, mc, true); break;
                case "Aim":
                    if (!Call(mc, new[] { "SetPoseLevel" }, 1f)) Warn("SetPoseLevel not found");
                    SetAim(player, true);
                    break;
                default:
                    if (!Call(mc, new[] { "SetPoseLevel" }, level)) return "SetPoseLevel not found (see the log)";
                    break;
            }
            return null;
        }
    }
}
