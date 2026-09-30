// Loads outfit bundles into the game's pools before PlayerBody.Init (try-on in hideout / raid).
// 0.7.0 log (from_pc/20260929-195957): the only LoadBundlesAndCreatePools found in SPT 4.1.6 is
//   EFT.ObjectsFactory.LoadBundlesAndCreatePools(Pools pools, List<PoolResourceInfo> resources, AssemblyType assemblyType,
//                                                YieldDelegate yield, IProgress<> progress, CancellationToken ct)
// and 0.7.0 passed null for pools / resources / yield ("Value cannot be null, parameter 'source'" = resources).
// This builds every argument by type: the resource list as List<T> of T made from the bundle path (T's own 'path'
// member, a constructor taking a path / ResourceKey, or a ResourceKey member), and class / delegate arguments from the
// game's own static instances (Pools, YieldDelegate: found by type, logged). Every candidate loader is tried in turn.
// The first use logs the members of each type involved, so a wrong guess is fixable from one log.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class BundleLoader
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        sealed class Cand { public MethodInfo M; public object Target; public ParameterInfo Coll; public Type Elem; }
        static List<Cand> _cands;

        static void Log(string m) => InspectorPlugin.Log.LogInfo("Wear: " + m);
        static void Warn(string m) => InspectorPlugin.Log.LogWarning("Wear: " + m);

        static Type[] AllTypes(Assembly a)
        {
            try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        static Type ElementOf(Type t)
        {
            if (t == typeof(string)) return null;
            if (t.IsArray) return t.GetElementType();
            if (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t) && t.GetGenericArguments().Length == 1) return t.GetGenericArguments()[0];
            return null;
        }

        static bool HasPath(Type t) =>
            t.GetField("path", Inst) != null || (t.GetProperty("path", Inst)?.CanWrite ?? false) || (t.GetProperty("Path", Inst)?.CanWrite ?? false);

        static string Members(Type t)
        {
            if (t == null) return "?";
            var ms = new List<string>();
            foreach (var c in t.GetConstructors(Inst)) ms.Add("ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            foreach (var f in t.GetFields(Inst | BindingFlags.Static | BindingFlags.DeclaredOnly)) ms.Add((f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name);
            foreach (var p in t.GetProperties(Inst | BindingFlags.Static | BindingFlags.DeclaredOnly)) ms.Add("prop " + p.PropertyType.Name + " " + p.Name);
            if (typeof(Delegate).IsAssignableFrom(t))
            {
                var inv = t.GetMethod("Invoke");
                if (inv != null) ms.Add("invoke " + inv.ReturnType.Name + "(" + string.Join(", ", inv.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            }
            return $"{t.FullName}{(t.IsEnum ? " (enum)" : t.IsValueType ? " (struct)" : "")}: " + string.Join("; ", ms);
        }

        static object SingletonOf(Type t)
        {
            var single = Game.FindType("Comfort.Common.Singleton`1");
            try
            {
                var v = single?.MakeGenericType(t).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
                if (Game.Alive(v)) return v;
            }
            catch { }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) { var o = UnityEngine.Object.FindObjectOfType(t); if (o != null) return o; }
            return null;
        }

        public static bool Find()
        {
            if (_cands != null) return _cands.Count > 0;
            _cands = new List<Cand>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Assembly-CSharp"))
                foreach (var t in AllTypes(a))
                    foreach (var m in t.GetMethods(Inst | Stat | BindingFlags.DeclaredOnly).Where(x => x.Name == "LoadBundlesAndCreatePools"))
                    {
                        if (m.ContainsGenericParameters) continue;
                        var coll = m.GetParameters().FirstOrDefault(p => ElementOf(p.ParameterType) != null);
                        string sig = $"{t.FullName}.{m.Name}(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
                        if (coll == null) { Log("loader skipped (no resource list): " + sig); continue; }
                        object target = null;
                        if (!m.IsStatic && (target = SingletonOf(t)) == null) { Log("loader skipped (no instance of " + t.Name + "): " + sig); continue; }
                        _cands.Add(new Cand { M = m, Target = target, Coll = coll, Elem = ElementOf(coll.ParameterType) });
                        Log("loader candidate: " + sig + (m.IsStatic ? " (static)" : " (instance found)"));
                        Game.LogOnce("elem:" + coll.ParameterType.FullName, "Wear: resource type " + Members(ElementOf(coll.ParameterType)), false);
                        foreach (var p in m.GetParameters().Where(p => p != coll && !p.ParameterType.IsValueType && p.ParameterType != typeof(string)))
                            Game.LogOnce("ptype:" + p.ParameterType.FullName, "Wear: argument type " + Members(p.ParameterType), false);
                    }
            // lists of path-keyed resources (the older PoolManager shape) first
            _cands = _cands.OrderByDescending(c => HasPath(c.Elem)).ToList();
            if (_cands.Count == 0) Warn("no usable LoadBundlesAndCreatePools found in Assembly-CSharp");
            return _cands.Count > 0;
        }

        static object New(Type t)
        {
            if (t.IsValueType) return Activator.CreateInstance(t);
            var c = t.GetConstructor(Inst, null, Type.EmptyTypes, null);
            return c != null ? c.Invoke(null) : FormatterServices.GetUninitializedObject(t);
        }

        static bool Set(object o, string name, object v)
        {
            var t = o.GetType();
            var f = t.GetField(name, Inst);
            if (f != null && f.FieldType.IsInstanceOfType(v)) { f.SetValue(o, v); return true; }
            var p = t.GetProperty(name, Inst);
            if (p != null && p.CanWrite && p.PropertyType.IsInstanceOfType(v)) { p.SetValue(o, v, null); return true; }
            return false;
        }

        /// <summary>An element of type t for this bundle path (depth: ResourceKey inside a wrapper).</summary>
        static object MakeElem(Type t, string path, int depth = 0)
        {
            if (t == typeof(string)) return path;
            if (depth > 2) return null;
            if (HasPath(t))
            {
                var o = New(t);
                if (!Set(o, "path", path)) Set(o, "Path", path);
                if (!Set(o, "rcid", "")) Set(o, "Rcid", "");
                return o;
            }
            // constructor whose parameters we can fill: the first path-like one gets the bundle, the rest defaults
            foreach (var c in t.GetConstructors(Inst).OrderBy(c => c.GetParameters().Length))
            {
                var ps = c.GetParameters();
                if (ps.Length == 0) continue;
                var args = new object[ps.Length];
                bool got = false;
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (!got && (pt == typeof(string) || HasPath(pt))) { args[i] = MakeElem(pt, path, depth + 1); got = args[i] != null; }
                    else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                if (!got) continue;
                try { return c.Invoke(args); } catch (Exception e) { Warn($"new {t.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))}) failed: {(e.InnerException ?? e).Message}"); }
            }
            // a field / property holding a ResourceKey-like value
            var obj = New(t);
            foreach (var f in t.GetFields(Inst).Where(f => !f.IsInitOnly || true))
                if (HasPath(f.FieldType) || (f.FieldType == typeof(string) && f.Name.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0))
                { f.SetValue(obj, MakeElem(f.FieldType, path, depth + 1)); return obj; }
            foreach (var p in t.GetProperties(Inst).Where(p => p.CanWrite))
                if (HasPath(p.PropertyType) || (p.PropertyType == typeof(string) && p.Name.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0))
                { p.SetValue(obj, MakeElem(p.PropertyType, path, depth + 1), null); return obj; }
            return null;
        }

        static object MakeCollection(Cand c, IList<Outfit> items, List<string> problems)
        {
            var elems = new List<object>();
            foreach (var o in items)
            {
                var e = MakeElem(c.Elem, o.Bundle);
                if (e == null) { problems.Add($"can't make a {c.Elem.Name} from a bundle path (members logged above)"); return null; }
                elems.Add(e);
            }
            var pt = c.Coll.ParameterType;
            if (pt.IsArray)
            {
                var arr = Array.CreateInstance(c.Elem, elems.Count);
                for (int i = 0; i < elems.Count; i++) arr.SetValue(elems[i], i);
                return arr;
            }
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(c.Elem));
            foreach (var e in elems) list.Add(e);
            if (!pt.IsInstanceOfType(list)) { problems.Add($"{pt.Name} is not a List"); return null; }
            return list;
        }

        static object PickEnum(Type t, params string[] preferred)
        {
            foreach (var n in preferred)
                foreach (var v in Enum.GetNames(t)) if (string.Equals(v, n, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(t, v);
            return Enum.GetValues(t).GetValue(0);
        }

        static readonly string[] Preferred = { "Immediate", "General", "Default", "Player", "Raid", "Hideout", "Local", "Instance", "Main" };

        static object Best(List<KeyValuePair<string, object>> found, Type t, string where)
        {
            if (found.Count == 0) return null;
            var pick = found.FirstOrDefault(kv => Preferred.Any(n => string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase)));
            if (pick.Value == null) pick = found[0];
            Log($"argument {t.Name} = {where}.{pick.Key}" + (found.Count > 1 ? $" (of: {string.Join(", ", found.Select(kv => kv.Key).Distinct().Take(12))})" : ""));
            return pick.Value;
        }

        static void StaticsOf(Type owner, Type t, List<KeyValuePair<string, object>> found)
        {
            try
            {
                foreach (var f in owner.GetFields(Stat))
                    if (t.IsAssignableFrom(f.FieldType) && !f.FieldType.ContainsGenericParameters) { var v = f.GetValue(null); if (v != null) found.Add(new KeyValuePair<string, object>(f.Name, v)); }
                foreach (var p in owner.GetProperties(Stat))
                    if (t.IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0 && !owner.ContainsGenericParameters)
                    { var v = p.GetValue(null, null); if (v != null) found.Add(new KeyValuePair<string, object>(p.Name, v)); }
            }
            catch { }
        }

        /// <summary>A game object of type t for a class / delegate argument: its own statics, the loader's owner, the loader's instance,
        /// a singleton, or (delegates) a static field / method of that shape in its assembly.</summary>
        static object Resolve(Type t, Cand c)
        {
            var found = new List<KeyValuePair<string, object>>();
            StaticsOf(t, t, found);
            if (found.Count > 0) return Best(found, t, t.Name);
            StaticsOf(c.M.DeclaringType, t, found);
            if (found.Count > 0) return Best(found, t, c.M.DeclaringType.Name);
            if (c.Target != null)
            {
                foreach (var f in c.Target.GetType().GetFields(Inst))
                    if (t.IsAssignableFrom(f.FieldType)) { var v = f.GetValue(c.Target); if (v != null) found.Add(new KeyValuePair<string, object>(f.Name, v)); }
                if (found.Count > 0) return Best(found, t, "(the loader's instance)");
            }
            var s = SingletonOf(t);
            if (s != null) { Log($"argument {t.Name} = Singleton<{t.Name}>.Instance"); return s; }
            if (typeof(Delegate).IsAssignableFrom(t))
            {
                foreach (var ty in AllTypes(t.Assembly)) StaticsOf(ty, t, found);
                if (found.Count > 0) return Best(found, t, "(static in " + t.Assembly.GetName().Name + ")");
                var inv = t.GetMethod("Invoke");
                var sig = inv.GetParameters().Select(p => p.ParameterType).ToArray();
                foreach (var ty in AllTypes(t.Assembly))
                    foreach (var m in ty.GetMethods(Stat).Where(m => !m.ContainsGenericParameters && m.ReturnType == inv.ReturnType &&
                                                                     m.GetParameters().Select(p => p.ParameterType).SequenceEqual(sig)))
                        try { found.Add(new KeyValuePair<string, object>(ty.Name + "." + m.Name, Delegate.CreateDelegate(t, m))); } catch { }
                if (found.Count > 0) return Best(found, t, "(static method)");
            }
            // a class with a parameterless constructor (e.g. a settings object): a fresh one
            if (!t.IsAbstract && !typeof(Delegate).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
            { Log($"argument {t.Name} = new {t.Name}() (no game instance found)"); return Activator.CreateInstance(t); }
            return null;
        }

        static object[] Args(Cand c, object coll, List<string> problems)
        {
            var ps = c.M.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (ps[i] == c.Coll) args[i] = coll;
                else if (pt.IsEnum)
                    args[i] = pt.Name.IndexOf("Priority", StringComparison.OrdinalIgnoreCase) >= 0 ? PickEnum(pt, "Immediate", "General")
                            : pt.Name.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0 ? PickEnum(pt, "Local", "Offline")
                            : PickEnum(pt, "Player", "Raid", "Hideout", "General");
                else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                else if (pt.IsGenericType && pt.GetGenericTypeDefinition().Name.StartsWith("IProgress")) args[i] = null;   // optional callback
                else
                {
                    args[i] = Resolve(pt, c);
                    if (args[i] == null) problems.Add($"no value for '{ps[i].Name}' ({pt.FullName}; members logged above)");
                }
            }
            Game.LogOnce("args:" + c.M.DeclaringType.FullName, "Wear: loader arguments: " + string.Join(", ",
                args.Select((a, i) => ps[i].ParameterType.Name + " " + ps[i].Name + "=" + (a is ICollection ? $"[{((ICollection)a).Count} resources]" : a?.ToString() ?? "null"))), false);
            return args;
        }

        static IEnumerator Run(Cand c, IList<Outfit> items, float timeout, Action<string> result)
        {
            var problems = new List<string>();
            Task t = null;
            try
            {
                var coll = MakeCollection(c, items, problems);
                var args = problems.Count == 0 ? Args(c, coll, problems) : null;
                if (problems.Count == 0) t = c.M.Invoke(c.M.IsStatic ? null : c.Target, args) as Task;
            }
            catch (Exception e) { problems.Add("call failed: " + Wearer.Describe(e)); }
            if (t != null)
            {
                float until = Time.realtimeSinceStartup + timeout;
                while (!t.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
                if (!t.IsCompleted) problems.Add($"no answer in {timeout:0} s");
                else if (t.IsFaulted)
                {
                    InspectorPlugin.Log.LogWarning($"Wear: {c.M.DeclaringType.Name}.{c.M.Name} failed, full exception: {t.Exception}");
                    problems.Add(Wearer.Describe(t.Exception));
                }
            }
            result(problems.Count == 0 ? null : string.Join("; ", problems));
        }

        /// <summary>Loads the items' bundles. done(null) on success, else a message naming the loader and the failing bundles.</summary>
        public static IEnumerator Load(IList<Outfit> items, Action<string> done)
        {
            if (!Find()) { done("bundle loader not found (see log)"); yield break; }
            Log($"loading {items.Count} bundle(s): " + string.Join(", ", items.Select(o => o.Bundle)));
            var errors = new List<string>();
            foreach (var c in _cands)
            {
                string err = null;
                yield return Run(c, items, 60f, m => err = m);
                if (err == null) { Log($"bundles loaded with {c.M.DeclaringType.Name}.{c.M.Name}"); done(null); yield break; }
                errors.Add($"{c.M.DeclaringType.Name}: {err}");
            }
            // every loader failed: which bundles fail alone (with the first loader)?
            var bad = new List<string>();
            if (items.Count > 1)
                foreach (var o in items)
                {
                    string err = null;
                    yield return Run(_cands[0], new[] { o }, 30f, m => err = m);
                    Log($"diagnosis: {o.Part} '{o.Name}' bundle '{o.Bundle}': {err ?? "loads fine alone"}");
                    if (err != null) bad.Add($"{o.Part} '{o.Name}' bundle '{o.Bundle}'");
                }
            done("loading the bundles failed: " + string.Join(" | ", errors) +
                 (bad.Count > 0 && bad.Count < items.Count ? " | failing alone: " + string.Join("; ", bad) : ""));
        }

        /// <summary>True when every item's bundle is already loaded (worn now, or loaded by the menu): Init can use it.</summary>
        public static bool AllLoaded(IList<Outfit> items)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
                if (b != null) { names.Add(b.name); names.Add(System.IO.Path.GetFileName(b.name.Replace('\\', '/'))); }
            return items.All(o => o.Bundle != null && (names.Contains(o.Bundle) || names.Contains(System.IO.Path.GetFileName(o.Bundle.Replace('\\', '/')))));
        }
    }
}
