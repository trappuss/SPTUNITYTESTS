// Try-on in the main-menu character preview (Character / Inventory screen), client-side only.
// A Harmony prefix records the arguments of every EFT.UI.PlayerModelView.Show call (the game's own call: profile or
// PlayerVisualRepresentation, inventory controller, position, ...). Try-on = put the ids into that subject's
// Customization, call the same Show again with the same arguments (the game loads the bundles itself), then put the
// real ids back. Same idea as Improved Customization UI (github.com/hjal-dev/Improved-Customization-UI), which dresses
// a preview profile and calls PlayerProfilePreview.Show; signatures from the user's first log:
//   Task Show(Profile, InventoryController, Action onCreated, float update, Vector3? position, bool animateWeapon)
//   Task Show(PlayerVisualRepresentation, InventoryController, Action, float, Vector3?, bool)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class MenuTryOn
    {
        sealed class Call { public Component View; public MethodBase Method; public object[] Args; }
        static readonly List<Call> Calls = new List<Call>();
        public static bool Busy;

        static void Log(string m) => InspectorPlugin.Log.LogInfo("Menu try-on: " + m);
        static void Warn(string m) => InspectorPlugin.Log.LogWarning("Menu try-on: " + m);

        static void ShowPrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                var view = __instance as Component;
                if (view == null || __args == null) return;
                Calls.RemoveAll(c => c.View == null || c.View == view);
                Calls.Add(new Call { View = view, Method = __originalMethod, Args = (object[])__args.Clone() });
                Game.LogOnce("menu-show:" + __originalMethod, $"Menu try-on: recorded {__originalMethod.DeclaringType?.Name}.Show(" +
                    string.Join(", ", __originalMethod.GetParameters().Select(p => p.ParameterType.Name)) + ")", false);
            }
            catch { }
        }

        public static void InstallPatch(string harmonyId)
        {
            var t = Game.FindType("EFT.UI.PlayerModelView");
            if (t == null) { Warn("PlayerModelView not found; menu try-on off"); return; }
            try
            {
                var h = new HarmonyLib.Harmony(harmonyId + ".menu");
                var prefix = new HarmonyLib.HarmonyMethod(typeof(MenuTryOn).GetMethod(nameof(ShowPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                   .Where(m => m.Name == "Show" && m.GetParameters().Length > 0))
                { h.Patch(m, prefix: prefix); n++; }
                Log($"patched {n} PlayerModelView.Show overload(s)");
            }
            catch (Exception e) { Warn("patching PlayerModelView.Show failed: " + e); }
        }

        /// <summary>The preview shown now: the one holding this body if given, else the last one shown that is still visible.</summary>
        static Call Pick(Component body)
        {
            Calls.RemoveAll(c => c.View == null);
            var live = Calls.Where(c => c.View.gameObject.activeInHierarchy).ToList();
            if (body != null)
            {
                var own = live.FirstOrDefault(c => ReferenceEquals(Game.Get(c.View, "PlayerBody"), body) || body.transform.IsChildOf(c.View.transform));
                if (own != null) return own;
            }
            return live.LastOrDefault();
        }

        public static bool Available => Calls.Any(c => c.View != null && c.View.gameObject.activeInHierarchy);

        static string Skins(Component view)
        {
            var body = Game.Get(view, "PlayerBody");
            var skins = Game.Get(body, "BodySkins") as IDictionary;
            if (skins == null) return "";
            var names = new List<string>();
            foreach (DictionaryEntry de in skins) { var c = de.Value as Component; names.Add(de.Key + "=" + (c != null ? c.gameObject.name : "-")); }
            return string.Join(",", names);
        }

        static IEnumerator Invoke(Call c, Action<string> fail)
        {
            Task t = null;
            try { t = c.Method.Invoke(c.View, c.Args) as Task; }
            catch (Exception e) { fail("Show failed: " + (e.InnerException ?? e)); }
            if (t == null) yield break;
            float until = Time.realtimeSinceStartup + 60f;
            while (!t.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!t.IsCompleted) fail("Show did not finish in 60 s");
            else if (t.IsFaulted) fail("Show failed: " + t.Exception?.GetBaseException());
        }

        /// <summary>Shows the preview again with these ids; the real ids go back into the profile afterwards.</summary>
        public static IEnumerator Wear(Component body, IList<Outfit> items, Action<string> done)
        {
            Busy = true;
            string error = null;
            Action<string> fail = m => { if (error == null) { error = m; Warn(m); } };
            var call = Pick(body);
            IDictionary cust = null;
            var old = new Dictionary<object, object>();
            if (call == null) fail("no menu preview recorded yet: open the Character or Inventory screen first");
            else
            {
                cust = Game.Get(call.Args[0], "Customization") as IDictionary;
                if (cust == null) fail($"{call.Args[0]?.GetType().Name}.Customization is not a dictionary");
            }
            string before = call != null ? Skins(call.View) : "";
            if (error == null)
            {
                try
                {
                    foreach (var o in items)
                    {
                        object key = null;
                        foreach (var k in cust.Keys) if (k.ToString() == Wearer.BodyKey(o.Part)) key = k;
                        if (key == null) { fail($"no '{Wearer.BodyKey(o.Part)}' in the preview's Customization"); break; }
                        if (!old.ContainsKey(key)) old[key] = cust[key];
                        cust[key] = Wearer.MakeId(Wearer.ValueType(cust), o.Id);
                    }
                }
                catch (Exception e) { fail("setting the ids failed: " + e); }
            }
            if (error == null) yield return Invoke(call, fail);
            // same outfit still on? the view may skip an identical subject: close it and show again
            if (error == null && Skins(call.View) == before)
            {
                Log("preview unchanged after Show; closing and showing again");
                try { call.View.GetType().GetMethod("Close", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(call.View, null); }
                catch (Exception e) { Warn("Close failed: " + (e.InnerException ?? e).Message); }
                yield return Invoke(call, fail);
            }
            foreach (var kv in old) try { cust[kv.Key] = kv.Value; } catch { }   // the profile keeps its real outfit
            Busy = false;
            string after = call != null ? Skins(call.View) : "";
            if (error == null && after == before) error = "the preview did not change (see the log)";
            string msg = error == null ? "Menu preview wearing: " + string.Join(", ", items.Select(o => $"{o.Part} '{o.Name}'")) + " (not saved)" : "Menu try-on failed: " + error;
            Log(msg + $"  [{before}] -> [{after}]");
            done(msg);
        }

        /// <summary>Shows the preview again with the profile's real outfit.</summary>
        public static IEnumerator Reshow(Action<string> done)
        {
            var call = Pick(null);
            string err = null;
            if (call != null) yield return Invoke(call, m => err = m);
            done(call == null ? "no menu preview" : err ?? "Menu preview shows your real outfit again");
        }
    }
}
