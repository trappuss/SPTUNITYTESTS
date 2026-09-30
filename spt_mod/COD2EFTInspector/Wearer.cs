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
        static object _originalProfile;                 // the profile _original belongs to (PMC / scav / a new session)
        // part -> id being tried on. The profile itself is put back to its real ids as soon as the body is rebuilt, so a
        // try-on can never reach the server (SPT sends profile data back at raid end); reports read the try-on from here.
        static readonly Dictionary<string, string> _tryOn = new Dictionary<string, string>();

        /// <summary>Customization lines ("Part = id") with the try-on ids swapped in and marked.</summary>
        public static List<string> WithTryOn(List<string> lines)
        {
            var res = new List<string>();
            foreach (var l in lines)
            {
                int i = l.IndexOf(" = ", StringComparison.Ordinal);
                string part = i > 0 ? l.Substring(0, i) : null, id;
                res.Add(part != null && _tryOn.TryGetValue(part, out id) ? $"{part} = {id} (try-on, not saved; real: {l.Substring(i + 3)})" : l);
            }
            return res;
        }

        /// <summary>Id worn now for a part (try-on first, else the profile's line).</summary>
        public static string TryOnId(string part) { string id; return _tryOn.TryGetValue(part, out id) ? id : null; }

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
        internal static object MakeId(Type t, string id)
        {
            if (t == null || t == typeof(string) || t == typeof(object)) return id;
            var c = t.GetConstructor(new[] { typeof(string) });
            if (c != null) return c.Invoke(new object[] { id });
            var parse = t.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (parse != null) return parse.Invoke(null, new object[] { id });
            throw new InvalidOperationException("can't make a " + t.FullName + " from a string");
        }

        internal static Type ValueType(IDictionary d)
        {
            var t = d.GetType();
            for (; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericArguments().Length == 2) return t.GetGenericArguments()[1];
            foreach (var v in d.Values) if (v != null) return v.GetType();
            return null;
        }

        /// <summary>One line for an exception: type, message, the null parameter's name and the first game frames.</summary>
        internal static string Describe(Exception e)
        {
            if (e == null) return "?";
            e = e.GetBaseException();
            var an = e as ArgumentException;
            string param = an != null && !string.IsNullOrEmpty(an.ParamName) ? $" [parameter '{an.ParamName}']" : "";
            var frames = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4);
            return $"{e.GetType().Name}: {e.Message.Split('\n')[0].Trim()}{param} at {string.Join(" < ", frames)}";
        }

        static IEnumerator Await(Task t, string what, Action<string> fail)
        {
            float until = Time.realtimeSinceStartup + 60f;
            while (!t.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!t.IsCompleted) fail(what + " did not finish in 60 s");
            else if (t.IsFaulted)
            {
                InspectorPlugin.Log.LogWarning($"Wear: {what} failed, full exception: {t.Exception}");
                fail(what + " failed: " + Describe(t.Exception));
            }
        }

        static string Name(Outfit o) => $"{o.Part} '{o.Name}' (id {o.Id ?? "null"}, bundle '{o.Bundle ?? "null"}', from {o.Source})";

        /// <summary>Wears the given catalog entries (any parts). done(message) is called with the result.</summary>
        public static IEnumerator Wear(Component player, IList<Outfit> items, Action<string> done)
        {
            Busy = true;
            string error = null;
            Action<string> fail = m => { if (error == null) { error = m; Warn(m); } };
            try
            {
                if (player == null) fail("no local player (try-on works in raid and the hideout)");
                var profile = Game.Get(player, "Profile");
                var cust = Game.Get(profile, "Customization") as IDictionary;
                var body = (Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody")) as Component;
                if (error == null && cust == null) fail("Profile.Customization is not a dictionary");
                if (error == null && body == null) fail("PlayerBody not found");
                if (error == null && !BundleLoader.Find()) fail("bundle loader not found (see log)");
                var init = body?.GetType().GetMethods(Inst).FirstOrDefault(m => m.Name == "Init" && m.GetParameters().Length == 8);
                if (error == null && init == null) fail("PlayerBody.Init with 8 parameters not found");
                foreach (var o in items)
                    if (error == null && (string.IsNullOrEmpty(o.Id) || string.IsNullOrEmpty(o.Bundle)))
                        fail("this catalog entry has no " + (string.IsNullOrEmpty(o.Id) ? "id" : "bundle path") + ": " + Name(o));
                if (error == null)
                    Log($"target: {player.GetType().Name} '{player.name}', PlayerBody #{body.GetInstanceID()} at {BodyScan.PathOf(body.transform)}; " +
                        "items: " + string.Join("; ", items.Select(Name)));

                if (error == null)
                {
                    // remember the real outfit once, then set the new ids
                    if (_original == null || !ReferenceEquals(_originalProfile, profile))
                    {
                        _originalProfile = profile;
                        _tryOn.Clear();
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

                    // (the bundle keys are built by BundleLoader from each item's bundle path)
                }
            }
            catch (Exception e) { fail("preparing failed: " + e); }

            string loadErr = null;
            if (error == null) yield return BundleLoader.Load(items, m => loadErr = m);
            if (error == null && loadErr != null)
            {
                if (BundleLoader.AllLoaded(items)) Warn(loadErr + " -- but every bundle is already in memory, so the body is rebuilt anyway");
                else fail(loadErr);
            }

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
                        if (args[i] == null && i != 6) Warn($"Init argument {i} ({ps[i].ParameterType.Name} {ps[i].Name}) is null");
                        else if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i]))
                            Warn($"Init argument {i} ({ps[i].Name}) is a {args[i].GetType().Name}, expected {ps[i].ParameterType.Name}");
                    initTask = init.Invoke(body, args) as Task;
                }
                catch (Exception e) { fail("PlayerBody.Init failed: " + Describe(e)); }
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
            // put the profile's real ids back (the rebuilt body keeps the try-on look); remember the try-on separately
            try
            {
                var profile = Game.Get(player, "Profile");
                var body = (Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody")) as Component;
                foreach (var d in new[] { Game.Get(profile, "Customization") as IDictionary, Game.Get(body, "BodyCustomization") as IDictionary })
                {
                    if (d == null || _original == null) continue;
                    var vt = ValueType(d);
                    foreach (var o in items)
                    {
                        string part = BodyKey(o.Part), real;
                        var k = FindKey(d, part);
                        if (k != null && _original.TryGetValue(part, out real) && real != null) d[k] = MakeId(vt, real);
                    }
                }
                if (error == null)
                    foreach (var o in items)
                    {
                        string part = BodyKey(o.Part), real;
                        if (_original != null && _original.TryGetValue(part, out real) && real == o.Id) _tryOn.Remove(part);
                        else _tryOn[part] = o.Id;
                    }
            }
            catch (Exception e) { Warn("putting the profile's real ids back failed: " + e); }
            try
            {
                var body = (Game.Get(player, "_playerBody") ?? Game.Get(player, "PlayerBody")) as Component;
                if (error == null && body != null) { _watchBody = body; _watchSkins = BodyScan.Skins(body); Log("body now shows " + _watchSkins); }
                BodyScan.LogBodies(body);
            }
            catch (Exception e) { Warn("body diagnosis failed: " + e.Message); }
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
        public static bool HasTryOn => _tryOn.Count > 0;

        // hideout vs menu diagnosis: does the game rebuild the body after a try-on (overwriting it)?
        static Component _watchBody;
        static string _watchSkins;

        /// <summary>Called every second: a message once when the body the try-on dressed now shows something else.</summary>
        public static string CheckOverwritten()
        {
            if (Busy || _watchBody == null) return null;
            if (!Game.Alive(_watchBody)) { _watchBody = null; return "the try-on's body was destroyed (the game made a new one: your real outfit)"; }
            string now = BodyScan.Skins(_watchBody);
            if (now == _watchSkins) return null;
            string msg = $"the game rebuilt your body after the try-on: [{_watchSkins}] -> [{now}]";
            _watchBody = null;
            return msg;
        }

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
