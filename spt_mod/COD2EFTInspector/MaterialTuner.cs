// Materials tab (0.10.0): live tuning of the worn outfit's shader values, and a texture channel view.
//  - Every float / range / colour / vector property of a material's shader is listed (Shader.GetProperty*); a change is
//    applied to the loaded material at once (all renderers using it). The first change of a property stores its old value,
//    so Reset puts back exactly what the bundle had.
//  - Save writes <time>_<outfit>_material_tuning.txt: per material the changed values, "was" and "now", in a form that
//    can be copied into the Blender preview / Unity material settings (COD2EFT_TEXTURE_SPEC.md names).
//  - Channel view: every body renderer temporarily gets unlit copies showing one texture slot (_MainTex, _BumpMap, ...);
//    the original material arrays are restored on Off, on a rescan and when the plugin unloads.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace COD2EFTInspector
{
    internal sealed class MaterialTuner
    {
        public sealed class Prop
        {
            public string Name; public ShaderPropertyType Type; public float Min, Max;
        }

        // material -> property -> original value (float, Color or Vector4)
        readonly Dictionary<Material, Dictionary<string, object>> _orig = new Dictionary<Material, Dictionary<string, object>>();
        readonly Dictionary<Renderer, Material[]> _viewSaved = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Texture, Material> _viewMats = new Dictionary<Texture, Material>();
        Material _viewBlank;
        public string View;   // texture property shown, null = normal

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<Prop> Props(Material m)
        {
            var list = new List<Prop>();
            var sh = m != null ? m.shader : null;
            if (sh == null) return list;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                var t = sh.GetPropertyType(i);
                if ((sh.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0 && t != ShaderPropertyType.Texture) continue;
                var p = new Prop { Name = sh.GetPropertyName(i), Type = t };
                if (t == ShaderPropertyType.Range) { var r = sh.GetPropertyRangeLimits(i); p.Min = r.x; p.Max = r.y; }
                list.Add(p);
            }
            return list;
        }

        public bool Changed(Material m) => m != null && _orig.ContainsKey(m) && _orig[m].Count > 0;
        public bool Changed(Material m, string prop) => m != null && _orig.ContainsKey(m) && _orig[m].ContainsKey(prop);
        public int ChangedCount => _orig.Values.Sum(d => d.Count);

        void Remember(Material m, string prop, object value)
        {
            Dictionary<string, object> d;
            if (!_orig.TryGetValue(m, out d)) _orig[m] = d = new Dictionary<string, object>();
            if (!d.ContainsKey(prop)) d[prop] = value;
        }

        public object Original(Material m, string prop)
        {
            Dictionary<string, object> d; object v;
            return m != null && _orig.TryGetValue(m, out d) && d.TryGetValue(prop, out v) ? v : null;
        }

        public void SetFloat(Material m, string prop, float v)
        {
            float cur = m.GetFloat(prop);
            if (Mathf.Approximately(cur, v)) return;
            Remember(m, prop, cur);
            m.SetFloat(prop, v);
        }

        public void SetColor(Material m, string prop, Color v)
        {
            var cur = m.GetColor(prop);
            if (cur == v) return;
            Remember(m, prop, cur);
            m.SetColor(prop, v);
        }

        public void SetVector(Material m, string prop, Vector4 v)
        {
            var cur = m.GetVector(prop);
            if (cur == v) return;
            Remember(m, prop, cur);
            m.SetVector(prop, v);
        }

        static void Put(Material m, string prop, object v)
        {
            if (v is float) m.SetFloat(prop, (float)v);
            else if (v is Color) m.SetColor(prop, (Color)v);
            else if (v is Vector4) m.SetVector(prop, (Vector4)v);
        }

        public void ResetProp(Material m, string prop)
        {
            Dictionary<string, object> d; object v;
            if (m == null || !_orig.TryGetValue(m, out d) || !d.TryGetValue(prop, out v)) return;
            Put(m, prop, v);
            d.Remove(prop);
        }

        public void Reset(Material m)
        {
            Dictionary<string, object> d;
            if (m == null || !_orig.TryGetValue(m, out d)) return;
            foreach (var kv in d) Put(m, kv.Key, kv.Value);
            _orig.Remove(m);
        }

        public void ResetAll()
        {
            foreach (var m in _orig.Keys.ToList()) if (m != null) Reset(m);
            _orig.Clear();
        }

        static string Fmt(object v)
        {
            if (v is float) return ((float)v).ToString("0.####", Inv);
            if (v is Color) { var c = (Color)v; return string.Format(Inv, "({0:0.####}, {1:0.####}, {2:0.####}, {3:0.####})", c.r, c.g, c.b, c.a); }
            if (v is Vector4) { var c = (Vector4)v; return string.Format(Inv, "({0:0.####}, {1:0.####}, {2:0.####}, {3:0.####})", c.x, c.y, c.z, c.w); }
            return v?.ToString() ?? "?";
        }

        static object Current(Material m, string prop, object like) =>
            like is float ? (object)m.GetFloat(prop) : like is Color ? (object)m.GetColor(prop) : m.GetVector(prop);

        /// <summary>The tuning file: every changed value per material, old and new.</summary>
        public string Report(string outfit, Func<Material, string> where)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# COD2EFT Inspector v{InspectorPlugin.Version} - material tuning, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# outfit: {outfit}");
            sb.AppendLine("# format: property = new value   (was: bundle value)");
            foreach (var kv in _orig.Where(k => k.Key != null && k.Value.Count > 0))
            {
                var m = kv.Key;
                sb.AppendLine();
                sb.AppendLine($"[{m.name.Replace(" (Instance)", "")}]  shader {m.shader?.name}  used by {where(m)}");
                foreach (var p in kv.Value.OrderBy(p => p.Key))
                    sb.AppendLine($"{p.Key} = {Fmt(Current(m, p.Key, p.Value))}   (was: {Fmt(p.Value)})");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ channel view

        static Shader _unlit;
        static Shader Unlit()
        {
            if (_unlit != null) return _unlit;
            foreach (var n in new[] { "Unlit/Texture", "Legacy Shaders/Diffuse", "UI/Default", "Sprites/Default" })
            {
                _unlit = Shader.Find(n);
                if (_unlit != null) { InspectorPlugin.Log.LogInfo("Materials: channel view uses shader " + n); return _unlit; }
            }
            InspectorPlugin.Log.LogWarning("Materials: no unlit shader found for the channel view");
            return null;
        }

        Material ViewMat(Texture t)
        {
            var sh = Unlit();
            if (sh == null) return null;
            if (t == null)
            {
                if (_viewBlank == null) _viewBlank = new Material(sh) { name = "COD2EFT_view_none", color = new Color(0.25f, 0.0f, 0.25f), hideFlags = HideFlags.HideAndDontSave };
                return _viewBlank;
            }
            Material vm;
            if (!_viewMats.TryGetValue(t, out vm) || vm == null)
                _viewMats[t] = vm = new Material(sh) { name = "COD2EFT_view_" + t.name, mainTexture = t, color = Color.white, hideFlags = HideFlags.HideAndDontSave };
            return vm;
        }

        /// <summary>Shows one texture slot on these renderers (null = back to normal). Slots a material lacks show purple.</summary>
        public void SetView(string prop, IEnumerable<Renderer> renderers)
        {
            ClearView();
            View = prop;
            if (prop == null) return;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                _viewSaved[r] = mats;
                r.sharedMaterials = mats.Select(m => ViewMat(m != null && m.HasProperty(prop) ? m.GetTexture(prop) : null)).ToArray();
            }
            InspectorPlugin.Log.LogInfo($"Materials: channel view {prop} on {_viewSaved.Count} renderer(s)");
        }

        public Material[] Saved(Renderer r) { Material[] m; return _viewSaved.TryGetValue(r, out m) ? m : null; }

        public void ClearView()
        {
            foreach (var kv in _viewSaved) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            _viewSaved.Clear();
            View = null;
        }

        /// <summary>The texture slots the given materials have (for the view buttons), most common first.</summary>
        public static List<string> TextureSlots(IEnumerable<Material> mats)
        {
            var count = new Dictionary<string, int>();
            foreach (var m in mats)
                foreach (var p in Props(m).Where(p => p.Type == ShaderPropertyType.Texture))
                    if (m.GetTexture(p.Name) != null) { int n; count.TryGetValue(p.Name, out n); count[p.Name] = n + 1; }
            return count.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).Take(6).ToList();
        }

        public static string Pretty(string prop)
        {
            switch (prop)
            {
                case "_MainTex": return "Colour";
                case "_BumpMap": return "Normal";
                case "_SpecMap": return "Specular";
                case "_GlossMap": return "Gloss";
                default: return prop.TrimStart('_');
            }
        }

        public void WriteFile(string path, string text) => File.WriteAllText(path, text);
    }
}
