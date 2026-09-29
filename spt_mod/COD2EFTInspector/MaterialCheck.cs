// Structural checks on the materials the game actually loaded (no Unity types: unit-tested with mono).
// Expected values are the established facts in docs/PROJECT_CONTEXT.md ("Game and rendering"):
//   body parts: p0/Reflective/Bumped Specular SMap_Decal, _StencilType 1; first-person hands: ...SMap, _StencilType 2;
//   hair / alpha: p0/Cutout/Bumped Diffuse. Reflective shaders sample _MainTex, _BumpMap, _SpecMap.
// Material *values* (_Glossness etc.) are not judged here: they depend on the encoding (enc=2 / enc=3).
using System;
using System.Collections.Generic;
using System.Linq;

namespace COD2EFTInspector
{
    internal sealed class TexInfo
    {
        public string Name; public int W, H;
    }

    internal sealed class MatInfo
    {
        public string Part;          // Top / Pants / Head / Hands (catalog names) or "" for gear
        public string Renderer, Material, Shader;
        public bool NoMaterial, NoMesh, Skinned;
        public int Bones;
        public Dictionary<string, float> Floats = new Dictionary<string, float>();
        public Dictionary<string, TexInfo> Textures = new Dictionary<string, TexInfo>();   // null value = slot empty
    }

    internal static class MaterialCheck
    {
        public const string BodyShader = "p0/Reflective/Bumped Specular SMap_Decal";
        public const string HandsShader = "p0/Reflective/Bumped Specular SMap";
        public const string CutoutShader = "p0/Cutout/Bumped Diffuse";

        static bool Pow2(int v) => v > 0 && (v & (v - 1)) == 0;

        /// <summary>Problems as "ERROR ..." / "WARN ..." lines (distinct), empty when all is fine.</summary>
        public static List<string> Run(IEnumerable<MatInfo> mats)
        {
            var out_ = new List<string>();
            foreach (var m in mats)
            {
                string where = $"[{(string.IsNullOrEmpty(m.Part) ? "gear" : m.Part)}] {m.Renderer}";
                if (m.NoMesh) out_.Add($"ERROR {where}: no mesh");
                if (m.Skinned && !m.NoMesh && m.Bones == 0) out_.Add($"WARN  {where}: skinned mesh with 0 bones (won't follow the body)");
                if (m.NoMaterial) { out_.Add($"ERROR {where}: a material slot is empty"); continue; }
                if (string.IsNullOrEmpty(m.Part)) continue;   // gear: only the basic checks above
                string what = $"{where} material '{m.Material}'";
                string sh = m.Shader ?? "";
                if (sh.Length == 0 || sh.IndexOf("InternalErrorShader", StringComparison.OrdinalIgnoreCase) >= 0)
                { out_.Add($"ERROR {what}: shader missing (draws pink) - the shaders bundle wasn't a dependency"); continue; }
                if (!sh.StartsWith("p0/", StringComparison.Ordinal))
                { out_.Add($"ERROR {what}: shader '{sh}' is not an EFT shader (e.g. Standard left in)"); continue; }

                bool cutout = sh == CutoutShader;
                bool hands = m.Part == "Hands";
                if (!cutout)
                {
                    string want = hands ? HandsShader : BodyShader;
                    if (sh != want) out_.Add($"WARN  {what}: shader '{sh}', expected '{want}'");
                    float st;
                    int wantStencil = hands ? 2 : 1;
                    if (m.Floats.TryGetValue("_StencilType", out st) && Math.Abs(st - wantStencil) > 0.01f)
                        out_.Add($"WARN  {what}: _StencilType {st}, expected {wantStencil}");
                }
                var need = cutout ? new[] { "_MainTex", "_BumpMap" } : new[] { "_MainTex", "_BumpMap", "_SpecMap" };
                foreach (var slot in need)
                {
                    TexInfo t;
                    if (!m.Textures.TryGetValue(slot, out t) || t == null) out_.Add($"ERROR {what}: {slot} is empty");
                }
                foreach (var kv in m.Textures.Where(kv => kv.Value != null && need.Contains(kv.Key)))
                {
                    var t = kv.Value;
                    if (!Pow2(t.W) || !Pow2(t.H)) out_.Add($"WARN  {what}: {kv.Key} '{t.Name}' is {t.W}x{t.H} (not a power of two)");
                    if (t.W > 4096 || t.H > 4096) out_.Add($"WARN  {what}: {kv.Key} '{t.Name}' is {t.W}x{t.H} (over 4096)");
                    string n = (t.Name ?? "").ToLowerInvariant();
                    if (n == "white" || n == "grey" || n == "gray" || n == "black" || n == "bump" || n.StartsWith("unity_default"))
                        out_.Add($"ERROR {what}: {kv.Key} is Unity's default '{t.Name}' (texture not assigned in Unity)");
                }
            }
            return out_.Distinct().ToList();
        }
    }
}
