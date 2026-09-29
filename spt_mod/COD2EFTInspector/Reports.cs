// Text files written next to the screenshots: what is equipped (and from which bundle), what was hidden,
// and the material report (every renderer -> material -> shader, textures and values, as the game loaded them).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace COD2EFTInspector
{
    internal static class Reports
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // the values the COD2EFT pipeline sets (docs/PROJECT_CONTEXT.md, "Facts"); always listed, even when missing
        static readonly string[] KeyProps = { "_Glossness", "_Specularness", "_ReflectColor", "_SpecVals", "_DefVals", "_Cutoff", "_StencilType" };

        static string F(float v) => v.ToString("0.####", Inv);
        static string C(Color c) => $"({F(c.r)}, {F(c.g)}, {F(c.b)}, {F(c.a)})";
        static string V(Vector4 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)}, {F(v.w)})";

        /// <summary>Loaded AssetBundles that contain a prefab with this name (lower-case match on the file name).</summary>
        public static List<string> BundlesWith(ICollection<string> prefabNames)
        {
            var hits = new List<string>();
            var want = new HashSet<string>(prefabNames.Select(n => n.ToLowerInvariant()));
            if (want.Count == 0) return hits;
            try
            {
                foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (b == null) continue;
                    string[] assets;
                    try { assets = b.GetAllAssetNames(); } catch { continue; }
                    foreach (var a in assets)
                    {
                        if (!a.EndsWith(".prefab")) continue;
                        var n = System.IO.Path.GetFileNameWithoutExtension(a);
                        if (want.Contains(n)) hits.Add($"{n} <- bundle '{b.name}' ({a})");
                    }
                }
            }
            catch (Exception e) { hits.Add("bundle lookup failed: " + e.Message); }
            return hits;
        }

        static void Header(StringBuilder sb, string title, Scan scan)
        {
            sb.AppendLine($"COD2EFT Inspector v{InspectorPlugin.Version} - {title}");
            sb.AppendLine($"Time: {DateTime.Now:s}   Game: {Application.version}   Scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
            sb.AppendLine($"Character: {scan?.Target?.Label ?? "(none found)"}");
            var cam = Camera.main;
            if (cam != null) sb.AppendLine($"Camera: {cam.name} pos {cam.transform.position} rot {cam.transform.eulerAngles} fov {F(cam.fieldOfView)}");
            sb.AppendLine();
        }

        /// <summary>Catalog entries for a worn body part: by profile customization id of that part, else by bundle file name.</summary>
        static IEnumerable<Outfit> CatalogMatches(Group g, List<string> cust)
        {
            var cat = InspectorPlugin.Cat;
            if (cat == null) return Enumerable.Empty<Outfit>();
            string part = Catalog.PartOfBodyKey(g.Part);
            var byId = cust.Where(l => l.StartsWith(g.Part + " = ", StringComparison.Ordinal))
                           .Select(l => cat.ById(l.Substring(g.Part.Length + 3))).Where(o => o != null).ToList();
            return byId.Count > 0 ? byId : cat.ByBundleStem(g.Source).Where(o => o.Part == part);
        }

        static void Outfit(StringBuilder sb, Scan scan)
        {
            sb.AppendLine("Profile customization (ids):");
            var cust = scan?.Target?.Player != null ? Game.Customization(scan.Target.Player) : new List<string>();
            if (cust.Count == 0) sb.AppendLine("  not available (menu preview, or Profile.Customization not found)");
            foreach (var c in cust) sb.AppendLine("  " + c);
            sb.AppendLine();
            sb.AppendLine("Body parts on the character (PlayerBody.BodySkins) and the bundles they came from:");
            if (scan == null || !scan.Body.Any()) sb.AppendLine("  none found");
            else
            {
                var bundles = BundlesWith(scan.Body.Select(g => g.Source).ToList());
                foreach (var g in scan.Body)
                {
                    sb.AppendLine($"  {BodyScan.PartLabel(g.Part),-22} {g.Source}   ({g.Entries.Count} renderers)");
                    foreach (var o in CatalogMatches(g, cust)) sb.AppendLine("      catalog: " + o);
                    foreach (var b in bundles.Where(b => b.StartsWith(g.Source.ToLowerInvariant() + " <-"))) sb.AppendLine("      " + b);
                }
                if (bundles.Count == 0) sb.AppendLine("  (no loaded bundle has a prefab with these names)");
            }
            sb.AppendLine();
        }

        public static string ScreenshotInfo(Scan scan, string png, int supersize, int w, int h, ICollection<Renderer> hidden)
        {
            var sb = new StringBuilder();
            Header(sb, "screenshot " + System.IO.Path.GetFileName(png), scan);
            sb.AppendLine($"Image: {w} x {h} (supersize {supersize})");
            sb.AppendLine();
            Outfit(sb, scan);
            sb.AppendLine("Hidden meshes:");
            int n = 0;
            if (scan != null)
                foreach (var g in scan.Groups)
                    foreach (var e in g.Entries.Where(e => e.R != null && hidden.Contains(e.R)))
                    { sb.AppendLine($"  [{g.Name}] {e.Path}"); n++; }
            if (n == 0) sb.AppendLine("  none");
            return sb.ToString();
        }

        public static string Materials(Scan scan)
        {
            var sb = new StringBuilder();
            Header(sb, "material report", scan);
            Outfit(sb, scan);
            if (scan == null) return sb.ToString();
            var seenMats = new HashSet<Material>();
            foreach (var g in scan.Groups)
            {
                sb.AppendLine($"=== {g.Name}  [{g.Kind}]");
                foreach (var e in g.Entries)
                {
                    var r = e.R;
                    if (r == null) { sb.AppendLine($"  {e.Path}: (destroyed)"); continue; }
                    string mesh = "";
                    var smr = r as SkinnedMeshRenderer;
                    var mf = smr == null ? r.GetComponent<MeshFilter>() : null;
                    if (smr != null && smr.sharedMesh != null)
                        mesh = $" mesh '{smr.sharedMesh.name}' {smr.sharedMesh.vertexCount} verts, {smr.bones?.Length ?? 0} bones";
                    else if (mf != null && mf.sharedMesh != null)
                        mesh = $" mesh '{mf.sharedMesh.name}' {mf.sharedMesh.vertexCount} verts";
                    sb.AppendLine($"  {e.Path}  <{r.GetType().Name}> active={r.gameObject.activeInHierarchy} enabled={r.enabled} " +
                                  $"shadows={r.shadowCastingMode}{mesh}");
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) { sb.AppendLine($"    [{i}] (no material)"); continue; }
                        var sh = m.shader;
                        sb.AppendLine($"    [{i}] material '{m.name}'  shader '{(sh != null ? sh.name : "null")}'  queue {m.renderQueue}" +
                                      (m.shaderKeywords.Length > 0 ? "  keywords " + string.Join(" ", m.shaderKeywords) : ""));
                        if (!seenMats.Add(m)) { sb.AppendLine("        (same material as listed above)"); continue; }
                        foreach (var p in KeyProps)
                            if (!m.HasProperty(p)) sb.AppendLine($"        {p,-16} (not on this material)");
                        if (sh == null) continue;
                        for (int k = 0; k < sh.GetPropertyCount(); k++)
                        {
                            string pn = sh.GetPropertyName(k);
                            string val;
                            try
                            {
                                switch (sh.GetPropertyType(k))
                                {
                                    case ShaderPropertyType.Texture:
                                        var tex = m.GetTexture(pn);
                                        val = tex == null ? "none" : $"'{tex.name}' {tex.width}x{tex.height}" +
                                              (tex is Texture2D ? $" {((Texture2D)tex).format} mips {((Texture2D)tex).mipmapCount}" : $" <{tex.GetType().Name}>") +
                                              $" filter {tex.filterMode} scale {m.GetTextureScale(pn)} offset {m.GetTextureOffset(pn)}";
                                        break;
                                    case ShaderPropertyType.Color: val = "color " + C(m.GetColor(pn)); break;
                                    case ShaderPropertyType.Vector: val = "vector " + V(m.GetVector(pn)); break;
                                    default: val = F(m.GetFloat(pn)); break;
                                }
                            }
                            catch (Exception ex) { val = "(read failed: " + ex.Message + ")"; }
                            sb.AppendLine($"        {(KeyProps.Contains(pn) ? "*" : " ")}{pn,-16} {val}");
                        }
                    }
                }
                sb.AppendLine();
            }
            sb.AppendLine("(* = a value the COD2EFT pipeline sets)");
            return sb.ToString();
        }
    }
}
