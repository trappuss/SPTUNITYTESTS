// EFT Material Fixer - pure logic (no UnityEngine types, unit-testable outside Unity).
//
// Normal-map convention detection: a tangent-space normal map baked from a height field is (nearly) curl-free
// when read with the right green-channel convention, and has large curl when read with the wrong one.
// Checked 2026-09-27: vanilla EFT normal maps (Wild_Feet_2_n, wildman01head_n, wildman_hats_n) all read as OpenGL
// with a ~2x margin; the kleo _n.png files re-exported as "DirectX" read as DirectX (1.3-1.8x), and the older kleo
// head normal that looked right in game reads as OpenGL (1.65x).

using System;
using System.Collections.Generic;
using System.Linq;

namespace EFTAutoPrefab
{
    public enum NormalConvention { Unknown, OpenGL, DirectX }
    public enum TexRole { Albedo, Normal, Gloss, Roughness }

    public enum ShaderMode { EFT, Standard }

    /// <summary>Vanilla material values for one EFT shader use. Colors/vectors are RGBA / XYZW.</summary>
    public sealed class EftPreset
    {
        public string Label;
        public string ShaderName;
        public long ShaderPathId;          // path ID inside the game's "shaders" bundle (CAB-56d919bd...)
        public float StencilType;
        public float Glossness = 1f, Specularness = 1f, BumpTiling = 1f, DecalPower = 0f;
        public float[] Color = { 1, 1, 1, 1 };
        public float[] SpecColor = { 1, 1, 1, 1 };
        public float[] ReflectColor, SpecVals, DefVals, Temperature;
        public bool Cutout;

        public EftPreset Clone()
        {
            var c = (EftPreset)MemberwiseClone();
            c.Color = (float[])Color?.Clone(); c.SpecColor = (float[])SpecColor?.Clone();
            c.ReflectColor = (float[])ReflectColor?.Clone(); c.SpecVals = (float[])SpecVals?.Clone();
            c.DefVals = (float[])DefVals?.Clone(); c.Temperature = (float[])Temperature?.Clone();
            return c;
        }
    }

    public static class EFTMaterialCore
    {
        // ------------------------------------------------------------ EFT shaders (evidence, SPT 4.1 game files, 2026-09-27)
        // Materials of all 273 vanilla character prefab bundles: 268 use p0/Reflective/Bumped Specular SMap_Decal,
        // hair uses p0/Cutout/Bumped Diffuse. 170 materials in the 108 vanilla hands bundles use
        // p0/Reflective/Bumped Specular SMap, 162 of them with _StencilType 2 and no keywords.
        // Values below: median per part over the vanilla materials (textures measured only where the meshes' UVs cover them);
        // _Temperature is the most common value.
        // The game renders in Gamma colour space (globalgamemanagers m_ActiveColorSpace = 0) and deferred
        // (GraphicsSettings m_Deferred mode 2 -> Hidden/Internal-DeferredShadingEFT, decompiled 2026-09-27: Unity's standard
        // BRDF1 = GGX + Smith-joint visibility + Disney diffuse, gamma-space variant; plus a thermal-light special case).
        // Character shader deferred pass (SDK decompiled stub):
        //   G-buffer specular colour = _MainTex.a * _Glossness * (_SpecVals.x + _SpecVals.y * F) / 2 * _SpecColor
        //   G-buffer smoothness      = _SpecMap.r * _Specularness            (GGX roughness = (1 - smoothness)^2)
        //   albedo                   = _MainTex.rgb * _Color * (_DefVals.x + _DefVals.y * F)
        //   reflection (emission)    = _MainTex.a * cube * _ReflectColor * (_SpecVals.x + _SpecVals.y * F) / 2
        //   F = (1 - N.V)^2 / 2. (The forward pass's Blinn-Phong x128 exponent only applies to forward-rendered objects.)

        public const string ShadersCab = "CAB-56d919bd5479d38f741da52a6beef92f";
        public const string CubemapsCab = "CAB-4d8a4131cf377709ee7c7e960f65d349";
        public const long SMapDecalPathId = 5560642222599436764;   // p0/Reflective/Bumped Specular SMap_Decal
        public const long SMapPathId = 6014991791773097075;        // p0/Reflective/Bumped Specular SMap
        public const long CutoutPathId = -5942632167267530218;     // p0/Cutout/Bumped Diffuse
        public const long CubeMattePathId = 972550011776207695;    // patron_cubemap_metall_matte (242 of 275 vanilla _Cube refs)
        public const string CubeMatteGuid = "757b7b0306d602641ae068706a1a2d26"; // its SDK stub

        public static readonly EftPreset Upper = new EftPreset
        {
            Label = "top", ShaderName = "p0/Reflective/Bumped Specular SMap_Decal", ShaderPathId = SMapDecalPathId, StencilType = 1,
            SpecVals = new float[] { 1.1f, 2, 0, 0 }, DefVals = new float[] { 0.85f, 0.7f, 0, 0 },
            ReflectColor = new float[] { 0.358f, 0.358f, 0.358f, 0.5f }, Temperature = new float[] { 0.1f, 1, 1, 0 },
        };
        public static readonly EftPreset Lower = new EftPreset
        {
            Label = "pants", ShaderName = Upper.ShaderName, ShaderPathId = SMapDecalPathId, StencilType = 1,
            SpecVals = new float[] { 1.1f, 2, 0, 0 }, DefVals = new float[] { 0.85f, 0.7f, 0, 0 },
            ReflectColor = new float[] { 0.255f, 0.255f, 0.255f, 0.5f }, Temperature = new float[] { 0.1f, 1, 1, 0 },
        };
        public static readonly EftPreset Head = new EftPreset
        {
            Label = "head", ShaderName = Upper.ShaderName, ShaderPathId = SMapDecalPathId, StencilType = 1,
            SpecVals = new float[] { 1.0f, 3, 0, 0 }, DefVals = new float[] { 0.8f, 1.0f, 0, 0 },
            ReflectColor = new float[] { 0.302f, 0.302f, 0.302f, 0.5f }, Temperature = new float[] { 0f, 1, 2, 0 },
        };
        public static readonly EftPreset Hands = new EftPreset
        {
            Label = "hands", ShaderName = "p0/Reflective/Bumped Specular SMap", ShaderPathId = SMapPathId, StencilType = 2,
            SpecVals = new float[] { 1.1f, 2, 0, 0 }, DefVals = new float[] { 0.8f, 0.7f, 0, 0 },
            ReflectColor = new float[] { 0.198f, 0.198f, 0.198f, 0.5f }, Temperature = new float[] { 0.1f, 1, 2, 0 },
        };

        // ------------------------------------------------------------ COD2EFT 2.4+ textures (PNG tEXt Software = "COD2EFT enc=2")
        // _d alpha = COD specular reflectance F0 (dielectrics 0.04), _g = COD gloss (see SPTModdingTools\COD2EFT\unity\COD2EFT_TEXTURE_SPEC.md).
        // Used as stored; the material values map them onto what vanilla puts into the G-buffer (medians over UV-covered pixels):
        //   vanilla specular at F=0 (a * _Glossness * SpecVals.x/2): top 0.052, pants 0.045, head 0.044, hands 0.057
        //   vanilla smoothness (_SpecMap * _Specularness):            top 0.24,  pants 0.18,  head 0.29,  hands 0.27
        //   vanilla reflection (a * _ReflectColor * SpecVals.x/2):    top 0.019, pants 0.013, head 0.018, hands 0.012
        //   COD: dielectric F0 0.04; gloss medians cloth 0.24, skin 0.48-0.56
        //   => _Glossness = spec / (0.04 * SpecVals.x/2), _ReflectColor = refl / (0.04 * SpecVals.x/2), _Specularness = smooth / COD gloss.
        // Hands' _Specularness assumes hands textures are mostly cloth/gloves (no COD hands gloss measured yet) - a hunch until
        // checked in game. All of these are statistical matches; the in-game side-by-side is the final check.
        public const string Cod2EftTagPrefix = "COD2EFT enc=";

        public static EftPreset Cod2EftPreset(BodyPart part)
        {
            EftPreset p;
            switch (part)
            {
                case BodyPart.Head: p = Head.Clone(); p.Glossness = 2.2f; p.Specularness = 0.6f; p.ReflectColor = new[] { 0.9f, 0.9f, 0.9f, 0.5f }; break;
                case BodyPart.Lower: p = Lower.Clone(); p.Glossness = 2.0f; p.Specularness = 0.75f; p.ReflectColor = new[] { 0.61f, 0.61f, 0.61f, 0.5f }; break;
                case BodyPart.Hands: p = Hands.Clone(); p.Glossness = 2.6f; p.Specularness = 0.8f; p.ReflectColor = new[] { 0.55f, 0.55f, 0.55f, 0.5f }; break;
                default: p = Upper.Clone(); p.Glossness = 2.4f; p.Specularness = 1.0f; p.ReflectColor = new[] { 0.87f, 0.87f, 0.87f, 0.5f }; break;
            }
            p.Label += " (COD2EFT)";
            return p;
        }

        /// <summary>
        /// The COD2EFT encoding number from a PNG's first bytes (tEXt "Software" = "COD2EFT enc=N" before the image data),
        /// 0 when the file is not a tagged COD2EFT PNG.
        /// </summary>
        public static int Cod2EftEncoding(byte[] head)
        {
            if (head == null || head.Length < 16) return 0;
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < 8; i++) if (head[i] != sig[i]) return 0;
            int p = 8;
            while (p + 8 <= head.Length)
            {
                long len = ((long)head[p] << 24) | ((long)head[p + 1] << 16) | ((long)head[p + 2] << 8) | head[p + 3];
                string type = System.Text.Encoding.ASCII.GetString(head, p + 4, 4);
                if (type == "IDAT" || type == "IEND") return 0;
                if (type == "tEXt" && p + 8 + len <= head.Length)
                {
                    int start = p + 8, end = start + (int)len;
                    int zero = Array.IndexOf(head, (byte)0, start, (int)len);
                    if (zero > start)
                    {
                        string key = Latin1(head, start, zero - start);
                        string val = Latin1(head, zero + 1, end - zero - 1);
                        if (key == "Software" && val.StartsWith(Cod2EftTagPrefix) &&
                            int.TryParse(val.Substring(Cod2EftTagPrefix.Length).Trim(), out int enc)) return enc;
                    }
                }
                p += 12 + (int)len;
            }
            return 0;
        }

        static string Latin1(byte[] b, int start, int count)
        {
            var c = new char[count];
            for (int i = 0; i < count; i++) c[i] = (char)b[start + i];
            return new string(c);
        }

        /// <summary>COD2EFT's cut-out material slots are named &lt;name&gt;_&lt;Part&gt;_alpha.</summary>
        public static bool IsCod2EftCutoutSlot(string materialName) =>
            (materialName ?? "").Trim().EndsWith("_alpha", StringComparison.OrdinalIgnoreCase);
        /// <summary>Only 3 vanilla hair materials exist; stencil/defvals/temperature are their majority value, SpecVals the shader default.</summary>
        public static readonly EftPreset Hair = new EftPreset
        {
            Label = "cutout", ShaderName = "p0/Cutout/Bumped Diffuse", ShaderPathId = CutoutPathId, StencilType = 0, Cutout = true,
            SpecVals = new float[] { 0.35f, 2, 0, 0 }, DefVals = new float[] { 0.8f, 1.0f, 0, 0 },
            Temperature = new float[] { 0.1f, 0.3f, 0.5f, 0 },
        };

        public static EftPreset PresetFor(BodyPart part, bool cutout)
        {
            if (cutout) return Hair;
            switch (part)
            {
                case BodyPart.Head: return Head;
                case BodyPart.Lower: return Lower;
                case BodyPart.Hands: return Hands;
                default: return Upper;
            }
        }

        /// <summary>
        /// The body part a material is set up for. Only hands use it: Hands (set up as a hands material directly).
        /// Shared with body parts: hands are ignored (they get a hands variant when the hands prefab is built), then Head
        /// wins, then the most common part; Upper when nothing is known.
        /// </summary>
        public static BodyPart InferPart(IEnumerable<BodyPart> usedBy)
        {
            var parts = (usedBy ?? Enumerable.Empty<BodyPart>()).Where(p => p != BodyPart.Ignore).ToList();
            var body = parts.Where(p => p != BodyPart.Hands).ToList();
            if (body.Count == 0) return parts.Count > 0 ? BodyPart.Hands : BodyPart.Upper;
            if (body.Contains(BodyPart.Head)) return BodyPart.Head;
            int up = body.Count(p => p == BodyPart.Upper), low = body.Count(p => p == BodyPart.Lower);
            return low > up ? BodyPart.Lower : BodyPart.Upper;
        }

        // Specular mask for _MainTex alpha. Vanilla albedo alpha averages 0.04-0.28 and follows the gloss map
        // (per-material correlation median ~0.73 over 44 vanilla materials; median fit alpha = 0.41*gloss + 0.03).
        public const float DefaultMaskSlope = 0.4f;
        public const float DefaultMaskOffset = 0.03f;
        /// <summary>Used when there is no gloss map: typical vanilla albedo-alpha mean (clothing ~0.1).</summary>
        public const float DefaultMaskConstant = 0.1f;
        /// <summary>_Specularness when no gloss map exists (_SpecMap stays white): typical vanilla gloss-map mean.</summary>
        public const float NoGlossSpecularness = 0.2f;

        public static byte SpecMask(byte gloss, float slope, float offset)
        {
            double v = slope * (gloss / 255.0) + offset;
            if (v < 0) v = 0; if (v > 1) v = 1;
            return (byte)Math.Round(v * 255.0);
        }

        public static readonly Dictionary<TexRole, string> DefaultSuffixes = new Dictionary<TexRole, string>
        {
            { TexRole.Albedo, "_d, _c, _col, _color, _albedo, _diffuse, _basecolor" },
            { TexRole.Normal, "_n, _nml, _nrm, _normal" },
            { TexRole.Gloss, "_g, _gloss, _glossiness, _smoothness" },
            { TexRole.Roughness, "_r, _rough, _roughness" },
        };

        public const string DefaultCutoutKeywords = "alpha, hair, lash, brow, fur, cutout";

        /// <summary>Minimum curl ratio before the detector trusts its answer.</summary>
        public const double MinConfidence = 1.15;

        public static string[] Split(string csv) =>
            (csv ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct()
                       .OrderByDescending(s => s.Length).ToArray();

        /// <summary>"mp_x_Head_d" with suffixes {_d,...} -> "mp_x_Head"; null when no suffix matches.</summary>
        public static string StripSuffix(string texName, string[] suffixes)
        {
            if (string.IsNullOrEmpty(texName)) return null;
            string lower = texName.ToLowerInvariant();
            foreach (var s in suffixes)
                if (lower.EndsWith(s) && texName.Length > s.Length) return texName.Substring(0, texName.Length - s.Length);
            return null;
        }

        /// <summary>Exact (case-insensitive) match of base + one of the suffixes.</summary>
        public static string FindTexture(IEnumerable<string> baseNames, IEnumerable<string> textureNames, string[] suffixes)
        {
            var names = textureNames.ToList();
            foreach (var b in baseNames.Where(x => !string.IsNullOrEmpty(x)))
                foreach (var s in suffixes)
                {
                    string want = (b + s).ToLowerInvariant();
                    var hit = names.FirstOrDefault(n => n.ToLowerInvariant() == want);
                    if (hit != null) return hit;
                }
            return null;
        }

        static readonly System.Text.RegularExpressions.Regex WordRx =
            new System.Text.RegularExpressions.Regex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Words of a name: split on '_', digits etc. and camelCase ("mp_HairCards_d" -> mp, hair, cards, d).</summary>
        public static string[] Words(string name) =>
            WordRx.Matches(name ?? "").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.ToLowerInvariant()).ToArray();

        /// <summary>
        /// True when a whole word of the material or albedo name is a keyword, its plural, or "eye"+keyword
        /// ("hair", "Hairs", "eyelashes", "EyeBrow"), so "brown", "flash" or "sulfur" do not count.
        /// </summary>
        public static bool WantsCutout(string materialName, string albedoName, string[] keywords)
        {
            var words = Words(materialName).Concat(Words(albedoName)).ToList();
            return keywords.Any(k => words.Any(w => w == k || w == k + "s" || w == k + "es" || w == "eye" + k || w == "eye" + k + "s" || w == "eye" + k + "es"));
        }

        /// <summary>
        /// rgba: 4 bytes per pixel, rows in memory order. bottomUp = row 0 is the bottom of the image (Unity
        /// Texture2D/GetPixels32 order); false = row 0 is the top (PNG/file order). X = R channel, Y = G channel.
        /// </summary>
        public static NormalConvention DetectNormalConvention(byte[] rgba, int width, int height, bool bottomUp,
                                                               out double ratio)
        {
            ratio = 1;
            if (rgba == null || width < 3 || height < 3 || rgba.Length < width * height * 4) return NormalConvention.Unknown;
            double gl = 0, dx = 0;
            long n = 0;
            for (int r = 0; r < height - 1; r++)
            {
                int row = r * width * 4, next = (r + 1) * width * 4;
                for (int c = 0; c < width - 1; c++)
                {
                    // 2x2 cell-centred differences, so the result does not depend on which row is stored first
                    int i00 = row + c * 4, i01 = i00 + 4, i10 = next + c * 4, i11 = i10 + 4;
                    double da = ((rgba[i10] - rgba[i00]) + (rgba[i11] - rgba[i01])) / 255.0;           // d(X)/d(row index)
                    double db = ((rgba[i01 + 1] - rgba[i00 + 1]) + (rgba[i11 + 1] - rgba[i10 + 1])) / 255.0; // d(Y)/d(column)
                    if (bottomUp) da = -da;          // convert to "row increases downward"
                    gl += (da + db) * (da + db);
                    dx += (da - db) * (da - db);
                    n++;
                }
            }
            gl /= n; dx /= n;
            double lo = Math.Min(gl, dx), hi = Math.Max(gl, dx);
            if (hi < 1e-7) { ratio = 1; return NormalConvention.Unknown; } // flat map
            ratio = hi / Math.Max(lo, 1e-12);
            if (ratio < MinConfidence) return NormalConvention.Unknown;
            return gl < dx ? NormalConvention.OpenGL : NormalConvention.DirectX;
        }
    }
}
