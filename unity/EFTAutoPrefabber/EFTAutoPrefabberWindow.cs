// EFT Auto Prefabber - builds game-ready EFT clothing / head / hands prefabs from skinned meshes, by name.
// Menu: Custom Windows > EFT Auto Prefabber
//
// It never edits your source objects or models: it reads each SkinnedMeshRenderer (mesh, materials, bone names)
// and builds a fresh prefab laid out like the vanilla game prefabs (see EFTAutoPrefabCore.cs for the evidence).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Skinning;
using EFT.Visual;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace EFTAutoPrefab
{
    public class EFTAutoPrefabberWindow : EditorWindow
    {
        [MenuItem("Custom Windows/EFT Auto Prefabber")]
        public static void Open()
        {
            var w = GetWindow<EFTAutoPrefabberWindow>();
            w.titleContent = new GUIContent("EFT Auto Prefabber");
            w.minSize = new Vector2(760, 420);
        }

        // ------------------------------------------------------------------ data

        class Row
        {
            public SkinnedMeshRenderer Smr;
            public string MeshName;
            public bool Include = true;
            public BodyPart Part;
            public string Variant = "";
            public string Piece = "";
            public int Lod;
            public MeshState State;
            public int BoneCount;
            public string[] RelPaths;          // relative to Root_Joint
            public string RootRel;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        class Source
        {
            public GameObject Root;
            public bool IsAsset;
            public string ModelName;
            public string OutputFolder;
            public bool Foldout = true;
            public readonly List<Row> Rows = new List<Row>();
        }

        class Group
        {
            public Source Src;
            public BodyPart Part;
            public string Variant;
            public List<Row> Rows;                 // base meshes (one renderer each)
            public List<Slot> Slots;               // base mesh + its armor/vest/face-cover alternatives
            public string PrefabName;
            public string PrefabPath;
            public string BundleName;
            public Transform HolsterSource;        // Lower: a "pistol_holster" object under a thigh bone, else null (vanilla default)
            public bool HolsterLeft;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>One renderer in the prefab: the base mesh plus the meshes the game swaps in (TorsoSkin / HeadSkin).</summary>
        class Slot
        {
            public Row Base;
            public readonly Dictionary<MeshState, Row> States = new Dictionary<MeshState, Row>();
        }

        readonly List<Source> _sources = new List<Source>();
        Vector2 _scroll;
        Vector2 _logScroll;
        bool _showSettings;
        string _log = "";

        // settings (EditorPrefs)
        const string Pref = "EFTAutoPrefab.";
        readonly Dictionary<BodyPart, string> _aliases = new Dictionary<BodyPart, string>();
        readonly Dictionary<BodyPart, string> _bundlePatterns = new Dictionary<BodyPart, string>();
        string _prefabPattern;
        bool _setBundleNames;
        bool _addHotObject;
        bool _fixMaterialsOnScan;
        bool _addHolster;
        bool _holsterLeft;
        bool _showMaterialSettings;
        readonly MaterialFixOptions _mat = new MaterialFixOptions();

        static readonly BodyPart[] Parts = { BodyPart.Upper, BodyPart.Lower, BodyPart.Head, BodyPart.Hands };

        void OnEnable() { LoadSettings(); }

        void LoadSettings()
        {
            foreach (var p in Parts)
            {
                _aliases[p] = EditorPrefs.GetString(Pref + "alias." + p, EFTAutoPrefabCore.DefaultAliases[p]);
                _bundlePatterns[p] = EditorPrefs.GetString(Pref + "bundle." + p, EFTAutoPrefabCore.DefaultBundlePatterns[p]);
            }
            _prefabPattern = EditorPrefs.GetString(Pref + "prefabPattern", EFTAutoPrefabCore.DefaultPrefabPattern);
            _setBundleNames = EditorPrefs.GetBool(Pref + "setBundleNames", true);
            _addHotObject = EditorPrefs.GetBool(Pref + "addHotObject", true);
            _fixMaterialsOnScan = EditorPrefs.GetBool(Pref + "mat.fixOnScan", true);
            _addHolster = EditorPrefs.GetBool(Pref + "holster", true);
            _holsterLeft = EditorPrefs.GetBool(Pref + "holsterLeft", false);
            _mat.Mode = (ShaderMode)EditorPrefs.GetInt(Pref + "mat.mode", (int)ShaderMode.EFT);
            _mat.ReapplyValues = EditorPrefs.GetBool(Pref + "mat.reapply", false);
            _mat.HighQualityCompression = EditorPrefs.GetBool(Pref + "mat.bc7", false);
            _mat.MaskSlope = EditorPrefs.GetFloat(Pref + "mat.maskSlope", EFTMaterialCore.DefaultMaskSlope);
            _mat.MaskOffset = EditorPrefs.GetFloat(Pref + "mat.maskOffset", EFTMaterialCore.DefaultMaskOffset);
            _mat.Aliases = _aliases;
            _mat.Extract = EditorPrefs.GetBool(Pref + "mat.extract", true);
            _mat.Normals = (NormalMode)EditorPrefs.GetInt(Pref + "mat.normals", (int)NormalMode.AutoDetect);
            _mat.UseGloss = EditorPrefs.GetBool(Pref + "mat.gloss", true);
            _mat.CutoutKeywords = EditorPrefs.GetString(Pref + "mat.cutoutKeys", EFTMaterialCore.DefaultCutoutKeywords);
            _mat.Cutoff = EditorPrefs.GetFloat(Pref + "mat.cutoff", EFTMaterialCore.DefaultCutoff);
            // 1.7.1: the default went 0.5 -> 0.3; a saved 0.5 is the old default, not a choice (once)
            if (!EditorPrefs.GetBool(Pref + "mat.cutoff.171", false))
            {
                if (Mathf.Abs(_mat.Cutoff - 0.5f) < 1e-4f) _mat.Cutoff = EFTMaterialCore.DefaultCutoff;
                EditorPrefs.SetBool(Pref + "mat.cutoff.171", true);
            }
            foreach (var r in EFTMaterialCore.DefaultSuffixes.Keys.ToList())
                _mat.Suffixes[r] = EditorPrefs.GetString(Pref + "mat.suffix." + r, EFTMaterialCore.DefaultSuffixes[r]);
        }

        void SaveSettings()
        {
            foreach (var p in Parts)
            {
                EditorPrefs.SetString(Pref + "alias." + p, _aliases[p]);
                EditorPrefs.SetString(Pref + "bundle." + p, _bundlePatterns[p]);
            }
            EditorPrefs.SetString(Pref + "prefabPattern", _prefabPattern);
            EditorPrefs.SetBool(Pref + "setBundleNames", _setBundleNames);
            EditorPrefs.SetBool(Pref + "addHotObject", _addHotObject);
            EditorPrefs.SetBool(Pref + "mat.fixOnScan", _fixMaterialsOnScan);
            EditorPrefs.SetBool(Pref + "holster", _addHolster);
            EditorPrefs.SetBool(Pref + "holsterLeft", _holsterLeft);
            EditorPrefs.SetInt(Pref + "mat.mode", (int)_mat.Mode);
            EditorPrefs.SetBool(Pref + "mat.reapply", _mat.ReapplyValues);
            EditorPrefs.SetFloat(Pref + "mat.maskSlope", _mat.MaskSlope);
            EditorPrefs.SetFloat(Pref + "mat.maskOffset", _mat.MaskOffset);
            EditorPrefs.SetBool(Pref + "mat.extract", _mat.Extract);
            EditorPrefs.SetInt(Pref + "mat.normals", (int)_mat.Normals);
            EditorPrefs.SetBool(Pref + "mat.gloss", _mat.UseGloss);
            EditorPrefs.SetString(Pref + "mat.cutoutKeys", _mat.CutoutKeywords);
            EditorPrefs.SetFloat(Pref + "mat.cutoff", _mat.Cutoff);
            EditorPrefs.SetBool(Pref + "mat.bc7", _mat.HighQualityCompression);
            foreach (var kv in _mat.Suffixes) EditorPrefs.SetString(Pref + "mat.suffix." + kv.Key, kv.Value);
        }

        /// <summary>Selected top-level objects (children of other selected objects dropped).</summary>
        static List<GameObject> PickedRoots()
        {
            var picked = Selection.objects.OfType<GameObject>().Distinct().ToList();
            return picked.Where(g => !picked.Any(p => p != g && g.transform.IsChildOf(p.transform))).ToList();
        }

        /// <summary>Runs the material fixer; returns the roots re-resolved (a model reimport can replace asset objects).</summary>
        List<GameObject> FixMaterials(List<GameObject> roots)
        {
            var keys = roots.Select(g => EditorUtility.IsPersistent(g)
                ? (asset: true, path: AssetDatabase.GetAssetPath(g), name: g.name, obj: g)
                : (asset: false, path: (string)null, name: g.name, obj: g)).ToList();
            try
            {
                EditorUtility.DisplayProgressBar("EFT Auto Prefabber", "Fixing materials...", 0.5f);
                foreach (var line in EFTMaterialFixer.Fix(roots, _mat)) AppendLog(line);
            }
            catch (Exception e) { AppendLog("Material fix FAILED: " + e.Message); Debug.LogException(e); }
            finally { EditorUtility.ClearProgressBar(); }
            var result = new List<GameObject>();
            foreach (var k in keys)
            {
                if (!k.asset) { if (k.obj != null) result.Add(k.obj); continue; }
                var main = AssetDatabase.LoadAssetAtPath<GameObject>(k.path);
                GameObject again = main != null && main.name == k.name ? main
                    : AssetDatabase.LoadAllAssetsAtPath(k.path).OfType<GameObject>().FirstOrDefault(x => x.name == k.name);
                if (again != null) result.Add(again);
            }
            return result;
        }

        // ------------------------------------------------------------------ scan

        void Scan()
        {
            _sources.Clear();
            var picked = PickedRoots();
            if (picked.Count > 0 && _fixMaterialsOnScan) picked = FixMaterials(picked);

            if (picked.Count == 0)
            {
                AppendLog("Nothing selected. Select model(s) in the Project panel and/or object(s) in the Hierarchy, then Scan.");
                return;
            }

            foreach (var go in picked)
            {
                var src = new Source { Root = go, IsAsset = EditorUtility.IsPersistent(go) };
                if (src.IsAsset)
                {
                    string ap = AssetDatabase.GetAssetPath(go);
                    src.ModelName = Path.GetFileNameWithoutExtension(ap);
                    string dir = (Path.GetDirectoryName(ap) ?? "Assets").Replace('\\', '/');
                    src.OutputFolder = dir + "/Prefabs";
                }
                else
                {
                    src.ModelName = go.name.Trim();
                    src.OutputFolder = "Assets/Prefabs/" + EFTAutoPrefabCore.Format("{model}", src.ModelName, BodyPart.Ignore, "", false);
                }

                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var parsed = EFTAutoPrefabCore.Parse(smr.gameObject.name, _aliases);
                    var row = new Row
                    {
                        Smr = smr,
                        MeshName = smr.gameObject.name,
                        Part = parsed.Part,
                        Variant = parsed.Variant,
                        Piece = parsed.Piece,
                        Lod = parsed.Lod,
                        State = parsed.State,
                        Include = parsed.Part != BodyPart.Ignore,
                    };
                    ValidateRow(row);
                    src.Rows.Add(row);
                }

                if (src.Rows.Count == 0)
                {
                    int plain = go.GetComponentsInChildren<MeshRenderer>(true).Length;
                    if (plain > 0)
                        AppendLog($"'{go.name}': {plain} mesh(es) but none of them skinned - the FBX has no armature. " +
                                  "COD2EFT before 2.6.9 left the armature out when it was hidden in Blender: export again with 2.6.9 or newer.");
                    else
                        AppendLog($"'{go.name}': no SkinnedMeshRenderers found under it.");
                }
                _sources.Add(src);
            }
            AppendLog($"Scanned {picked.Count} object(s), {_sources.Sum(s => s.Rows.Count)} skinned mesh(es).");
        }

        void ValidateRow(Row row)
        {
            row.Errors.Clear();
            row.Warnings.Clear();
            row.RelPaths = null;
            row.RootRel = null;

            var smr = row.Smr;
            if (smr == null) { row.Errors.Add("Mesh object no longer exists - Scan again."); return; }
            var mesh = smr.sharedMesh;
            if (mesh == null) { row.Errors.Add("SkinnedMeshRenderer has no mesh."); return; }

            Transform[] bones = smr.bones ?? new Transform[0];
            row.BoneCount = bones.Length;

            try
            {
                int bindposes = mesh.bindposes.Length;
                if (bindposes != bones.Length)
                    row.Errors.Add($"Renderer has {bones.Length} bones but the mesh has {bindposes} bind poses.");
            }
            catch (Exception e)
            {
                row.Warnings.Add("Could not read bind poses: " + e.Message);
            }

            var names = bones.Select(b => b != null ? b.name : null).ToList();
            EFTAutoPrefabCore.MapBones(names, smr.rootBone != null ? smr.rootBone.name : null,
                                       out row.RelPaths, out row.RootRel, row.Errors, row.Warnings);
        }

        // ------------------------------------------------------------------ grouping

        List<Group> BuildGroups(Source s)
        {
            var groups = new List<Group>();
            var rows = s.Rows.Where(r => r.Include && r.Part != BodyPart.Ignore && r.Smr != null);
            foreach (var g in rows.GroupBy(r => new { r.Part, V = (r.Variant ?? "").Trim().ToLowerInvariant() })
                                  .OrderBy(g => (int)g.Key.Part).ThenBy(g => g.Key.V))
            {
                var all = g.OrderBy(r => r.Piece).ThenBy(r => r.Lod).ThenBy(r => (int)r.State).ToList();
                var grp = new Group
                {
                    Src = s,
                    Part = g.Key.Part,
                    Variant = g.Key.V,
                    Rows = all.Where(r => r.State == MeshState.Base).ToList(),
                    Slots = new List<Slot>(),
                };
                grp.PrefabName = EFTAutoPrefabCore.Format(_prefabPattern, s.ModelName, grp.Part, grp.Variant, false);
                grp.PrefabPath = (s.OutputFolder ?? "").TrimEnd('/') + "/" + grp.PrefabName + ".prefab";
                grp.BundleName = EFTAutoPrefabCore.Format(_bundlePatterns[grp.Part], s.ModelName, grp.Part, grp.Variant, true);

                if (string.IsNullOrEmpty(grp.PrefabName)) grp.Errors.Add("Prefab name is empty (check the name pattern).");
                if (!IsAssetsPath(s.OutputFolder)) grp.Errors.Add("Output folder must be inside Assets/.");
                if (_setBundleNames && string.IsNullOrEmpty(grp.BundleName)) grp.Errors.Add("Bundle name is empty (check the pattern).");
                if (grp.Rows.Count == 0) grp.Errors.Add("Only alternative (armor/vest/face cover) meshes - the base mesh is missing.");

                foreach (var r in all.Where(r => r.Errors.Count > 0))
                    grp.Errors.Add($"'{r.MeshName}' has errors.");

                foreach (var dup in all.GroupBy(r => new { r.Piece, r.Lod, r.State }).Where(d => d.Count() > 1))
                    grp.Errors.Add($"Several meshes share piece '{dup.Key.Piece}' LOD {dup.Key.Lod}" +
                                   (dup.Key.State != MeshState.Base ? " " + dup.Key.State : "") + ": " +
                                   string.Join(", ", dup.Select(d => d.MeshName)) + ". Rename or untick one.");

                foreach (var b in grp.Rows.GroupBy(r => new { r.Piece, r.Lod }).Select(x => x.First()))
                    grp.Slots.Add(new Slot { Base = b });
                foreach (var st in all.Where(r => r.State != MeshState.Base))
                {
                    if (!EFTAutoPrefabCore.StateAllowed(grp.Part, st.State))
                    {
                        grp.Errors.Add($"'{st.MeshName}': {st.State} meshes are only used by " +
                                       (st.State == MeshState.FaceCover ? "heads" : "tops (upper)") + ". Rename it or untick it.");
                        continue;
                    }
                    var slot = grp.Slots.FirstOrDefault(x => x.Base.Piece == st.Piece && x.Base.Lod == st.Lod);
                    if (slot == null)
                    {
                        grp.Errors.Add($"'{st.MeshName}' ({st.State}) has no base mesh with the same piece '{st.Piece}' and LOD {st.Lod}.");
                        continue;
                    }
                    if (!slot.States.ContainsKey(st.State)) slot.States[st.State] = st;
                    if (st.Smr != null && slot.Base.Smr != null)
                    {
                        var bm = slot.Base.Smr.sharedMaterials; var sm = st.Smr.sharedMaterials;
                        int bs = slot.Base.Smr.sharedMesh != null ? slot.Base.Smr.sharedMesh.subMeshCount : 0;
                        int ss = st.Smr.sharedMesh != null ? st.Smr.sharedMesh.subMeshCount : 0;
                        if (bs != ss)
                            grp.Errors.Add($"'{st.MeshName}' has {ss} material slot(s) but its base '{slot.Base.MeshName}' has {bs}. The game only swaps the mesh, so both need the same material slots.");
                        else if (!bm.SequenceEqual(sm))
                            grp.Warnings.Add($"'{st.MeshName}' uses different materials than '{slot.Base.MeshName}'; the base mesh's materials are used for it in game.");
                    }
                }

                var lods = grp.Rows.Select(r => r.Lod).Distinct().OrderBy(l => l).ToList();
                if (lods.Count > 0 && (lods[0] != 0 || lods[lods.Count - 1] != lods.Count - 1))
                    grp.Warnings.Add("LOD numbers are not 0..N without gaps (" + string.Join(",", lods) + "); they are used in that order.");
                if (grp.Part == BodyPart.Hands && lods.Count > 1)
                    grp.Warnings.Add("Vanilla hands have a single LOD; all LODs will be listed in LoddedSkin and shown together.");

                if (AssetDatabase.LoadAssetAtPath<GameObject>(grp.PrefabPath) != null)
                    grp.Warnings.Add("Prefab exists and will be overwritten (its GUID/references are kept).");

                if (grp.Part == BodyPart.Lower && _addHolster)
                {
                    grp.HolsterLeft = _holsterLeft;
                    var h = s.Root.GetComponentsInChildren<Transform>(true)
                                  .FirstOrDefault(t => t.name.Equals("pistol_holster", StringComparison.OrdinalIgnoreCase));
                    if (h != null)
                    {
                        string par = h.parent != null ? h.parent.name : "";
                        if (par == "Base HumanRThigh1" || par == "Base HumanLThigh1")
                        {
                            grp.HolsterSource = h;
                            grp.HolsterLeft = par == "Base HumanLThigh1";
                            grp.Warnings.Add($"Pistol holster position from '{h.name}' ({(grp.HolsterLeft ? "left" : "right")} thigh).");
                        }
                        else grp.Warnings.Add($"'{h.name}' is not a child of Base HumanRThigh1 / Base HumanLThigh1, so the vanilla holster position is used.");
                    }
                }

                groups.Add(grp);
            }

            // two different groups must not produce the same prefab file / bundle
            foreach (var clash in groups.GroupBy(g => g.PrefabPath.ToLowerInvariant()).Where(c => c.Count() > 1))
                foreach (var g in clash) g.Errors.Add("Another group writes the same prefab file: " + g.PrefabPath);
            if (_setBundleNames)
                foreach (var clash in groups.GroupBy(g => g.BundleName).Where(c => c.Count() > 1))
                    foreach (var g in clash) g.Errors.Add("Another group uses the same bundle name: " + g.BundleName);
            return groups;
        }

        static bool IsAssetsPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            p = p.Replace('\\', '/').TrimEnd('/');
            return p == "Assets" || p.StartsWith("Assets/");
        }

        // ------------------------------------------------------------------ build

        /// <summary>Builds every group without errors; true when all of them were built (none skipped or failed).</summary>
        bool BuildAll()
        {
            var all = _sources.Where(s => s.Root != null).SelectMany(BuildGroups).ToList();
            var ok = all.Where(g => g.Errors.Count == 0).ToList();
            var bad = all.Where(g => g.Errors.Count > 0).ToList();
            foreach (var g in bad)
                AppendLog($"SKIPPED {g.PrefabName}: " + string.Join(" | ", g.Errors));
            if (ok.Count == 0) { AppendLog("Nothing to build."); return false; }

            var preview = EditorSceneManager.NewPreviewScene();
            var handsMats = new Dictionary<Material, Material>();
            int built = 0;
            try
            {
                for (int i = 0; i < ok.Count; i++)
                {
                    var g = ok[i];
                    EditorUtility.DisplayProgressBar("EFT Auto Prefabber", g.PrefabName, (float)i / ok.Count);
                    try
                    {
                        if (BuildGroup(g, preview, handsMats)) built++;
                    }
                    catch (Exception e)
                    {
                        AppendLog($"FAILED {g.PrefabName}: {e.Message}");
                        Debug.LogException(e);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            AppendLog($"Done: {built} prefab(s) built, {bad.Count} skipped.");
            return built == ok.Count && bad.Count == 0;
        }

        // Vanilla pistol holster transforms (child "pistol_holster" of the pants root, copied by the game onto the holster
        // bone under the thigh). Right: usec_feet (default USEC pants). Left: pants_bear_fsburban, the only vanilla left one.
        static readonly Vector3 HolsterRightPos = new Vector3(0.42520f, 0.05940f, 0.14110f);
        static readonly Quaternion HolsterRightRot = new Quaternion(-0.52819f, -0.57112f, -0.42935f, 0.45881f);
        static readonly Vector3 HolsterLeftPos = new Vector3(0.31098f, 0.05372f, -0.11084f);
        static readonly Quaternion HolsterLeftRot = new Quaternion(-0.46416f, -0.50264f, -0.49787f, 0.53295f);

        bool BuildGroup(Group g, Scene preview, Dictionary<Material, Material> handsMats)
        {
            bool hands = g.Part == BodyPart.Hands;
            string prefix = EFTAutoPrefabCore.SkeletonPrefix(g.Part);
            float[] temp = EFTAutoPrefabCore.DefaultTemperature(g.Part);

            EnsureFolder(g.Src.OutputFolder);
            string meshFolder = g.Src.OutputFolder.TrimEnd('/') + "/" + g.PrefabName + "_meshes";
            int remapped = 0, torso = 0, headSkins = 0;
            var usedMeshPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var root = new GameObject(g.PrefabName);
            SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                var skins = new List<AbstractSkin>();
                var byLod = new SortedDictionary<int, List<Renderer>>();

                foreach (var slot in g.Slots)
                {
                    var r = slot.Base;
                    var go = new GameObject(r.MeshName);
                    go.transform.SetParent(root.transform, false);

                    // bone list: the base mesh's; with alternative meshes, the union of all of them (each mesh is
                    // re-indexed to it if needed, because the game swaps only the mesh and keeps the renderer's bones)
                    var members = new List<Row> { r };
                    members.AddRange(slot.States.Values);
                    string[] bones = r.RelPaths;
                    if (slot.States.Count > 0)
                    {
                        var union = new List<string>(r.RelPaths);
                        foreach (var m in slot.States.Values)
                            foreach (var b in m.RelPaths) if (!union.Contains(b)) union.Add(b);
                        bones = union.ToArray();
                    }
                    var meshFor = new Dictionary<Row, Mesh>();
                    foreach (var m in members)
                    {
                        if (m.RelPaths.SequenceEqual(bones)) { meshFor[m] = m.Smr.sharedMesh; continue; }
                        EnsureFolder(meshFolder);
                        string mp = meshFolder + "/" + SafeName(m.MeshName) + ".asset";
                        for (int k = 2; !usedMeshPaths.Add(mp); k++) mp = meshFolder + "/" + SafeName(m.MeshName) + "_" + k + ".asset";
                        meshFor[m] = RemapMesh(m, bones, members, mp);
                        remapped++;
                    }

                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    CopyRenderer(r.Smr, smr);
                    smr.sharedMesh = meshFor[r];
                    if (hands)
                        smr.sharedMaterials = r.Smr.sharedMaterials.Select(m => HandsMaterial(m, handsMats)).ToArray();
                    smr.bones = new Transform[bones.Length]; // vanilla ships empty slots; the game fills them
                    smr.rootBone = null;

                    var skin = go.AddComponent<Skin>();
                    var so = new SerializedObject(skin);
                    so.FindProperty("_skinnedMeshRenderer").objectReferenceValue = smr;
                    var bp = so.FindProperty("_bonePaths");
                    bp.arraySize = bones.Length;
                    for (int i = 0; i < bones.Length; i++)
                        bp.GetArrayElementAtIndex(i).stringValue = prefix + bones[i];
                    so.FindProperty("_rootBonePath").stringValue = prefix + r.RootRel;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    AbstractSkin lodEntry = skin;
                    if (g.Part == BodyPart.Upper && (slot.States.ContainsKey(MeshState.Armor) || slot.States.ContainsKey(MeshState.Vest)))
                    {
                        // TorsoSkin (game): armor worn and not vest-like -> _armor; vest-like armor / rig / backpack -> _vest;
                        // else _base. A missing one falls back to the other alternative, then to the base mesh.
                        Mesh baseM = meshFor[r];
                        Mesh armor = slot.States.TryGetValue(MeshState.Armor, out var ar) ? meshFor[ar] : null;
                        Mesh vest = slot.States.TryGetValue(MeshState.Vest, out var vr) ? meshFor[vr] : null;
                        var ts = go.AddComponent<TorsoSkin>();
                        var tso = new SerializedObject(ts);
                        tso.FindProperty("_skin").objectReferenceValue = skin;
                        tso.FindProperty("_base").objectReferenceValue = baseM;
                        tso.FindProperty("_armor").objectReferenceValue = armor ?? vest ?? baseM;
                        tso.FindProperty("_vest").objectReferenceValue = vest ?? armor ?? baseM;
                        tso.ApplyModifiedPropertiesWithoutUndo();
                        lodEntry = ts;
                        torso++;
                    }
                    else if (g.Part == BodyPart.Head && slot.States.TryGetValue(MeshState.FaceCover, out var fc))
                    {
                        // HeadSkin (game): a full (non-half) face cover worn -> Data.FaceCover, else Data.Base
                        var hs = go.AddComponent<HeadSkin>();
                        var hso = new SerializedObject(hs);
                        hso.FindProperty("Skin").objectReferenceValue = skin;
                        hso.FindProperty("Data.Base").objectReferenceValue = meshFor[r];
                        hso.FindProperty("Data.FaceCover").objectReferenceValue = meshFor[fc];
                        hso.ApplyModifiedPropertiesWithoutUndo();
                        lodEntry = hs;
                        headSkins++;
                    }

                    if (_addHotObject)
                    {
                        var hot = go.AddComponent<HotObject>();
                        hot.Temperature = new Vector3(temp[0], temp[1], temp[2]);
                        hot.TemperatureCelsio = 29f;
                        hot.IsApplyAllMaterials = false;
                        var hso = new SerializedObject(hot);
                        var mid = hso.FindProperty("materialsId");
                        if (mid != null)
                        {
                            mid.arraySize = 1;
                            mid.GetArrayElementAtIndex(0).intValue = 0;
                            hso.ApplyModifiedPropertiesWithoutUndo();
                        }
                    }
                    if (hands) go.AddComponent<RainCondensator>();

                    skins.Add(lodEntry);
                    if (!byLod.TryGetValue(r.Lod, out var list)) byLod[r.Lod] = list = new List<Renderer>();
                    list.Add(smr);
                }

                var lodded = root.AddComponent<LoddedSkin>();
                var lso = new SerializedObject(lodded);
                var lp = lso.FindProperty("_lods");
                lp.arraySize = skins.Count;
                for (int i = 0; i < skins.Count; i++)
                    lp.GetArrayElementAtIndex(i).objectReferenceValue = skins[i];
                lso.ApplyModifiedPropertiesWithoutUndo();

                if (!hands)
                {
                    var lg = root.AddComponent<LODGroup>();
                    float[] h = EFTAutoPrefabCore.LodHeights(byLod.Count, g.Part == BodyPart.Head);
                    var lods = byLod.Values.Select((rs, i) => new LOD(h[i], rs.ToArray())).ToArray();
                    lg.SetLODs(lods);
                    lg.localReferencePoint = Vector3.zero;
                    lg.size = 2f; // vanilla value
                }

                if (g.Part == BodyPart.Lower && _addHolster)
                {
                    // PlayerBody: pants without LegsView get no pistol on the leg at all (holster slot view removed)
                    var holster = new GameObject("pistol_holster").transform;
                    holster.SetParent(root.transform, false);
                    if (g.HolsterSource != null)
                    {
                        // relative to the thigh in world space (metres), so an armature imported with scale 100 still works
                        var th = g.HolsterSource.parent;
                        var inv = Quaternion.Inverse(th.rotation);
                        holster.localPosition = inv * (g.HolsterSource.position - th.position);
                        holster.localRotation = inv * g.HolsterSource.rotation;
                    }
                    else
                    {
                        holster.localPosition = g.HolsterLeft ? HolsterLeftPos : HolsterRightPos;
                        holster.localRotation = g.HolsterLeft ? HolsterLeftRot : HolsterRightRot;
                    }
                    var legs = root.AddComponent<LegsView>();
                    var lvo = new SerializedObject(legs);
                    lvo.FindProperty("_isRightLeg").boolValue = !g.HolsterLeft;
                    lvo.FindProperty("_holster").objectReferenceValue = holster;
                    lvo.ApplyModifiedPropertiesWithoutUndo();
                }

                PrefabUtility.SaveAsPrefabAsset(root, g.PrefabPath, out bool saved);
                if (!saved) { AppendLog($"FAILED to save {g.PrefabPath}"); return false; }
            }
            finally
            {
                DestroyImmediate(root);
            }

            string bundleNote = "";
            if (_setBundleNames)
            {
                var imp = AssetImporter.GetAtPath(g.PrefabPath);
                if (imp != null)
                {
                    imp.SetAssetBundleNameAndVariant(g.BundleName, "bundle");
                    bundleNote = $"  ->  {g.BundleName}.bundle";
                }
                else bundleNote = "  (could not set bundle name)";
            }
            var extra = new List<string>();
            if (torso > 0) extra.Add($"{torso} with armor/vest meshes");
            if (headSkins > 0) extra.Add($"{headSkins} with face-cover mesh");
            if (remapped > 0) extra.Add($"{remapped} mesh(es) re-indexed to a shared bone list in {meshFolder}");
            if (g.Part == BodyPart.Lower && _addHolster) extra.Add("pistol holster " + (g.HolsterSource != null ? "from model" : "vanilla " + (g.HolsterLeft ? "left" : "right")));
            AppendLog($"Built {g.PrefabPath}  [{g.Slots.Count} mesh(es)" + (extra.Count > 0 ? "; " + string.Join("; ", extra) : "") + $"]{bundleNote}");
            return true;
        }

        Material HandsMaterial(Material m, Dictionary<Material, Material> cache)
        {
            if (m == null) return null;
            if (cache.TryGetValue(m, out var v)) return v;
            var log = new List<string>();
            v = EFTMaterialFixer.HandsVariant(m, _mat, log) ?? m;
            foreach (var l in log) AppendLog(l);
            cache[m] = v;
            return v;
        }

        /// <summary>
        /// Copy of the row's mesh whose bone indices / bind poses follow 'bones' (paths relative to Root_Joint).
        /// Bind poses of bones this mesh does not use are taken from another member mesh (unused by its weights).
        /// </summary>
        static Mesh RemapMesh(Row row, string[] bones, List<Row> members, string assetPath)
        {
            var src = row.Smr.sharedMesh;
            var srcBind = src.bindposes;
            var bindFor = new Dictionary<string, Matrix4x4>();
            foreach (var m in members)
            {
                var bp = m.Smr.sharedMesh.bindposes;
                for (int i = 0; i < m.RelPaths.Length && i < bp.Length; i++)
                    if (m == row || !bindFor.ContainsKey(m.RelPaths[i])) bindFor[m.RelPaths[i]] = bp[i];
            }
            var map = row.RelPaths.Select(p => Array.IndexOf(bones, p)).ToArray();

            var copy = Instantiate(src);
            copy.name = Path.GetFileNameWithoutExtension(assetPath);
            // bind poses first, so the new bone indices never point past the bind-pose count
            copy.bindposes = bones.Select(b => bindFor.TryGetValue(b, out var mtx) ? mtx : Matrix4x4.identity).ToArray();
            var perVertex = src.GetBonesPerVertex();
            var weights = src.GetAllBoneWeights();
            var nw = new Unity.Collections.NativeArray<BoneWeight1>(weights.Length, Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < weights.Length; i++)
                {
                    var w = weights[i];
                    w.boneIndex = map[w.boneIndex];
                    nw[i] = w;
                }
                copy.SetBoneWeights(perVertex, nw);
            }
            finally { nw.Dispose(); }

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(copy, existing);
                DestroyImmediate(copy);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(copy, assetPath);
            return copy;
        }

        static string SafeName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((s ?? "mesh").Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        static void CopyRenderer(SkinnedMeshRenderer src, SkinnedMeshRenderer dst)
        {
            dst.sharedMesh = src.sharedMesh;
            dst.sharedMaterials = src.sharedMaterials;
            dst.localBounds = src.localBounds;
            dst.quality = src.quality;
            dst.updateWhenOffscreen = false;
            dst.skinnedMotionVectors = src.skinnedMotionVectors;
            dst.shadowCastingMode = src.shadowCastingMode;
            dst.receiveShadows = src.receiveShadows;
            dst.lightProbeUsage = src.lightProbeUsage;
            dst.reflectionProbeUsage = src.reflectionProbeUsage;
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string cur = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        // ------------------------------------------------------------------ GUI

        void AppendLog(string line)
        {
            _log = (_log.Length > 0 ? _log + "\n" : "") + line;
            Debug.Log("[EFT Auto Prefabber] " + line);
            Repaint();
        }

        static GUIStyle _err, _warn, _ok;
        static void Styles()
        {
            if (_err != null) return;
            _err = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.35f, 0.3f) }, wordWrap = true };
            _warn = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.75f, 0.2f) }, wordWrap = true };
            _ok = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.4f, 0.85f, 0.4f) } };
        }

        void OnGUI()
        {
            Styles();
            EFTToolsVersion.DrawHeader();

            EditorGUILayout.HelpBox(
                "1) Select model(s) in the Project panel and/or objects in the Hierarchy.  2) Scan.  3) Check parts/variants.  4) Build.\n" +
                "Naming: <piece>_<part><variant>[_state][_lodN]  e.g. test1_upper + test2_upper -> one prefab; uppera / upper1 -> separate variant prefabs;\n" +
                "upper_armor / upper_vest / head_facecover -> meshes the game swaps in when armor / a rig / a face cover is worn.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Scan Selection", _fixMaterialsOnScan ? "Fixes the selection's materials first (see Settings > Materials)" : "Scans the selection"), GUILayout.Height(24))) Scan();
                if (GUILayout.Button(new GUIContent("Fix Materials", "Extract materials, find _d/_n/_g textures, normal-map import + DirectX detection, then EFT shaders with vanilla values " +
                    "(or Standard, see Settings > Materials). Hair/lashes/alpha materials become cutout."), GUILayout.Height(24), GUILayout.Width(110)))
                {
                    var roots = PickedRoots();
                    if (roots.Count == 0) AppendLog("Nothing selected to fix.");
                    else FixMaterials(roots);
                }
                GUI.enabled = _sources.Count > 0;
                if (GUILayout.Button("Re-check", GUILayout.Height(24), GUILayout.Width(90)))
                    foreach (var s in _sources) foreach (var r in s.Rows) ValidateRow(r);
                if (GUILayout.Button("Clear", GUILayout.Height(24), GUILayout.Width(70))) { _sources.Clear(); _log = ""; }
                GUI.enabled = true;
            }

            DrawSettings();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var s in _sources.ToList())
            {
                if (s.Root == null) continue;
                DrawSource(s);
            }
            EditorGUILayout.EndScrollView();

            var groups = _sources.Where(s => s.Root != null).SelectMany(BuildGroups).ToList();
            int good = groups.Count(g => g.Errors.Count == 0);
            GUI.enabled = good > 0;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button($"Build {good} prefab(s)" + (groups.Count > good ? $"  ({groups.Count - good} with errors will be skipped)" : ""), GUILayout.Height(30)))
                    BuildAll();
                GUI.enabled = good > 0 && good == groups.Count;
                if (GUILayout.Button(new GUIContent("Prefabs + bundles + mod", "Builds the prefabs, then opens the EFT Mod Builder and runs 'Build bundles + mod' " +
                        "(LZ4 bundles of this mod only, then the SPT mod folder). Needs the Mod Builder's folders / mod name set once."), GUILayout.Height(30), GUILayout.Width(190)))
                {
                    if (BuildAll()) EditorApplication.delayCall += EFTModBuilderWindow.RunPipeline;
                    else AppendLog("Pipeline stopped: not every prefab was built.");
                }
            }
            GUI.enabled = true;

            if (_log.Length > 0)
            {
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(110));
                EditorGUILayout.SelectableLabel(_log, EditorStyles.textArea, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        void DrawSettings()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Settings", true);
            if (!_showSettings) return;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Name words per part (comma separated)", EditorStyles.boldLabel);
                foreach (var p in Parts) _aliases[p] = EditorGUILayout.TextField(p.ToString(), _aliases[p]);

                EditorGUILayout.LabelField("Prefab name  ({model} {part} {variant})", EditorStyles.boldLabel);
                _prefabPattern = EditorGUILayout.TextField("Prefab", _prefabPattern);

                _setBundleNames = EditorGUILayout.ToggleLeft("Set AssetBundle name (variant 'bundle')", _setBundleNames);
                GUI.enabled = _setBundleNames;
                foreach (var p in Parts) _bundlePatterns[p] = EditorGUILayout.TextField(p.ToString(), _bundlePatterns[p]);
                GUI.enabled = true;

                _addHotObject = EditorGUILayout.ToggleLeft("Add HotObject (thermal) with vanilla-typical values", _addHotObject);
                _addHolster = EditorGUILayout.ToggleLeft(new GUIContent("Pants: add pistol holster (LegsView)",
                    "Without it the game shows no pistol on the leg. Position: vanilla default, or a 'pistol_holster' object parented to Base HumanRThigh1/LThigh1 in your model."), _addHolster);
                if (_addHolster)
                    _holsterLeft = EditorGUILayout.Popup("   Holster leg (default)", _holsterLeft ? 1 : 0, new[] { "Right (vanilla default)", "Left" }) == 1;

                _showMaterialSettings = EditorGUILayout.Foldout(_showMaterialSettings, "Materials", true);
                if (_showMaterialSettings)
                {
                    _fixMaterialsOnScan = EditorGUILayout.ToggleLeft("Fix materials automatically when scanning", _fixMaterialsOnScan);
                    _mat.Mode = (ShaderMode)EditorGUILayout.EnumPopup(new GUIContent("Shaders", "EFT: the game's own shaders (via the SDK stubs) with vanilla values - what vanilla clothing uses.\nStandard: Unity Standard shader (the game has no Standard shader; kept as a fallback)."), _mat.Mode);
                    if (_mat.Mode == ShaderMode.EFT)
                    {
                        _mat.ReapplyValues = EditorGUILayout.ToggleLeft(new GUIContent("Re-apply vanilla values to materials already on EFT shaders", "Off: only textures are updated on re-runs, so your own tweaks stay."), _mat.ReapplyValues);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PrefixLabel(new GUIContent("Specular mask", "Albedo alpha (= specular + reflection strength in EFT's shader) = gloss x slope + offset. Vanilla fit: 0.4 / 0.03."));
                            _mat.MaskSlope = EditorGUILayout.FloatField(_mat.MaskSlope, GUILayout.Width(50));
                            GUILayout.Label("x gloss +", GUILayout.Width(60));
                            _mat.MaskOffset = EditorGUILayout.FloatField(_mat.MaskOffset, GUILayout.Width(50));
                        }
                    }
                    _mat.Extract = EditorGUILayout.ToggleLeft("Extract materials embedded in the model (into <model folder>/Materials)", _mat.Extract);
                    _mat.Normals = (NormalMode)EditorGUILayout.EnumPopup(new GUIContent("Normal maps", "Auto-detect reads each _n texture and picks OpenGL/DirectX; DirectX gets Flip Green Channel"), _mat.Normals);
                    if (_mat.Mode == ShaderMode.Standard)
                        _mat.UseGloss = EditorGUILayout.ToggleLeft("Use gloss (_g) / roughness (_r) maps for smoothness (writes <base>_ms.png)", _mat.UseGloss);
                    _mat.CutoutKeywords = EditorGUILayout.TextField(new GUIContent("Cutout if name has", "Materials whose name or albedo texture contains one of these become Cutout; all others Opaque"), _mat.CutoutKeywords);
                    _mat.Cutoff = EditorGUILayout.Slider("Alpha cutoff", _mat.Cutoff, 0.01f, 0.99f);
                    _mat.HighQualityCompression = EditorGUILayout.ToggleLeft(new GUIContent("High-quality compression (BC7) for COD2EFT textures",
                        "Off (default): Unity's DXT5 / DXT1, the formats vanilla EFT uses. On: BC7 - fewer compression blocks on smooth " +
                        "gradients (skin, gloss); the gloss map doubles in size. Turning it off again removes only overrides this tool set."),
                        _mat.HighQualityCompression);
                    foreach (var r in EFTMaterialCore.DefaultSuffixes.Keys.ToList())
                        _mat.Suffixes[r] = EditorGUILayout.TextField(r + " suffixes", _mat.Suffixes[r]);
                }

                if (GUILayout.Button("Reset to defaults", GUILayout.Width(140)))
                {
                    foreach (var p in Parts)
                    {
                        _aliases[p] = EFTAutoPrefabCore.DefaultAliases[p];
                        _bundlePatterns[p] = EFTAutoPrefabCore.DefaultBundlePatterns[p];
                    }
                    _prefabPattern = EFTAutoPrefabCore.DefaultPrefabPattern;
                    _setBundleNames = true;
                    _addHotObject = true;
                    _fixMaterialsOnScan = true;
                    _mat.Extract = true; _mat.Normals = NormalMode.AutoDetect; _mat.UseGloss = true;
                    _mat.Mode = ShaderMode.EFT; _mat.ReapplyValues = false;
                    _mat.MaskSlope = EFTMaterialCore.DefaultMaskSlope; _mat.MaskOffset = EFTMaterialCore.DefaultMaskOffset;
                    _addHolster = true; _holsterLeft = false;
                    _mat.CutoutKeywords = EFTMaterialCore.DefaultCutoutKeywords; _mat.Cutoff = EFTMaterialCore.DefaultCutoff;
                    _mat.HighQualityCompression = false;
                    foreach (var r in EFTMaterialCore.DefaultSuffixes.Keys.ToList()) _mat.Suffixes[r] = EFTMaterialCore.DefaultSuffixes[r];
                    GUI.FocusControl(null);
                }
            }
            if (EditorGUI.EndChangeCheck()) SaveSettings();
        }

        void DrawSource(Source s)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                s.Foldout = EditorGUILayout.Foldout(s.Foldout, $"{(s.IsAsset ? "Model" : "Scene object")}: {s.Root.name}", true);
                if (!s.Foldout) return;

                s.ModelName = EditorGUILayout.TextField("Model name", s.ModelName);
                using (new EditorGUILayout.HorizontalScope())
                {
                    s.OutputFolder = EditorGUILayout.TextField("Output folder", s.OutputFolder);
                    if (GUILayout.Button("...", GUILayout.Width(28)))
                    {
                        string abs = EditorUtility.OpenFolderPanel("Prefab output folder", Application.dataPath, "");
                        if (!string.IsNullOrEmpty(abs))
                        {
                            abs = abs.Replace('\\', '/');
                            string data = Application.dataPath.Replace('\\', '/');
                            if (abs.StartsWith(data)) s.OutputFolder = "Assets" + abs.Substring(data.Length);
                            else AppendLog("Output folder must be inside this project's Assets folder.");
                        }
                    }
                }

                // header
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("", GUILayout.Width(18));
                    GUILayout.Label("Mesh object", EditorStyles.miniBoldLabel, GUILayout.Width(200));
                    GUILayout.Label("Part", EditorStyles.miniBoldLabel, GUILayout.Width(70));
                    GUILayout.Label("Variant", EditorStyles.miniBoldLabel, GUILayout.Width(50));
                    GUILayout.Label("Piece", EditorStyles.miniBoldLabel, GUILayout.Width(110));
                    GUILayout.Label("LOD", EditorStyles.miniBoldLabel, GUILayout.Width(34));
                    GUILayout.Label(new GUIContent("State", "Base, or an alternative mesh the game swaps in: Armor / Vest (tops), FaceCover (heads). Name suffix _armor / _vest / _facecover."), EditorStyles.miniBoldLabel, GUILayout.Width(74));
                    GUILayout.Label("Bones", EditorStyles.miniBoldLabel, GUILayout.Width(40));
                    GUILayout.Label("Status", EditorStyles.miniBoldLabel);
                }

                foreach (var r in s.Rows)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        r.Include = EditorGUILayout.Toggle(r.Include, GUILayout.Width(18));
                        if (GUILayout.Button(new GUIContent(r.MeshName, "Click to ping"), EditorStyles.label, GUILayout.Width(200)))
                            EditorGUIUtility.PingObject(r.Smr);
                        var np = (BodyPart)EditorGUILayout.EnumPopup(r.Part, GUILayout.Width(70));
                        if (np != r.Part) { r.Part = np; if (np != BodyPart.Ignore) r.Include = true; }
                        r.Variant = EditorGUILayout.TextField(r.Variant, GUILayout.Width(50));
                        r.Piece = EditorGUILayout.TextField(r.Piece, GUILayout.Width(110));
                        r.Lod = Mathf.Max(0, EditorGUILayout.IntField(r.Lod, GUILayout.Width(34)));
                        r.State = (MeshState)EditorGUILayout.EnumPopup(r.State, GUILayout.Width(74));
                        GUILayout.Label(r.BoneCount.ToString(), GUILayout.Width(40));
                        if (r.Errors.Count > 0) GUILayout.Label("ERROR: " + string.Join(" ", r.Errors), _err);
                        else if (r.Warnings.Count > 0) GUILayout.Label("OK, " + string.Join(" ", r.Warnings), _warn);
                        else GUILayout.Label("OK", _ok);
                    }
                }

                var groups = BuildGroups(s);
                if (groups.Count > 0)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Will build:", EditorStyles.miniBoldLabel);
                }
                foreach (var g in groups)
                {
                    int lods = g.Rows.Select(r => r.Lod).Distinct().Count();
                    int pieces = g.Rows.Select(r => r.Piece).Distinct().Count();
                    int alts = g.Slots.Sum(x => x.States.Count);
                    string line = $"  {g.PrefabPath}   ({pieces} piece(s), {lods} LOD(s)" + (alts > 0 ? $", {alts} alternative mesh(es)" : "") + ")" +
                                  (_setBundleNames ? $"   bundle: {g.BundleName}.bundle" : "");
                    GUILayout.Label(line, g.Errors.Count > 0 ? _err : EditorStyles.miniLabel);
                    foreach (var e in g.Errors) GUILayout.Label("      ERROR: " + e, _err);
                    foreach (var w in g.Warnings) GUILayout.Label("      note: " + w, _warn);
                }
            }
        }
    }
}
