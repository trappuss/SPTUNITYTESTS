// Live try-on (raid / hideout): puts any top / pants / head / hands from the catalog on your own character, client-side
// only. Nothing is sent to the server or saved: the next raid / hideout load shows your real outfit again.
// Recipe from SkinService (github.com/kmyuhkyuk/SkinService, Views/SkinServiceView.cs InitSkin, for SPT 3.10):
//   Profile.Customization[part] = id; LoadBundlesAndCreatePools(ResourceKey[] of the prefabs);
//   PlayerBody.Init(customization, equipment, itemInHands, layer, side, profileId, alternativeBones, isYourPlayer);
//   PlayerBody.UpdatePlayerRenders(pointOfView, side).
// SPT 4.1.6 signatures from the user's first log (PlayerBody members dump). The bundle loader is found by method name
// and its arguments are chosen by type; its full signature is logged on first use.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class Wearer
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static bool Busy;
        static Dictionary<string, string> _original;   // part -> id before the first try-on (for "Restore")

        static void Log(string m) => InspectorPlugin.Log.LogInfo("Wear: " + m);
        static void Warn(string m) => InspectorPlugin.Log.LogWarning("Wear: " + m);

        public static string BodyKey(string part) =>
            part == "Top" ? "Body" : part == "Pants" ? "Feet" : part;   // catalog part -> EBodyModelPart name

        static object FindKey(IDictionary d, string name)
        {
            foreach (var k in d.Keys) if (k != null && k.ToString() == name) return k;
            return null;
        }

        /// <summary>Converts an id string to the dictionary's value type (MongoID in EFT: constructor(string)).</summary>
        static object MakeId(Type t, string id)
        {
            if (t == null || t == typeof(string) || t == typeof(object)) return id;
            var c = t.GetConstructor(new[] { typeof(string) });
            if (c != null) return c.Invoke(new object[] { id });
            var parse = t.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (parse != null) return parse.Invoke(null, new object[] { id });
            throw new InvalidOperationException("can't make a " + t.FullName + " from a string");
        }

        static Type ValueType(IDictionary d)
        {
            var t = d.GetType();
            for (; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericArguments().Length == 2) return t.GetGenericArguments()[1];
            foreach (var v in d.Values) if (v != null) return v.GetType();
            return null;
        }

        static MethodInfo _loader;
        static object _loaderTarget;

        static bool FindLoader()
        {
            if (_loader != null) return true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Assembly-CSharp"))
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    var m = t.GetMethods(Inst | BindingFlags.Static).FirstOrDefault(x => x.Name == "LoadBundlesAndCreatePools" &&
                        x.GetParameters().Any(p => p.ParameterType.IsArray || (p.ParameterType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(p.ParameterType))));
                    if (m == null) continue;
                    object target = null;
                    if (!m.IsStatic)
                    {
                        var single = Game.FindType("Comfort.Common.Singleton`1");
                        try { target = single?.MakeGenericType(t).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null); } catch { }
                        if (!Game.Alive(target)) { Log($"{t.FullName}.{m.Name} found but Singleton<{t.Name}> is empty"); continue; }
                    }
                    _loader = m; _loaderTarget = target;
                    Log($"bundle loader: {t.FullName}.{m.Name}(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                    return true;
                }
            }
            Warn("no LoadBundlesAndCreatePools method found in Assembly-CSharp");
            return false;
        }

        static object PickEnum(Type t, params string[] preferred)
        {
            foreach (var n in preferred)
                foreach (var v in Enum.GetNames(t)) if (string.Equals(v, n, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(t, v);
            return Enum.GetValues(t).GetValue(0);
        }

        /// <summary>Arguments for the bundle loader, chosen by parameter type (logged).</summary>
        static object[] LoaderArgs(Type keyType, Array keys)
        {
            var ps = _loader.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt.IsArray && pt.GetElementType() == keyType) args[i] = keys;
                else if (pt.IsGenericType && pt.IsAssignableFrom(keys.GetType())) args[i] = keys;
                else if (pt.IsEnum)
                    args[i] = pt.Name.IndexOf("Priority", StringComparison.OrdinalIgnoreCase) >= 0 ? PickEnum(pt, "Immediate", "General")
                            : pt.Name.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0 ? PickEnum(pt, "Local", "Offline")
                            : PickEnum(pt, "Raid", "Hideout", "Player");
                else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);   // CancellationToken, bool, ...
                else args[i] = null;                                              // progress callbacks
            }
            Game.LogOnce("loaderargs", "Wear: loader arguments: " + string.Join(", ", args.Select((a, i) => ps[i].Name + "=" + (a is Array ? $"[{((Array)a).Length} keys]" : a?.ToString() ?? "null"))), false);
            return args;
        }

        static IEnumerator Await(Task t, string what, Action<string> fail)
        {
            float until = Time.realtimeSinceStartup + 60f;
            while (!t.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!t.IsCompleted) fail(what + " did not finish in 60 s");
            else if (t.IsFaulted) fail(what + " failed: " + (t.Exception?.GetBaseException().ToString() ?? "?"));
        }

        /// <summary>Wears the given catalog entries (any parts). done(message) is called with the result.</summary>
        public static IEnumerator Wear(Component player, IList<Outfit> items, Action<string> done)
        {
            Busy = true;
            string error = null;
            Action<string> fail = m => { if (error == null) { error = m; Warn(m); } };
            Array keys = null;
            Type keyType = null;
            try
            {
                if (player == null) fail("no local player (try-on works in raid and the hideout)");
                var profile = Game.Get(player, "Profile");
                var cust = Game.Get(profile, "Customization") as IDictionary;
                var body = (Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody")) as Component;
                if (error == null && cust == null) fail("Profile.Customization is not a dictionary");
                if (error == null && body == null) fail("PlayerBody not found");
                if (error == null && !FindLoader()) fail("bundle loader not found (see log)");
                var init = body?.GetType().GetMethods(Inst).FirstOrDefault(m => m.Name == "Init" && m.GetParameters().Length == 8);
                if (error == null && init == null) fail("PlayerBody.Init with 8 parameters not found");

                if (error == null)
                {
                    // remember the real outfit once, then set the new ids
                    if (_original == null)
                    {
                        _original = new Dictionary<string, string>();
                        foreach (DictionaryEntry de in cust) _original[de.Key.ToString()] = de.Value?.ToString();
                    }
                    var vt = ValueType(cust);
                    foreach (var o in items)
                    {
                        var k = FindKey(cust, BodyKey(o.Part));
                        if (k == null) { fail($"no '{BodyKey(o.Part)}' in Profile.Customization"); break; }
                        cust[k] = MakeId(vt, o.Id);
                    }
                    // the same ids into PlayerBody.BodyCustomization when it's a separate object
                    var bc = Game.Get(body, "BodyCustomization") as IDictionary;
                    if (bc != null && !ReferenceEquals(bc, cust))
                        foreach (var o in items) { var k = FindKey(bc, BodyKey(o.Part)); if (k != null) bc[k] = MakeId(ValueType(bc), o.Id); }

                    var keyParam = _loader.GetParameters().First(p => p.ParameterType.IsArray || p.ParameterType.IsGenericType).ParameterType;
                    keyType = keyParam.IsArray ? keyParam.GetElementType() : keyParam.GetGenericArguments()[0];
                    keys = Array.CreateInstance(keyType, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        var rk = Activator.CreateInstance(keyType);
                        SetMember(rk, "path", items[i].Bundle);
                        SetMember(rk, "rcid", "");
                        keys.SetValue(rk, i);
                    }
                    Log($"loading {items.Count} bundle(s): " + string.Join(", ", items.Select(o => o.Bundle)));
                }
            }
            catch (Exception e) { fail("preparing failed: " + e); }

            Task load = null;
            if (error == null)
            {
                try { load = _loader.Invoke(_loader.IsStatic ? null : _loaderTarget, LoaderArgs(keyType, keys)) as Task; }
                catch (Exception e) { fail("bundle loader call failed: " + (e.InnerException ?? e)); }
            }
            if (load != null) yield return Await(load, "loading the bundles", fail);

            Task initTask = null;
            if (error == null)
            {
                try
                {
                    var profile = Game.Get(player, "Profile");
                    var body = (Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody")) as Component;
                    var init = body.GetType().GetMethods(Inst).First(m => m.Name == "Init" && m.GetParameters().Length == 8);
                    var ps = init.GetParameters();
                    var customization = Game.Get(profile, "Customization");
                    if (!ps[0].ParameterType.IsInstanceOfType(customization)) customization = Game.Get(body, "BodyCustomization");
                    var lo = Game.Get(body, "_layer");
                    int layer = lo is int ? (int)lo : LayerMask.NameToLayer("Player");
                    var yo = Game.Get(body, "_isYourPlayer");
                    var args = new object[]
                    {
                        customization, Game.Get(body, "_equipment") ?? Game.Get(body, "Equipment"), Game.Get(body, "_itemInHands"),
                        layer, Game.Get(body, "_side"), Game.Get(body, "_playerProfileID") ?? "", null,
                        yo is bool && (bool)yo
                    };
                    for (int i = 0; i < ps.Length; i++)
                        if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i]))
                            Warn($"Init argument {i} ({ps[i].Name}) is a {args[i].GetType().Name}, expected {ps[i].ParameterType.Name}");
                    initTask = init.Invoke(body, args) as Task;
                }
                catch (Exception e) { fail("PlayerBody.Init failed: " + (e.InnerException ?? e)); }
            }
            if (initTask != null) yield return Await(initTask, "PlayerBody.Init", fail);

            if (error == null)
            {
                try
                {
                    var body = (Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody")) as Component;
                    var upd = body.GetType().GetMethod("UpdatePlayerRenders", Inst);
                    upd?.Invoke(body, new[] { Game.Get(player, "PointOfView"), Game.Get(body, "_side") });
                }
                catch (Exception e) { fail("UpdatePlayerRenders failed: " + (e.InnerException ?? e)); }
            }
            Busy = false;
            string msg = error == null ? "Wearing: " + string.Join(", ", items.Select(o => $"{o.Part} '{o.Name}'")) + " (not saved)" : "Try-on failed: " + error;
            if (error == null) Log(msg);
            done(msg);
        }

        /// <summary>Saves the head to the PMC profile through WTT HeadVoiceSelector's server route (sgtlaggy/spt-HeadVoiceSelector-server:
        /// POST /WTT/WTTChangeHead {"Data": id} -> "OK"), sent with SPT's own SPT.Common.Http.RequestHandler.PostJsonAsync.</summary>
        public static IEnumerator SaveHead(string headId, Action<string> done)
        {
            Task t = null;
            string err = null;
            try
            {
                var rh = Game.FindType("SPT.Common.Http.RequestHandler");
                var m = rh?.GetMethod("PostJsonAsync", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null);
                if (m == null) err = "SPT.Common.Http.RequestHandler.PostJsonAsync not found";
                else t = m.Invoke(null, new object[] { "/WTT/WTTChangeHead", "{\"Data\":\"" + headId + "\"}" }) as Task;
            }
            catch (Exception e) { err = (e.InnerException ?? e).Message; }
            if (t != null)
            {
                float until = Time.realtimeSinceStartup + 20f;
                while (!t.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
                if (!t.IsCompleted) err = "no answer from the server in 20 s";
                else if (t.IsFaulted) err = t.Exception?.GetBaseException().Message;
                else
                {
                    var answer = t.GetType().GetProperty("Result")?.GetValue(t, null) as string;
                    if (answer == null || answer.IndexOf("OK", StringComparison.Ordinal) < 0) err = "server answered: " + (answer ?? "null");
                }
            }
            string msg = err == null ? $"Head {headId} saved to your PMC profile (HeadVoiceSelector)" : "Saving the head failed: " + err;
            if (err == null) Log(msg); else Warn(msg);
            done(msg);
        }

        public static List<Outfit> OriginalOutfit(Catalog cat)
        {
            var list = new List<Outfit>();
            if (_original == null || cat == null) return list;
            foreach (var part in new[] { "Body", "Feet", "Head", "Hands" })
                { string id; if (_original.TryGetValue(part, out id) && cat.ById(id) != null) list.Add(cat.ById(id)); }
            return list;
        }

        public static bool HasOriginal => _original != null;

        static void SetMember(object o, string name, object value)
        {
            var t = o.GetType();
            var f = t.GetField(name, Inst);
            if (f != null) { f.SetValue(o, value); return; }
            var p = t.GetProperty(name, Inst);
            if (p != null && p.CanWrite) p.SetValue(o, value, null);
            else Game.LogOnce("rk:" + name, $"Wear: {t.FullName} has no writable '{name}'");
        }
    }
}
