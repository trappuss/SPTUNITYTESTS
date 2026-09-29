// EFT Material Fixer - sets up materials for EFT clothing/heads/hands automatically:
//   1. extracts materials embedded in the FBX (so they can be edited; same as Materials tab > Extract Materials),
//   2. finds each material's textures by name suffix (_d albedo, _n normal, _g gloss / _r roughness),
//   3. normal maps: marks them "Normal map", detects OpenGL vs DirectX and sets Flip Green Channel, assigns them,
//   4a. EFT mode (default): the game's own shaders through the SDK's imposter stubs, with vanilla values per part
//       (see EFTMaterialCore for the evidence). Opaque: p0/Reflective/Bumped Specular SMap_Decal, _SpecMap = gloss,
//       albedo alpha = specular mask (written to <albedo>_eft.png, the original is never changed).
//       Cutout (hair/lashes/...): p0/Cutout/Bumped Diffuse (a stub is created once if the SDK has none).
//       Hands get a separate <material>_hands.mat (Bumped Specular SMap, _StencilType 2), made by HandsVariant().
//   4b. Standard mode: Standard shader, gloss/roughness packed into <base>_ms.png, Cutout/Opaque by keyword.
// Used by the EFT Auto Prefabber (Fix Materials button, and automatically on Scan when enabled).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTAutoPrefab
{
    public enum NormalMode { AutoDetect, OpenGL, DirectX }

    public class MaterialFixOptions
    {
        public ShaderMode Mode = ShaderMode.EFT;
        public bool ReapplyValues;           // EFT mode: re-apply vanilla values to materials already on EFT shaders
        public float MaskSlope = EFTMaterialCore.DefaultMaskSlope;
        public float MaskOffset = EFTMaterialCore.DefaultMaskOffset;
        /// <summary>Part words used to tell which body part a material belongs to (from its renderers' names).</summary>
        public IDictionary<BodyPart, string> Aliases = EFTAutoPrefabCore.DefaultAliases;
        public bool Extract = true;
        public NormalMode Normals = NormalMode.AutoDetect;
        public bool UseGloss = true;
        public string CutoutKeywords = EFTMaterialCore.DefaultCutoutKeywords;
        public float Cutoff = 0.5f;
        public Dictionary<TexRole, string> Suffixes = new Dictionary<TexRole, string>(EFTMaterialCore.DefaultSuffixes);
    }

    public static class EFTMaterialFixer
    {
        public static List<string> Fix(IEnumerable<GameObject> roots, MaterialFixOptions o)
        {
            var log = new List<string>();
            var rootList = roots.Where(r => r != null).ToList();
            var renderers = rootList.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).Distinct().ToList();
            if (renderers.Count == 0) { log.Add("Materials: no renderers under the selection."); return log; }

            // ---------------------------------------------------------------- 1. extract embedded materials
            if (o.Extract)
            {
                // remember scene renderers' slots by material name: their references die when the model reimports
                var sceneSlots = renderers.Where(r => !EditorUtility.IsPersistent(r))
                    .ToDictionary(r => r, r => r.sharedMaterials.Select(m => m != null ? m.name : null).ToArray());
                var extracted = new Dictionary<string, Material>();   // material name -> external material
                var touchedModels = new HashSet<string>();

                foreach (var mat in renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
                {
                    if (!AssetDatabase.IsSubAsset(mat)) continue;
                    string modelPath = AssetDatabase.GetAssetPath(mat);
                    if (!(AssetImporter.GetAtPath(modelPath) is ModelImporter mi)) continue;

                    string folder = (Path.GetDirectoryName(modelPath) ?? "Assets").Replace('\\', '/') + "/Materials";
                    EnsureFolder(folder);
                    string dest = folder + "/" + SafeFile(mat.name) + ".mat";
                    var existing = AssetDatabase.LoadAssetAtPath<Material>(dest);
                    if (existing != null)
                    {
                        mi.AddRemap(new AssetImporter.SourceAssetIdentifier(mat), existing);
                        log.Add($"Materials: '{mat.name}' -> using existing {dest}");
                    }
                    else
                    {
                        string err = AssetDatabase.ExtractAsset(mat, dest);
                        if (!string.IsNullOrEmpty(err)) { log.Add($"Materials: could not extract '{mat.name}': {err}"); continue; }
                        log.Add($"Materials: extracted '{mat.name}' -> {dest}");
                    }
                    touchedModels.Add(modelPath);
                    extracted[mat.name] = null; // filled after reimport
                }

                foreach (var mp in touchedModels)
                {
                    AssetDatabase.WriteImportSettingsIfDirty(mp);
                    AssetDatabase.ImportAsset(mp, ImportAssetOptions.ForceUpdate);
                }

                if (touchedModels.Count > 0)
                {
                    foreach (var name in extracted.Keys.ToList())
                    {
                        var m = touchedModels.Select(mp => AssetDatabase.LoadAssetAtPath<Material>(
                                    (Path.GetDirectoryName(mp) ?? "Assets").Replace('\\', '/') + "/Materials/" + SafeFile(name) + ".mat"))
                                .FirstOrDefault(x => x != null);
                        extracted[name] = m;
                    }
                    // re-point scene renderers (unpacked instances keep stale references otherwise)
                    foreach (var kv in sceneSlots)
                    {
                        var r = kv.Key;
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < kv.Value.Length && i < mats.Length; i++)
                        {
                            if (kv.Value[i] == null) continue;
                            if (extracted.TryGetValue(kv.Value[i], out var ext) && ext != null && mats[i] != ext)
                            { mats[i] = ext; changed = true; }
                        }
                        if (changed) { Undo.RecordObject(r, "Fix materials"); r.sharedMaterials = mats; EditorUtility.SetDirty(r); }
                    }
                    // re-collect renderers: model reimport may have replaced asset-side objects
                    renderers = rootList.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).Distinct().ToList();
                }
            }

            // ---------------------------------------------------------------- 2-5. set up each material
            var sAlb = EFTMaterialCore.Split(o.Suffixes[TexRole.Albedo]);
            var sNrm = EFTMaterialCore.Split(o.Suffixes[TexRole.Normal]);
            var sGls = EFTMaterialCore.Split(o.Suffixes[TexRole.Gloss]);
            var sRgh = EFTMaterialCore.Split(o.Suffixes[TexRole.Roughness]);
            var cutoutKeys = EFTMaterialCore.Split(o.CutoutKeywords);

            // which body parts use each material (from the renderers' object names)
            var partsOf = new Dictionary<Material, List<BodyPart>>();
            foreach (var r in renderers)
            {
                var part = EFTAutoPrefabCore.Parse(r.gameObject.name, o.Aliases).Part;
                foreach (var m in r.sharedMaterials.Where(m => m != null))
                {
                    if (!partsOf.TryGetValue(m, out var l)) partsOf[m] = l = new List<BodyPart>();
                    l.Add(part);
                }
            }

            try
            {
                foreach (var mat in renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
                {
                    try
                    {
                        string label = $"'{mat.name}'";
                        if (AssetDatabase.IsSubAsset(mat)) { log.Add($"Materials: {label} is still embedded in the model - enable 'Extract materials' or extract it manually; skipped."); continue; }
                        if (IsHandsVariant(mat)) { log.Add($"Materials: {label} is a generated hands material (rebuilt from its body material when hands prefabs are built); skipped."); continue; }

                        bool isStandard = mat.shader != null && mat.shader.name == "Standard";
                        long eftId = ImposterPathId(mat.shader);
                        bool isEft = eftId == EFTMaterialCore.SMapDecalPathId || eftId == EFTMaterialCore.SMapPathId || eftId == EFTMaterialCore.CutoutPathId;
                        if (o.Mode == ShaderMode.Standard && !isStandard) { log.Add($"Materials: {label} uses shader '{mat.shader?.name}', not Standard; skipped (Standard mode)."); continue; }
                        if (o.Mode == ShaderMode.EFT && !isStandard && !isEft) { log.Add($"Materials: {label} uses shader '{mat.shader?.name}' (neither Standard nor a known EFT shader); skipped."); continue; }

                        // candidate textures, nearest folder first: albedo's folder, material's folder, its parent (recursive)
                        var mainTex = mat.GetTexture("_MainTex");
                        var folders = new List<string>();
                        void AddFolder(string f) { if (!string.IsNullOrEmpty(f) && f.StartsWith("Assets") && !folders.Contains(f)) folders.Add(f); }
                        if (mainTex != null) AddFolder(Path.GetDirectoryName(AssetDatabase.GetAssetPath(mainTex))?.Replace('\\', '/'));
                        string matDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mat))?.Replace('\\', '/');
                        AddFolder(matDir);
                        string parentDir = string.IsNullOrEmpty(matDir) ? null : Path.GetDirectoryName(matDir)?.Replace('\\', '/');
                        if (parentDir != null && parentDir != "Assets") AddFolder(parentDir); // never search the whole project
                        var texPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        // textures directly in a candidate folder win over ones in its subfolders (FindAssets is recursive)
                        var found = folders.Where(AssetDatabase.IsValidFolder)
                            .Select(f => (f, paths: AssetDatabase.FindAssets("t:Texture2D", new[] { f }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(x => x).ToList()))
                            .ToList();
                        foreach (bool direct in new[] { true, false })
                            foreach (var (f, paths) in found)
                                foreach (var p in paths)
                                {
                                    bool isDirect = (Path.GetDirectoryName(p) ?? "").Replace('\\', '/') == f;
                                    if (isDirect != direct) continue;
                                    string n = Path.GetFileNameWithoutExtension(p);
                                    if (!texPaths.ContainsKey(n)) texPaths[n] = p;
                                }
                        foreach (var prop in new[] { "_MainTex", "_BumpMap", "_SpecMap" }) // textures already assigned always count
                        {
                            if (!mat.HasProperty(prop)) continue;
                            var t = mat.GetTexture(prop);
                            if (t != null && AssetDatabase.Contains(t)) texPaths[t.name] = AssetDatabase.GetAssetPath(t);
                        }

                        // a packed <albedo>_eft texture from an earlier run stands for its source albedo
                        if (mainTex != null && mainTex.name.EndsWith(PackedSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            string srcName = mainTex.name.Substring(0, mainTex.name.Length - PackedSuffix.Length);
                            var src = texPaths.TryGetValue(srcName, out var sp) ? AssetDatabase.LoadAssetAtPath<Texture2D>(sp) : null;
                            mainTex = src != null ? src : null;
                        }

                        var bases = new List<string> { mat.name };
                        if (mainTex != null) { var b = EFTMaterialCore.StripSuffix(mainTex.name, sAlb); if (b != null) bases.Add(b); }
                        var notes = new List<string>();

                        // albedo
                        string albName = mainTex != null ? mainTex.name : EFTMaterialCore.FindTexture(bases, texPaths.Keys, sAlb);
                        string albPath = albName != null && texPaths.TryGetValue(albName, out var ap) ? ap : (mainTex != null ? AssetDatabase.GetAssetPath(mainTex) : null);
                        if (mainTex == null && albPath != null)
                        {
                            mainTex = AssetDatabase.LoadAssetAtPath<Texture2D>(albPath);
                            notes.Add("albedo " + albName);
                        }
                        // COD2EFT 2.4+ texture sets (tagged PNGs): data used as stored, cut-out = the "_alpha" material slot
                        bool cod = Cod2EftEnc(albPath) >= 2;
                        bool cutout = EFTMaterialCore.IsCod2EftCutoutSlot(mat.name) ||
                                      (!cod && EFTMaterialCore.WantsCutout(mat.name, albName, cutoutKeys));
                        if (cod) notes.Add("COD2EFT enc=" + Cod2EftEnc(albPath) + " textures");
                        else if (albPath != null && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(albPath),
                                     @"_(Head|Upper|Lower|Hands)(_alpha)?_d\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            notes.Add("looks like COD2EFT output but has no COD2EFT 2.4 tag (2.3 or older: _d alpha is wrong on skin) - convert again with COD2EFT 2.4+");

                        // normal map (import type + OpenGL/DirectX), same for both modes
                        string nrmName = mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null ? mat.GetTexture("_BumpMap").name : EFTMaterialCore.FindTexture(bases, texPaths.Keys, sNrm);
                        Texture2D nrmTex = null;
                        if (nrmName != null && texPaths.TryGetValue(nrmName, out var nrmPath))
                        {
                            notes.Add("normal " + nrmName + " " + PrepareNormalMap(nrmPath, Cod2EftEnc(nrmPath) >= 2 ? NormalMode.OpenGL : o.Normals));
                            EnsureMaxSize(nrmPath);
                            nrmTex = AssetDatabase.LoadAssetAtPath<Texture2D>(nrmPath);
                        }
                        else notes.Add("no normal map found");

                        // a gloss map already in _SpecMap (EFT shaders) wins over name matching, unless it is our inverted roughness
                        var specTex = mat.HasProperty("_SpecMap") ? mat.GetTexture("_SpecMap") : null;
                        string glsName = specTex != null && AssetDatabase.Contains(specTex) && !specTex.name.EndsWith(PackedSuffix, StringComparison.OrdinalIgnoreCase)
                            ? specTex.name : EFTMaterialCore.FindTexture(bases, texPaths.Keys, sGls);
                        string rghName = glsName == null ? EFTMaterialCore.FindTexture(bases, texPaths.Keys, sRgh) : null;

                        if (o.Mode == ShaderMode.EFT)
                        {
                            if (!SetupEft(mat, mainTex, albPath, cutout, cod, nrmTex, glsName != null ? texPaths[glsName] : null,
                                          rghName != null ? texPaths[rghName] : null,
                                          EFTMaterialCore.InferPart(partsOf.TryGetValue(mat, out var pl) ? pl : null), o, notes))
                            { log.Add($"Materials: {label}: " + string.Join(", ", notes)); continue; }
                        }
                        else
                        {
                            if (mainTex != null && mat.GetTexture("_MainTex") != mainTex) mat.SetTexture("_MainTex", mainTex);
                            SetBlendMode(mat, cutout, o.Cutoff);
                            notes.Add(cutout ? $"Cutout {o.Cutoff:0.##}" : "Opaque");
                            SetAlphaIsTransparency(albPath, cutout);
                            if (nrmTex != null)
                            {
                                mat.SetTexture("_BumpMap", nrmTex);
                                mat.SetFloat("_BumpScale", 1f);
                                mat.EnableKeyword("_NORMALMAP");
                            }
                            // gloss / roughness -> metallic-smoothness
                            if (o.UseGloss)
                            {
                                string srcName = glsName ?? rghName;
                                if (srcName != null)
                                {
                                    string srcPath = texPaths[srcName];
                                    var baseName = EFTMaterialCore.StripSuffix(srcName, glsName != null ? sGls : sRgh) ?? srcName;
                                    string msPath = (Path.GetDirectoryName(srcPath) ?? "Assets").Replace('\\', '/') + "/" + baseName + "_ms.png";
                                    var ms = BuildMetallicSmoothness(srcPath, msPath, invert: glsName == null, notes);
                                    if (ms != null)
                                    {
                                        mat.SetTexture("_MetallicGlossMap", ms);
                                        mat.SetFloat("_SmoothnessTextureChannel", 0f);
                                        mat.SetFloat("_GlossMapScale", 1f);
                                        mat.EnableKeyword("_METALLICGLOSSMAP");
                                        mat.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
                                        notes.Add((glsName != null ? "gloss " : "roughness(inverted) ") + srcName);
                                    }
                                }
                            }
                        }
                        EditorUtility.SetDirty(mat);
                        log.Add($"Materials: '{mat.name}': " + string.Join(", ", notes));
                    }
                    catch (Exception e)
                    {
                        log.Add($"Materials: '{mat.name}' FAILED: {e.Message}");
                        Debug.LogException(e);
                    }
                }
            }
            finally { AssetDatabase.SaveAssets(); }
            return log;
        }

        // ================================================================== EFT shaders

        const string PackedSuffix = "_eft";
        const string PackStampKey = "eftpack:v1";
        const string HandsStampPrefix = "eft.handsVariantOf:";
        static readonly Dictionary<long, Shader> _imposterShaders = new Dictionary<long, Shader>();

        /// <summary>The imposter canonicalPathID of a shader asset (its .meta userData), 0 when it has none.</summary>
        public static long ImposterPathId(UnityEngine.Object asset)
        {
            if (asset == null) return 0;
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return 0;
            var imp = AssetImporter.GetAtPath(path);
            return imp == null ? 0 : ParsePathId(imp.userData);
        }

        static readonly System.Text.RegularExpressions.Regex PathIdRx =
            new System.Text.RegularExpressions.Regex("\"imposter\\.canonicalPathID\"\\s*:\\s*(-?\\d+)");

        public static long ParsePathId(string userData)
        {
            if (string.IsNullOrEmpty(userData)) return 0;
            var m = PathIdRx.Match(userData);
            return m.Success && long.TryParse(m.Groups[1].Value, out long v) ? v : 0;
        }

        /// <summary>Finds the SDK stub (imposter) for a game shader by its canonical path ID.</summary>
        public static Shader FindImposterShader(long pathId)
        {
            if (_imposterShaders.TryGetValue(pathId, out var cached) && cached != null) return cached;
            foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(p);
                if (imp == null || ParsePathId(imp.userData) != pathId) continue;
                var sh = AssetDatabase.LoadAssetAtPath<Shader>(p);
                if (sh != null) { _imposterShaders[pathId] = sh; return sh; }
            }
            return null;
        }

        /// <summary>The SDK has no stub for p0/Cutout/Bumped Diffuse; this writes one (next to the SDK's other stubs) once.</summary>
        public static Shader EnsureCutoutStub(List<string> log)
        {
            var sh = FindImposterShader(EFTMaterialCore.CutoutPathId);
            if (sh != null) return sh;

            string folder = AssetDatabase.IsValidFolder("Assets/Shader Assets") ? "Assets/Shader Assets" : "Assets/EFT Shader Stubs";
            EnsureFolder(folder);
            string path = folder + "/Cutout_Bumped Diffuse.shader";
            bool reuse = File.Exists(path) && File.ReadAllText(path).Contains("Shader \"p0/Cutout/Bumped Diffuse\"");
            if (!reuse)
            {
                if (File.Exists(path) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                    path = AssetDatabase.GenerateUniqueAssetPath(path);
                File.WriteAllText(path, CutoutStubSource, new System.Text.UTF8Encoding(false));
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path);
            if (imp == null) { log?.Add("Materials: could not import the cutout shader stub at " + path); return null; }
            imp.userData = "{\r\n  \"imposter.canonicalCabID\": \"" + EFTMaterialCore.ShadersCab + "\",\r\n  \"imposter.canonicalPathID\": " +
                           EFTMaterialCore.CutoutPathId + "\r\n}";
            imp.SetAssetBundleNameAndVariant("shaders", "");
            imp.SaveAndReimport();
            _imposterShaders.Remove(EFTMaterialCore.CutoutPathId);
            log?.Add("Materials: created shader stub " + path + " (imposter for the game's p0/Cutout/Bumped Diffuse, bundle 'shaders').");
            return FindImposterShader(EFTMaterialCore.CutoutPathId);
        }

        // Editor preview only: at build time the imposter system swaps this for the game's own shader (path ID above).
        // Property names/defaults are the game shader's (read from the game's shaders bundle).
        const string CutoutStubSource =
@"Shader ""p0/Cutout/Bumped Diffuse"" {
	Properties {
		[MaterialEnum(Static, 0, Characters, 1, Hands, 2)] _StencilType (""_StencilType"", Float) = 0
		_Color (""Main Color"", Color) = (1,1,1,1)
		_MainTex (""Base (RGB) Trans (A)"", 2D) = ""white"" {}
		_BumpMap (""Normalmap"", 2D) = ""bump"" {}
		_Cutoff (""Alpha cutoff"", Range(0,1)) = 0.5
		_SpecVals (""Specular Vals"", Vector) = (0.35,2,0,0)
		_DefVals (""Defuse Vals"", Vector) = (0.1,2.5,0,0)
		_Temperature (""_Temperature(min, max, factor)"", Vector) = (0.1,0.38,0.3,0)
		_Factor (""Z Offset Angle"", Float) = 0
		_Units (""Z Offset Forward"", Float) = 0
	}
	SubShader {
		Tags { ""Queue"" = ""AlphaTest"" ""IgnoreProjector"" = ""True"" ""RenderType"" = ""TransparentCutout"" }
		LOD 300
		CGPROGRAM
		#pragma surface surf Lambert alphatest:_Cutoff
		sampler2D _MainTex;
		sampler2D _BumpMap;
		fixed4 _Color;
		struct Input { float2 uv_MainTex; };
		void surf (Input IN, inout SurfaceOutput o) {
			fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
			o.Albedo = c.rgb;
			o.Alpha = c.a;
			o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
		}
		ENDCG
	}
	FallBack ""Legacy Shaders/Transparent/Cutout/Diffuse""
}
";

        static Texture FindCubemap()
        {
            string p = AssetDatabase.GUIDToAssetPath(EFTMaterialCore.CubeMatteGuid);
            if (!string.IsNullOrEmpty(p))
            {
                var imp = AssetImporter.GetAtPath(p);
                if (imp != null && ParsePathId(imp.userData) == EFTMaterialCore.CubeMattePathId)
                    return AssetDatabase.LoadAssetAtPath<Texture>(p);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Texture", new[] { "Assets" }))
            {
                string q = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(q);
                if (imp != null && ParsePathId(imp.userData) == EFTMaterialCore.CubeMattePathId)
                    return AssetDatabase.LoadAssetAtPath<Texture>(q);
            }
            return null;
        }

        static void ApplyPreset(Material m, EftPreset p, float cutoff)
        {
            void F(string n, float v) { if (m.HasProperty(n)) m.SetFloat(n, v); }
            void C(string n, float[] v) { if (v != null && m.HasProperty(n)) m.SetColor(n, new Color(v[0], v[1], v[2], v[3])); }
            void V(string n, float[] v) { if (v != null && m.HasProperty(n)) m.SetVector(n, new Vector4(v[0], v[1], v[2], v[3])); }
            F("_StencilType", p.StencilType);
            F("_Glossness", p.Glossness);
            F("_Specularness", p.Specularness);
            F("_BumpTiling", p.BumpTiling);
            F("_DecalPower", p.DecalPower);
            F("_Cutoff", p.Cutout ? cutoff : 0.5f);
            C("_Color", p.Color);
            C("_SpecColor", p.SpecColor);
            C("_ReflectColor", p.ReflectColor);
            V("_SpecVals", p.SpecVals);
            V("_DefVals", p.DefVals);
            V("_Temperature", p.Temperature);
            if (m.HasProperty("_SkinnedMeshMaterial")) m.SetFloat("_SkinnedMeshMaterial", 0f);
            m.shaderKeywords = new string[0];   // vanilla character materials carry no keywords
            m.renderQueue = -1;                 // = the shader's queue, as in vanilla
        }

        static bool SetupEft(Material mat, Texture mainTex, string albPath, bool cutout, bool cod, Texture2D nrmTex,
                             string glossPath, string roughPath, BodyPart part, MaterialFixOptions o, List<string> notes)
        {
            var preset = cod && !cutout ? EFTMaterialCore.Cod2EftPreset(part) : EFTMaterialCore.PresetFor(part, cutout);
            var shader = cutout ? EnsureCutoutStub(notes) : FindImposterShader(preset.ShaderPathId);
            if (shader == null)
            {
                notes.Add($"no imposter stub for '{preset.ShaderName}' in the project (the WTT SDK's 'Shader Assets' folder); skipped");
                return false;
            }

            // what the material had before this run: _Glossness/_Specularness depend on it (see below)
            bool hadSpec = mat.HasProperty("_SpecMap") && mat.GetTexture("_SpecMap") != null;
            var oldMain = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            bool hadPacked = oldMain != null && oldMain.name.EndsWith(PackedSuffix, StringComparison.OrdinalIgnoreCase);

            bool converting = mat.shader != shader;
            if (converting)
            {
                mat.shader = shader;
                notes.Add($"{preset.ShaderName} ({preset.Label} values)");
            }
            else notes.Add(preset.ShaderName + (o.ReapplyValues ? $" (re-applied {preset.Label} values)" : " (values kept)"));
            // switching a material from the gloss-packed albedo (older runs) to COD2EFT data changes what its values mean
            bool regimeChanged = cod && !cutout && hadPacked;
            bool applyValues = converting || o.ReapplyValues || regimeChanged;
            if (applyValues) ApplyPreset(mat, preset, o.Cutoff);
            if (regimeChanged && !converting) notes.Add($"re-applied {preset.Label} values (texture data changed to COD2EFT)");
            EnsureMaxSize(albPath);
            EnsureMaxSize(glossPath);

            if (nrmTex != null) mat.SetTexture("_BumpMap", nrmTex);

            if (cutout)
            {
                if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
                SetAlphaIsTransparency(albPath, true);
                SetCoverage(albPath, mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : o.Cutoff);
                notes.Add($"Cutout {mat.GetFloat("_Cutoff"):0.##} (albedo alpha = opacity)");
                return true;
            }

            if (mat.HasProperty("_Cube"))
            {
                var cube = FindCubemap();
                if (cube != null) mat.SetTexture("_Cube", cube);
                else notes.Add("cubemap stub patron_cubemap_metall_matte not found (no reflections)");
            }

            // gloss -> _SpecMap (vanilla: sRGB import, greyscale; the game renders in gamma space so values are used as stored)
            string specPath = glossPath;
            if (specPath == null && roughPath != null) specPath = BuildInvertedGloss(roughPath, notes);
            if (specPath != null)
            {
                mat.SetTexture("_SpecMap", AssetDatabase.LoadAssetAtPath<Texture2D>(specPath));
                if (applyValues || !hadSpec) mat.SetFloat("_Specularness", preset.Specularness); // gloss map (newly) present
                notes.Add("gloss " + Path.GetFileNameWithoutExtension(specPath) + " -> _SpecMap");
            }
            else
            {
                mat.SetTexture("_SpecMap", null);
                if (applyValues || hadSpec) mat.SetFloat("_Specularness", EFTMaterialCore.NoGlossSpecularness);
                notes.Add($"no gloss map (_Specularness {mat.GetFloat("_Specularness"):0.##})");
            }

            // albedo RGB + specular mask in alpha
            if (albPath == null) { notes.Add("no albedo found"); return true; }
            if (cod)
            {
                // COD2EFT's _d alpha already is the per-pixel specular reflectance: use it as is, scaled by _Glossness
                mat.SetTexture("_MainTex", mainTex);
                SetAlphaIsTransparency(albPath, false);
                notes.Add($"_d alpha = COD specular, _Glossness {mat.GetFloat("_Glossness"):0.##}, _Specularness {mat.GetFloat("_Specularness"):0.##}");
                return true;
            }
            var packed = BuildEftAlbedo(albPath, glossPath ?? roughPath, glossPath == null && roughPath != null, o, notes);
            if (packed != null)
            {
                mat.SetTexture("_MainTex", packed);
                if (applyValues || !hadPacked) mat.SetFloat("_Glossness", preset.Glossness);   // mask is in the alpha
            }
            else
            {
                mat.SetTexture("_MainTex", mainTex);
                if (applyValues || hadPacked) mat.SetFloat("_Glossness", EFTMaterialCore.DefaultMaskConstant); // alpha assumed 1
                notes.Add($"albedo used as-is; its alpha is the specular mask, _Glossness {EFTMaterialCore.DefaultMaskConstant}");
            }
            return true;
        }

        static bool CanRead(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        /// <summary>Loads a png/jpg; the result is always RGBA32 (greyscale files would otherwise load as one red channel).</summary>
        static Texture2D LoadFile(string path)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path), false)) { UnityEngine.Object.DestroyImmediate(t); return null; }
            t.wrapMode = TextureWrapMode.Clamp;
            var f = t.format;
            if (f == TextureFormat.RGBA32 || f == TextureFormat.RGB24 || f == TextureFormat.ARGB32) return t;
            var px = t.GetPixels32();
            bool single = f == TextureFormat.R8 || f == TextureFormat.R16 || f == TextureFormat.Alpha8;
            if (single)
                for (int i = 0; i < px.Length; i++)
                {
                    byte v = f == TextureFormat.Alpha8 ? px[i].a : px[i].r;
                    px[i] = new Color32(v, v, v, 255);
                }
            var outT = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp };
            outT.SetPixels32(px);
            outT.Apply(false);
            UnityEngine.Object.DestroyImmediate(t);
            return outT;
        }

        /// <summary>Up to date = the file exists and its importer carries exactly this stamp (which includes each source's size and time).</summary>
        static bool Fresh(string outPath, string stamp)
        {
            if (!File.Exists(outPath)) return false;
            var imp = AssetImporter.GetAtPath(outPath);
            return imp != null && imp.userData == stamp;
        }

        static string FileSig(string path)
        {
            if (path == null || !File.Exists(path)) return "-";
            var fi = new FileInfo(path);
            return AssetDatabase.AssetPathToGUID(path) + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
        }

        /// <summary>
        /// Writes &lt;albedo&gt;_eft.png: RGB = albedo, A = specular mask (gloss*slope+offset, or the constant when there is no gloss map).
        /// Rewritten only when the sources are newer or the settings changed. The original albedo is never modified.
        /// </summary>
        static Texture2D BuildEftAlbedo(string albPath, string glossPath, bool invertGloss, MaterialFixOptions o, List<string> notes)
        {
            if (!CanRead(albPath)) { notes.Add("albedo is " + Path.GetExtension(albPath) + " (only png/jpg can be packed)"); return null; }
            if (glossPath != null && !CanRead(glossPath)) { notes.Add("gloss map is " + Path.GetExtension(glossPath) + " (only png/jpg can be read); constant specular mask"); glossPath = null; }
            string dir = (Path.GetDirectoryName(albPath) ?? "Assets").Replace('\\', '/');
            string outPath = dir + "/" + Path.GetFileNameWithoutExtension(albPath) + PackedSuffix + ".png";
            string stamp = string.Join("|", PackStampKey, FileSig(albPath), FileSig(glossPath), invertGloss ? "inv" : "",
                o.MaskSlope.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                o.MaskOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                EFTMaterialCore.DefaultMaskConstant.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            if (!Fresh(outPath, stamp))
            {
                var alb = LoadFile(albPath);
                if (alb == null) { notes.Add("could not read albedo"); return null; }
                Texture2D gl = glossPath != null ? LoadFile(glossPath) : null;
                try
                {
                    var px = alb.GetPixels32();
                    int w = alb.width, h = alb.height;
                    byte constant = (byte)Mathf.RoundToInt(EFTMaterialCore.DefaultMaskConstant * 255f);
                    Color32[] g = null;
                    bool sameSize = gl != null && gl.width == w && gl.height == h;
                    if (sameSize) g = gl.GetPixels32();
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * w + x;
                            byte a;
                            if (gl == null) a = constant;
                            else
                            {
                                byte gv = sameSize ? g[i].r : (byte)Mathf.Clamp(Mathf.RoundToInt(gl.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r * 255f), 0, 255);
                                if (invertGloss) gv = (byte)(255 - gv);
                                a = EFTMaterialCore.SpecMask(gv, o.MaskSlope, o.MaskOffset);
                            }
                            px[i].a = a;
                        }
                    var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                    outTex.SetPixels32(px);
                    File.WriteAllBytes(outPath, ImageConversion.EncodeToPNG(outTex));
                    UnityEngine.Object.DestroyImmediate(outTex);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(alb);
                    if (gl != null) UnityEngine.Object.DestroyImmediate(gl);
                }
                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
                notes.Add("wrote " + Path.GetFileName(outPath) + (glossPath != null ? " (alpha = specular mask from gloss)" : " (alpha = constant specular mask)"));
            }
            else notes.Add(Path.GetFileName(outPath) + " up to date");

            if (AssetImporter.GetAtPath(outPath) is TextureImporter ti)
            {
                var src = AssetImporter.GetAtPath(albPath) as TextureImporter;
                bool dirty = false;
                if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
                if (!ti.sRGBTexture) { ti.sRGBTexture = true; dirty = true; }
                if (ti.alphaSource != TextureImporterAlphaSource.FromInput) { ti.alphaSource = TextureImporterAlphaSource.FromInput; dirty = true; }
                if (ti.alphaIsTransparency) { ti.alphaIsTransparency = false; dirty = true; }
                if (src != null && ti.maxTextureSize != src.maxTextureSize) { ti.maxTextureSize = src.maxTextureSize; dirty = true; }
                if (src != null && ti.textureCompression != src.textureCompression) { ti.textureCompression = src.textureCompression; dirty = true; }
                if (ti.userData != stamp) { ti.userData = stamp; dirty = true; }
                if (dirty) ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        /// <summary>Roughness-only models: writes &lt;roughness&gt;_eft.png = 1 - roughness (greyscale) for _SpecMap.</summary>
        static string BuildInvertedGloss(string roughPath, List<string> notes)
        {
            if (!CanRead(roughPath)) { notes.Add("roughness map is " + Path.GetExtension(roughPath) + " (only png/jpg can be inverted)"); return null; }
            string dir = (Path.GetDirectoryName(roughPath) ?? "Assets").Replace('\\', '/');
            string outPath = dir + "/" + Path.GetFileNameWithoutExtension(roughPath) + PackedSuffix + ".png";
            string stamp = PackStampKey + "|invert|" + FileSig(roughPath);
            if (!Fresh(outPath, stamp))
            {
                var src = LoadFile(roughPath);
                if (src == null) { notes.Add("could not read roughness map"); return null; }
                try
                {
                    var px = src.GetPixels32();
                    for (int i = 0; i < px.Length; i++) { byte v = (byte)(255 - px[i].r); px[i] = new Color32(v, v, v, 255); }
                    var outTex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                    outTex.SetPixels32(px);
                    File.WriteAllBytes(outPath, ImageConversion.EncodeToPNG(outTex));
                    UnityEngine.Object.DestroyImmediate(outTex);
                }
                finally { UnityEngine.Object.DestroyImmediate(src); }
                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
                notes.Add("wrote " + Path.GetFileName(outPath) + " (gloss = 1 - roughness)");
            }
            if (AssetImporter.GetAtPath(outPath) is TextureImporter ti && ti.userData != stamp) { ti.userData = stamp; ti.SaveAndReimport(); }
            return outPath;
        }

        static bool IsHandsVariant(Material m)
        {
            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(m));
            return imp != null && (imp.userData ?? "").StartsWith(HandsStampPrefix);
        }

        /// <summary>
        /// The material a hands prefab should use instead of 'body' (vanilla hands: Bumped Specular SMap, _StencilType 2).
        /// Written as &lt;material&gt;_hands.mat next to the body material and regenerated from it every time: textures,
        /// _Color, _SpecColor, _Glossness, _Specularness come from the body material, the rest from the vanilla hands values.
        /// Materials that are not on an EFT shader (Standard mode) are returned unchanged.
        /// </summary>
        public static Material HandsVariant(Material body, MaterialFixOptions o, List<string> log)
        {
            if (body == null) return null;
            long id = ImposterPathId(body.shader);
            if (id != EFTMaterialCore.SMapDecalPathId && id != EFTMaterialCore.SMapPathId && id != EFTMaterialCore.CutoutPathId) return body;
            if (id == EFTMaterialCore.SMapPathId) return body; // already a hands material (e.g. COD2EFT's own <name>_Hands set)
            if (IsHandsVariant(body)) return body;
            string bodyPath = AssetDatabase.GetAssetPath(body);
            if (string.IsNullOrEmpty(bodyPath) || AssetDatabase.IsSubAsset(body)) return body;

            bool cutout = id == EFTMaterialCore.CutoutPathId;
            var shader = cutout ? body.shader : FindImposterShader(EFTMaterialCore.SMapPathId);
            if (shader == null) { log?.Add($"Hands material: no stub for p0/Reflective/Bumped Specular SMap; '{body.name}' used as-is."); return body; }

            string path = (Path.GetDirectoryName(bodyPath) ?? "Assets").Replace('\\', '/') + "/" + SafeFile(body.name) + "_hands.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = m == null;
            if (created) m = new Material(shader);
            else if (m.shader != shader) m.shader = shader;

            if (cutout)
            {
                m.CopyPropertiesFromMaterial(body);
                m.SetFloat("_StencilType", 2f);
                m.shaderKeywords = new string[0];
            }
            else
            {
                ApplyPreset(m, EFTMaterialCore.Hands, o?.Cutoff ?? 0.5f);
                foreach (var t in new[] { "_MainTex", "_BumpMap", "_SpecMap", "_Cube" })
                    if (body.HasProperty(t) && m.HasProperty(t))
                    {
                        m.SetTexture(t, body.GetTexture(t));
                        m.SetTextureScale(t, body.GetTextureScale(t));
                        m.SetTextureOffset(t, body.GetTextureOffset(t));
                    }
                foreach (var f in new[] { "_Glossness", "_Specularness" })
                    if (body.HasProperty(f)) m.SetFloat(f, body.GetFloat(f));
                foreach (var c in new[] { "_Color", "_SpecColor" })
                    if (body.HasProperty(c)) m.SetColor(c, body.GetColor(c));
            }

            if (created) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssetIfDirty(m); // write the values before any reimport reloads the file
            var imp = AssetImporter.GetAtPath(path);
            string stamp = HandsStampPrefix + AssetDatabase.AssetPathToGUID(bodyPath);
            if (imp != null && imp.userData != stamp) { imp.userData = stamp; imp.SaveAndReimport(); }
            if (created) log?.Add($"Hands material: created {path} from '{body.name}'.");
            return AssetDatabase.LoadAssetAtPath<Material>(path) ?? m;
        }

        // ================================================================== shared helpers

        static readonly Dictionary<string, (long len, long ticks, int enc)> _encCache = new Dictionary<string, (long, long, int)>();

        /// <summary>COD2EFT encoding of a PNG asset (tEXt Software "COD2EFT enc=N" in its first 4 KB), 0 when untagged.</summary>
        public static int Cod2EftEnc(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(assetPath)) return 0;
            var fi = new FileInfo(assetPath);
            if (_encCache.TryGetValue(assetPath, out var c) && c.len == fi.Length && c.ticks == fi.LastWriteTimeUtc.Ticks) return c.enc;
            var head = new byte[4096];
            int n;
            using (var fs = new FileStream(assetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(head, 0, head.Length);
            if (n < head.Length) Array.Resize(ref head, n);
            int enc = EFTMaterialCore.Cod2EftEncoding(head);
            _encCache[assetPath] = (fi.Length, fi.LastWriteTimeUtc.Ticks, enc);
            return enc;
        }

        /// <summary>Raises the importer's Max Size to at least the file's own size (Unity's 2048 default halves a 4096 atlas).</summary>
        static void EnsureMaxSize(string texPath)
        {
            if (string.IsNullOrEmpty(texPath) || !(AssetImporter.GetAtPath(texPath) is TextureImporter ti)) return;
            ti.GetSourceTextureWidthAndHeight(out int w, out int h);
            int need = 32;
            while (need < Math.Max(w, h) && need < 16384) need *= 2;
            if (ti.maxTextureSize >= need) return;
            ti.maxTextureSize = need;
            ti.SaveAndReimport();
        }

        /// <summary>Cut-out albedo: keep the alpha-tested coverage in the mip maps so thin strands don't vanish at a distance.</summary>
        static void SetCoverage(string texPath, float cutoff)
        {
            if (string.IsNullOrEmpty(texPath) || !(AssetImporter.GetAtPath(texPath) is TextureImporter ti)) return;
            bool dirty = false;
            if (!ti.mipMapsPreserveCoverage) { ti.mipMapsPreserveCoverage = true; dirty = true; }
            if (Math.Abs(ti.alphaTestReferenceValue - cutoff) > 1e-4f) { ti.alphaTestReferenceValue = cutoff; dirty = true; }
            if (dirty) ti.SaveAndReimport();
        }

        static string PrepareNormalMap(string nrmPath, NormalMode mode)
        {
            var ti = AssetImporter.GetAtPath(nrmPath) as TextureImporter;
            if (ti == null) return "";
            bool dirty = false;
            if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; dirty = true; }
            bool flip = ti.flipGreenChannel;
            string conv;
            if (mode == NormalMode.OpenGL) { flip = false; conv = "OpenGL (setting)"; }
            else if (mode == NormalMode.DirectX) { flip = true; conv = "DirectX (setting)"; }
            else
            {
                var det = DetectFromFile(nrmPath, out double ratio);
                if (det == NormalConvention.OpenGL) { flip = false; conv = $"OpenGL (detected x{ratio:0.00})"; }
                else if (det == NormalConvention.DirectX) { flip = true; conv = $"DirectX (detected x{ratio:0.00}) -> green flipped"; }
                else conv = $"convention unclear (x{ratio:0.00}) - kept Flip Green = {ti.flipGreenChannel}";
            }
            if (ti.flipGreenChannel != flip) { ti.flipGreenChannel = flip; dirty = true; }
            if (dirty) ti.SaveAndReimport();
            return conv;
        }

        static void SetAlphaIsTransparency(string texPath, bool on)
        {
            if (string.IsNullOrEmpty(texPath)) return;
            if (AssetImporter.GetAtPath(texPath) is TextureImporter ai && ai.alphaIsTransparency != on)
            { ai.alphaIsTransparency = on; ai.SaveAndReimport(); }
        }

        static NormalConvention DetectFromFile(string assetPath, out double ratio)
        {
            ratio = 1;
            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return NormalConvention.Unknown; // LoadImage limits
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(assetPath), false)) return NormalConvention.Unknown;
                var px = tex.GetPixels32();
                var bytes = new byte[px.Length * 4];
                for (int i = 0; i < px.Length; i++) { bytes[i * 4] = px[i].r; bytes[i * 4 + 1] = px[i].g; bytes[i * 4 + 2] = px[i].b; bytes[i * 4 + 3] = px[i].a; }
                return EFTMaterialCore.DetectNormalConvention(bytes, tex.width, tex.height, bottomUp: true, out ratio);
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        static Texture2D BuildMetallicSmoothness(string srcPath, string msPath, bool invert, List<string> notes)
        {
            bool upToDate = File.Exists(msPath) && File.GetLastWriteTimeUtc(msPath) >= File.GetLastWriteTimeUtc(srcPath);
            if (!upToDate)
            {
                string ext = Path.GetExtension(srcPath).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") { notes.Add("gloss map is " + ext + " (only png/jpg can be packed)"); return null; }
                var src = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                try
                {
                    if (!ImageConversion.LoadImage(src, File.ReadAllBytes(srcPath), false)) { notes.Add("could not read gloss map"); return null; }
                    var px = src.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                    {
                        byte g = px[i].r; // gloss maps are greyscale; R carries it
                        px[i] = new Color32(0, 0, 0, invert ? (byte)(255 - g) : g);
                    }
                    var outTex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                    outTex.SetPixels32(px);
                    File.WriteAllBytes(msPath, ImageConversion.EncodeToPNG(outTex));
                    UnityEngine.Object.DestroyImmediate(outTex);
                }
                finally { UnityEngine.Object.DestroyImmediate(src); }
                AssetDatabase.ImportAsset(msPath, ImportAssetOptions.ForceUpdate);
            }
            if (AssetImporter.GetAtPath(msPath) is TextureImporter ti)
            {
                bool dirty = false;
                if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
                if (ti.sRGBTexture) { ti.sRGBTexture = false; dirty = true; }
                if (ti.alphaSource != TextureImporterAlphaSource.FromInput) { ti.alphaSource = TextureImporterAlphaSource.FromInput; dirty = true; }
                if (dirty) ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(msPath);
        }

        /// <summary>Same render state the Standard shader inspector sets for Opaque / Cutout.</summary>
        public static void SetBlendMode(Material m, bool cutout, float cutoff)
        {
            m.SetFloat("_Mode", cutout ? 1f : 0f);
            m.SetOverrideTag("RenderType", cutout ? "TransparentCutout" : "");
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.Zero);
            m.SetFloat("_ZWrite", 1f);
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            if (cutout) { m.EnableKeyword("_ALPHATEST_ON"); m.SetFloat("_Cutoff", cutoff); m.renderQueue = 2450; /* RenderQueue.AlphaTest */ }
            else { m.DisableKeyword("_ALPHATEST_ON"); m.renderQueue = -1; }
        }

        static string SafeFile(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((s ?? "material").Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
