// Every game-specific lookup goes through here, by reflection and by name, so a renamed class or member
// shows up as a clear log line instead of a crash or a compile error.
// Names used (SPT 4.1.x client, EFT 0.16; evidence in docs/SPT_INSPECTOR.md):
//   EFT.GameWorld via Comfort.Common.Singleton<GameWorld>.Instance   (SPT's own modules do this)
//   GameWorld.MainPlayer -> EFT.Player; Player.Profile.Customization; Player.Location
//   EFT.PlayerBody (component) .BodySkins : dictionary EBodyModelPart -> LoddedSkin; .SlotViews (gear)
//   EFT.UI.PlayerModelView: the menu character preview
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class Game
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        static readonly HashSet<string> Logged = new HashSet<string>();

        public static void LogOnce(string key, string msg, bool warn = true)
        {
            if (!Logged.Add(key)) return;
            if (warn) InspectorPlugin.Log.LogWarning(msg); else InspectorPlugin.Log.LogInfo(msg);
        }

        public static bool Alive(object o)
        {
            var uo = o as UnityEngine.Object;
            return (object)uo != null ? uo != null : o != null;
        }

        public static Type FindType(string fullName)
        {
            if (Types.TryGetValue(fullName, out var t)) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(fullName, false); } catch { t = null; }
                if (t != null) { Types[fullName] = t; return t; }
            }
            LogOnce("type:" + fullName, $"Type not found: {fullName}");
            return null;
        }

        /// <summary>Property or field (any visibility, base classes too). Null when missing or when it throws.</summary>
        public static object Get(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var p = t.GetProperty(name, Any);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(p.GetGetMethod(true).IsStatic ? null : o, null);
                    var f = t.GetField(name, Any);
                    if (f != null) return f.GetValue(f.IsStatic ? null : o);
                }
                catch (AmbiguousMatchException) { }
                catch (Exception e)
                {
                    LogOnce($"get:{t.FullName}.{name}", $"Reading {t.FullName}.{name} failed: {(e.InnerException ?? e).Message}");
                    return null;
                }
            }
            return null;
        }

        public static object GameWorld()
        {
            var gw = FindType("EFT.GameWorld");
            if (gw == null) return null;
            var single = FindType("Comfort.Common.Singleton`1");
            if (single == null) return UnityEngine.Object.FindObjectOfType(gw);
            try
            {
                var inst = single.MakeGenericType(gw).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
                return Alive(inst) ? inst : null;
            }
            catch (Exception e)
            {
                LogOnce("singleton", "Singleton<GameWorld>.Instance failed: " + (e.InnerException ?? e).Message);
                return null;
            }
        }

        public static Component MainPlayer()
        {
            var p = Get(GameWorld(), "MainPlayer") as Component;
            return p != null ? p : null;
        }

        public static string Location(Component player) => Get(player, "Location") as string;

        /// <summary>Profile.Customization of a Player, as "Part = id" lines (empty when not found).</summary>
        public static List<string> Customization(Component player)
        {
            var list = new List<string>();
            var cust = Get(Get(player, "Profile"), "Customization");
            var d = cust as IDictionary;
            var e = cust as IEnumerable;
            if (d != null)
                foreach (DictionaryEntry de in d) list.Add($"{de.Key} = {de.Value}");
            else if (e != null && !(cust is string))
                foreach (var x in e) list.Add(x?.ToString());
            else if (player != null)
                LogOnce("cust", $"Profile.Customization not readable on {player.GetType().FullName} (got {cust?.GetType().FullName ?? "null"})");
            return list;
        }

        /// <summary>Names of a type's fields, properties and methods (one-time diagnostics for later stages).</summary>
        public static string Members(Type t, Func<MemberInfo, bool> filter = null)
        {
            if (t == null) return "(type not found)";
            var sb = new StringBuilder();
            foreach (var m in t.GetMembers(Any).Where(m => filter == null || filter(m)).OrderBy(m => m.MemberType).ThenBy(m => m.Name))
            {
                var f = m as FieldInfo;
                var p = m as PropertyInfo;
                var mi = m as MethodInfo;
                if (f != null) sb.Append($"\n    field  {f.FieldType.Name} {f.Name}");
                else if (p != null) sb.Append($"\n    prop   {p.PropertyType.Name} {p.Name}");
                else if (mi != null && !mi.IsSpecialName)
                    sb.Append($"\n    method {mi.ReturnType.Name} {mi.Name}(" +
                              string.Join(", ", mi.GetParameters().Select(pa => pa.ParameterType.Name + " " + pa.Name)) + ")");
            }
            return sb.ToString();
        }

        public static void LogStartup()
        {
            var log = InspectorPlugin.Log;
            log.LogInfo($"Game {Application.version}, Unity {Application.unityVersion}, game folder {BepInEx.Paths.GameRootPath}");
            foreach (var n in new[] { "EFT.GameWorld", "Comfort.Common.Singleton`1", "EFT.Player", "EFT.PlayerBody",
                                      "EFT.UI.PlayerModelView", "EFT.UI.TacticalClothingView", "EFT.Visual.LoddedSkin" })
                log.LogInfo($"  type {n}: {(FindType(n) != null ? "found" : "NOT FOUND")}");
        }

        /// <summary>One-time dump of the classes stage 2 (outfit browser) will need. Goes to the BepInEx log.</summary>
        public static void LogResearchOnce()
        {
            if (!Logged.Add("research")) return;
            var log = InspectorPlugin.Log;
            log.LogInfo("Research dump (for the outfit browser, stage 2):");
            log.LogInfo("  EFT.PlayerBody members:" + Members(FindType("EFT.PlayerBody"), m => !(m is MethodInfo) || m.DeclaringType?.Namespace != "UnityEngine"));
            log.LogInfo("  EFT.UI.PlayerModelView methods:" + Members(FindType("EFT.UI.PlayerModelView"), m => m is MethodInfo));
            log.LogInfo("  EFT.UI.TacticalClothingView methods:" + Members(FindType("EFT.UI.TacticalClothingView"), m => m is MethodInfo));
        }
    }
}
