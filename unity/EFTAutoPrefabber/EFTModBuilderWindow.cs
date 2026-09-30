// EFT Mod Builder - turns the clothing/head/hands bundles you built into a complete SPT 4.1 + WTT-CommonLib mod:
//   <mods>/<Mod>/bundles/...            copied from your AssetBundle build folder
//   <mods>/<Mod>/bundles.json           manifest with dependency keys (defaults + references found inside each bundle)
//   <mods>/<Mod>/db/CustomClothing/Clothes.json   tops (with the hands they use) and bottoms
//   <mods>/<Mod>/db/CustomHeads/Heads.json
//   <mods>/<Mod>/<Mod>.dll + modinfo.json  server mod (template DLL, made unique per mod; metadata read from modinfo.json)
//   <mods>/<Mod>/modbuilder.json        this window's saved settings + ids (ids stay the same on every rebuild)
// "Build bundles + mod" also builds the AssetBundles first (LZ4, only this mod's bundles; see EFTBundleBuilder).
// "Clean" (and automatically on build) removes stale leftovers - see Clean() for exactly what it touches.
// Menu: Custom Windows > EFT Mod Builder

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Skinning;
using EFT.Visual;
using UnityEditor;
using UnityEngine;

namespace EFTAutoPrefab
{
    [Serializable]
    public class ModItemState
    {
        public string bundleKey = "";
        public string prefabPath = "";
        public int kind;                      // ClothingKind
        public bool include = true;
        public bool missing;                  // not found in the project any more
        public bool foldout = true;
        public string name = "";
        public string description = "";
        public string handsKey = "";          // tops: bundle key of the hands they use
        public bool customHands;              // handsKey typed by hand (bundle from the game or another mod)
        public bool defaultHands;             // 1.7.4: handsKey picked automatically (the top has no hands of its own)
        public bool customSide;               // false = WTT default sides
        public bool usec = true, bear = true, savage;
        public int trader;                    // index into TraderNames, -1 = custom id
        public string customTraderId = "";
        public int loyalty = 1, profileLevel = 1;
        public float standing;
        public int currency;
        public int price = 150;
        public bool addHeadToPlayer = true;
        public string suiteId = "", outfitId = "", topId = "", handsId = "", bottomId = "", headId = "";
    }

    [Serializable]
    public class ModBuilderProject
    {
        public string modName = "";
        public string author = "";
        public string version = "1.0.0";
        public bool autoGuid = true;
        public string guid = "";
        public string license = "NCSA";
        public string sptVersion = "~4.1.0";
        public string wttVersion = "~3.0.3";
        public string url = "";
        public string dependencyKeys = EFTModBuilderCore.DefaultDependencyKeys;
        // defaults for newly found clothing
        public int defTrader;
        public int defLoyalty = 1, defProfileLevel = 1;
        public float defStanding;
        public int defCurrency;
        public int defPrice = 150;
        public List<ModItemState> items = new List<ModItemState>();
        public List<string> generatedFiles = new List<string>();
    }

    public class EFTModBuilderWindow : EditorWindow
    {
        [MenuItem("Custom Windows/EFT Mod Builder")]
        public static void Open()
        {
            var w = GetWindow<EFTModBuilderWindow>();
            w.titleContent = new GUIContent("EFT Mod Builder");
            w.minSize = new Vector2(620, 480);
        }

        const string Pref = "EFTModBuilder.";
        const string ProjectFile = "modbuilder.json";

        [SerializeField] ModBuilderProject _p = new ModBuilderProject();
        [SerializeField] string _buildFolder = "";
        [SerializeField] string _modsFolder = "";
        [SerializeField] string _modFolderName = "";
        [SerializeField] string _gameFolder = "";
        [SerializeField] string _log = "";
        [SerializeField] string _projectFor = "";   // the mod folder _p belongs to
        bool _showRemoved;
        BundleCompression _compression = BundleCompression.LZ4;
        bool _win64;
        bool _forceRebuild;
        bool _autoClean = true;
        Vector2 _scroll, _logScroll;
        bool _showMod = true, _showDefaults;
        readonly List<string> _errors = new List<string>();
        readonly List<string> _warnings = new List<string>();
        readonly List<string> _notBuilt = new List<string>();   // 1.7.4: bundles never built yet (a note until a real build)
        double _lastValidate;

        static readonly string[] KindNames = { "?", "Top", "Bottom", "Head", "Hands" };

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_buildFolder)) _buildFolder = EditorPrefs.GetString(Pref + "buildFolder", GuessBuildFolder());
            if (string.IsNullOrEmpty(_modsFolder)) _modsFolder = EditorPrefs.GetString(Pref + "modsFolder", "");
            if (string.IsNullOrEmpty(_modFolderName)) _modFolderName = EditorPrefs.GetString(Pref + "modFolderName", "");
            if (string.IsNullOrEmpty(_gameFolder)) _gameFolder = EditorPrefs.GetString(Pref + "gameFolder", "");
            _compression = (BundleCompression)EditorPrefs.GetInt(Pref + "compression", (int)BundleCompression.LZ4);
            _win64 = EditorPrefs.GetBool(Pref + "win64", false);
            _forceRebuild = EditorPrefs.GetBool(Pref + "forceRebuild", false);
            _autoClean = EditorPrefs.GetBool(Pref + "autoClean", true);
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(Pref + "buildFolder", _buildFolder ?? "");
            EditorPrefs.SetString(Pref + "modsFolder", _modsFolder ?? "");
            EditorPrefs.SetString(Pref + "modFolderName", _modFolderName ?? "");
            EditorPrefs.SetString(Pref + "gameFolder", _gameFolder ?? "");
            EditorPrefs.SetInt(Pref + "compression", (int)_compression);
            EditorPrefs.SetBool(Pref + "win64", _win64);
            EditorPrefs.SetBool(Pref + "forceRebuild", _forceRebuild);
            EditorPrefs.SetBool(Pref + "autoClean", _autoClean);
        }

        static string GuessBuildFolder()
        {
            foreach (var c in new[] { "Assets/RCTA Working Folder/Build", "AssetBundles/StandaloneWindows", "AssetBundles" })
                if (Directory.Exists(c)) return c;
            return "";
        }

        string ModFolder => string.IsNullOrWhiteSpace(_modsFolder) || string.IsNullOrWhiteSpace(_modFolderName)
            ? "" : Path.Combine(_modsFolder, EFTModBuilderCore.SafeFolderName(_modFolderName));

        string BuildFolderAbs => string.IsNullOrWhiteSpace(_buildFolder) ? "" : Path.GetFullPath(_buildFolder);

        string Guid => _p.autoGuid ? EFTModBuilderCore.MakeGuid(_p.author, _p.modName) : (_p.guid ?? "").Trim();

        void Log(string s)
        {
            _log = (_log.Length > 0 ? _log + "\n" : "") + s;
            Debug.Log("[EFT Mod Builder] " + s);
            Repaint();
        }

        // ------------------------------------------------------------------ load / scan

        static string FolderKey(string f) => string.IsNullOrEmpty(f) ? "" : Path.GetFullPath(f).TrimEnd('\\', '/').ToLowerInvariant();

        /// <summary>
        /// The item list, ids and generated-file list belong to ONE mod folder (its modbuilder.json). Switching to another
        /// mod folder loads that folder's modbuilder.json, or starts a fresh list (author, versions and defaults are kept),
        /// so bundles of a previous mod never show up in the next one.
        /// </summary>
        void LoadProjectFromModFolder()
        {
            string f = string.IsNullOrEmpty(ModFolder) ? "" : Path.Combine(ModFolder, ProjectFile);
            bool sameMod = FolderKey(ModFolder) == _projectFor;
            if (f.Length > 0 && File.Exists(f))
            {
                try
                {
                    var loaded = JsonUtility.FromJson<ModBuilderProject>(File.ReadAllText(f));
                    if (loaded != null) { _p = loaded; Log("Loaded settings and ids from " + f); }
                }
                catch (Exception e) { Log("Could not read " + f + ": " + e.Message); }
            }
            else if (!sameMod && !string.IsNullOrEmpty(ModFolder))
            {
                var old = _p;
                _p = new ModBuilderProject
                {
                    modName = _modFolderName, author = old.author, version = "1.0.0", license = old.license,
                    sptVersion = old.sptVersion, wttVersion = old.wttVersion, dependencyKeys = old.dependencyKeys,
                    defTrader = old.defTrader, defLoyalty = old.defLoyalty, defProfileLevel = old.defProfileLevel,
                    defStanding = old.defStanding, defCurrency = old.defCurrency, defPrice = old.defPrice,
                };
                Log($"New mod '{_modFolderName}': started a fresh bundle list (nothing from the previous mod carried over).");
            }
            else if (string.IsNullOrEmpty(_p.modName) && !string.IsNullOrEmpty(_modFolderName))
                _p.modName = _modFolderName;
            _projectFor = FolderKey(ModFolder);
            ScanBundles();
        }

        void ScanBundles()
        {
            var aliases = new Dictionary<BodyPart, string>();
            foreach (var kv in EFTAutoPrefabCore.DefaultAliases)
                aliases[kv.Key] = EditorPrefs.GetString("EFTAutoPrefab.alias." + kv.Key, kv.Value);

            var found = new HashSet<string>();
            foreach (var bundle in AssetDatabase.GetAllAssetBundleNames())
            {
                if (!bundle.EndsWith(".bundle")) continue;
                GameObject prefab = null;
                foreach (var ap in AssetDatabase.GetAssetPathsFromAssetBundle(bundle))
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(ap);
                    if (go != null && go.GetComponentInChildren<LoddedSkin>(true) != null) { prefab = go; break; }
                }
                if (prefab == null) continue; // not a clothing/head/hands bundle

                bool? rootJoint = null;
                var skin = prefab.GetComponentInChildren<Skin>(true);
                if (skin != null)
                {
                    var bp = new SerializedObject(skin).FindProperty("_bonePaths");
                    if (bp != null && bp.arraySize > 0)
                        rootJoint = bp.GetArrayElementAtIndex(0).stringValue.StartsWith("Root_Joint/");
                }
                var kind = EFTModBuilderCore.Classify(prefab.name, bundle, rootJoint, aliases);
                found.Add(bundle);

                var st = _p.items.FirstOrDefault(i => i.bundleKey == bundle);
                if (st == null)
                {
                    st = new ModItemState
                    {
                        bundleKey = bundle,
                        kind = (int)kind,
                        name = Nice(prefab.name),
                        description = Nice(prefab.name),
                        trader = _p.defTrader, loyalty = _p.defLoyalty, profileLevel = _p.defProfileLevel,
                        standing = _p.defStanding, currency = _p.defCurrency, price = _p.defPrice,
                        customSide = kind == ClothingKind.Head, usec = true, bear = true, savage = false,
                        include = kind != ClothingKind.Unknown,
                    };
                    _p.items.Add(st);
                }
                else if (st.kind == (int)ClothingKind.Unknown) st.kind = (int)kind;
                st.prefabPath = AssetDatabase.GetAssetPath(prefab);
                st.missing = false;
            }
            foreach (var st in _p.items.Where(i => !found.Contains(i.bundleKey))) st.missing = true;
            // bundles that are gone from the project and were never part of this mod's build are just leftovers: forget them.
            // Ones this mod has shipped keep their ids (listed under "No longer in the project") in case the prefab comes back.
            var shippedBefore = new HashSet<string>(_p.generatedFiles.Select(Norm));
            int forgotten = _p.items.RemoveAll(i => i.missing && !shippedBefore.Contains(Norm("bundles/" + i.bundleKey)));
            if (forgotten > 0) Log($"Forgot {forgotten} bundle(s) that are no longer in the project and were never built into this mod.");

            AutoPickHands();
            _p.items = _p.items.OrderBy(i => i.kind == 0 ? 9 : i.kind).ThenBy(i => i.bundleKey).ToList();
            Log($"Found {found.Count} clothing/head/hands bundle(s) in the project.");
        }

        List<KeyValuePair<string, string>> _gameHands;
        string _gameHandsFrom;

        /// <summary>
        /// Vanilla first-person hands from SPT's database (SPT_Data/database/templates/customization.json, BodyPart "Hands"):
        /// name -> Prefab.path. WTT uses handsBundlePath as the hands' Prefab.path, exactly like these vanilla entries.
        /// </summary>
        List<KeyValuePair<string, string>> GameHands()
        {
            string rt = SptRuntime();
            string f = rt == null ? null : Path.Combine(rt, "SPT_Data", "database", "templates", "customization.json");
            if (_gameHands != null && _gameHandsFrom == f) return _gameHands;
            _gameHands = new List<KeyValuePair<string, string>>();
            _gameHandsFrom = f;
            if (f == null || !File.Exists(f)) return _gameHands;
            try
            {
                string text = File.ReadAllText(f);
                // entries look like: "_name": "usec_hands_cereum", ... "_props": { ... "BodyPart": "Hands", ... "Prefab": { "path": "..." } }
                var rx = new System.Text.RegularExpressions.Regex(
                    "\"_name\"\\s*:\\s*\"([^\"]+)\"[^{}]*?\"_props\"\\s*:\\s*\\{[^{}]*?\"BodyPart\"\\s*:\\s*\"Hands\"[^{}]*?\"Prefab\"\\s*:\\s*\\{\\s*\"path\"\\s*:\\s*\"([^\"]+\\.bundle)\"");
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
                    _gameHands.Add(new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value));
                _gameHands = _gameHands.GroupBy(kv => kv.Value).Select(g => g.First()).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) { Log("Could not read the game's hands list: " + e.Message); }
            return _gameHands;
        }

        static string Nice(string prefabName) => (prefabName ?? "").Replace('_', ' ').Trim();

        /// <summary>
        /// Tops without hands get the hands bundle whose key shares the same "{model}_" prefix, if exactly one does
        /// (else the project's only hands bundle). 1.7.4: if that finds nothing, the game's default USEC hands are
        /// picked as a default the user can change (a top without hands used to block the build). A top that got
        /// the default switches to its own hands once a matching hands bundle appears.
        /// </summary>
        void AutoPickHands()
        {
            var hands = _p.items.Where(i => i.kind == (int)ClothingKind.Hands && !i.missing).ToList();
            foreach (var top in _p.items.Where(i => i.kind == (int)ClothingKind.Top && !i.customHands &&
                                                    (string.IsNullOrEmpty(i.handsKey) || i.defaultHands)))
            {
                string stem = Stem(top.bundleKey);
                var match = hands.Where(h => Stem(h.bundleKey) == stem).ToList();
                string own = match.Count == 1 ? match[0].bundleKey : hands.Count == 1 ? hands[0].bundleKey : null;
                if (own != null)
                {
                    if (top.defaultHands) Log($"'{top.bundleKey}': now uses this mod's own hands '{own}' instead of the default.");
                    top.handsKey = own; top.defaultHands = false;
                }
                else if (string.IsNullOrEmpty(top.handsKey))
                {
                    var def = DefaultGameHands();
                    top.handsKey = def.Value; top.defaultHands = true;
                    Log($"'{top.bundleKey}' has no hands bundle of its own: using the game's {def.Key} ({def.Value}) by default. " +
                        "Change it under the top's 'Hands (arms)' if you like.");
                }
            }
        }

        // DefaultUsecHands / DefaultBearHands as the game's catalog lists them (Inspector outfit catalog, from_pc/20260929-071416)
        const string DefaultUsecHandsPath = "assets/content/hands/usec/usec_hands_skin.bundle";
        const string DefaultBearHandsPath = "assets/content/hands/bear/bear_hands_skin.bundle";

        /// <summary>The game's plain hands: USEC's default, else BEAR's, from SPT's customization.json when it can be read.</summary>
        KeyValuePair<string, string> DefaultGameHands()
        {
            var gh = GameHands();
            foreach (var name in new[] { "DefaultUsecHands", "DefaultBearHands" })
                foreach (var kv in gh)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv;
            foreach (var path in new[] { DefaultUsecHandsPath, DefaultBearHandsPath })
                foreach (var kv in gh)
                    if (string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase)) return kv;
            return new KeyValuePair<string, string>("DefaultUsecHands", DefaultUsecHandsPath);
        }

        static bool IsKnownGameHands(string key) =>
            string.Equals(key, DefaultUsecHandsPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, DefaultBearHandsPath, StringComparison.OrdinalIgnoreCase);

        static string Stem(string key)
        {
            string f = Path.GetFileNameWithoutExtension(key ?? "");
            int i = f.LastIndexOf('_');
            return i > 0 ? f.Substring(0, i) : f;
        }

        // ------------------------------------------------------------------ validation

        IEnumerable<ModItemState> Included => _p.items.Where(i => i.include && !i.missing);

        List<ModItemState> UsedHands()
        {
            var keys = new HashSet<string>(Included.Where(i => i.kind == (int)ClothingKind.Top).Select(i => i.handsKey));
            return _p.items.Where(i => i.kind == (int)ClothingKind.Hands && !i.missing && keys.Contains(i.bundleKey)).ToList();
        }

        /// <summary>Bundles that go into the mod: included tops/bottoms/heads + the hands those tops use.</summary>
        List<ModItemState> ShippedBundles() =>
            Included.Where(i => i.kind == (int)ClothingKind.Top || i.kind == (int)ClothingKind.Bottom || i.kind == (int)ClothingKind.Head)
                    .Concat(UsedHands()).GroupBy(i => i.bundleKey).Select(g => g.First()).ToList();

        void Validate() => Validate(true);

        /// <summary>requireBuilt = false: skip the checks on built bundle files (used before building them).
        /// notBuiltAsNote = true (the window's periodic check only): bundles never built go to _notBuilt, not _errors.</summary>
        void Validate(bool requireBuilt, bool notBuiltAsNote = false)
        {
            _errors.Clear(); _warnings.Clear(); _notBuilt.Clear();
            if (string.IsNullOrWhiteSpace(_p.modName)) _errors.Add("Mod name is empty.");
            if (string.IsNullOrWhiteSpace(_p.author)) _errors.Add("Author is empty.");
            if (!EFTModBuilderCore.IsValidSemVer(_p.version)) _errors.Add("Version must look like 1.0.0.");
            string guid = Guid;
            if (!EFTModBuilderCore.SptGuidRule.IsMatch(guid) || !guid.Contains("."))
                _errors.Add("Mod GUID must look like com.author.modname - letters, digits, '-' and '.', no spaces or '_' (SPT refuses to load ANY mod if one GUID is invalid).");
            if (string.IsNullOrEmpty(ModFolder)) _errors.Add("Set the SPT mods folder and the mod folder name.");
            else if (!Directory.Exists(_modsFolder)) _errors.Add("SPT mods folder does not exist: " + _modsFolder);
            if (string.IsNullOrEmpty(BuildFolderAbs)) _errors.Add("Set the AssetBundle build folder.");
            else if (requireBuilt && !Directory.Exists(BuildFolderAbs)) _errors.Add("AssetBundle build folder does not exist: " + _buildFolder);

            var shipped = ShippedBundles();
            if (!Included.Any(i => i.kind == (int)ClothingKind.Top || i.kind == (int)ClothingKind.Bottom || i.kind == (int)ClothingKind.Head))
                _errors.Add("Nothing to build: include at least one top, bottom or head.");

            foreach (var it in Included)
            {
                string label = $"'{it.bundleKey}'";
                var k = (ClothingKind)it.kind;
                if (k == ClothingKind.Unknown) { _errors.Add(label + ": choose Top / Bottom / Head / Hands."); continue; }
                if (k == ClothingKind.Hands) continue;
                if (string.IsNullOrWhiteSpace(it.name)) _errors.Add(label + ": name is empty.");
                if (k == ClothingKind.Head)
                {
                    if (!it.usec && !it.bear) _errors.Add(label + ": pick at least one side (USEC/BEAR).");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(it.description)) _errors.Add(label + ": description is empty (WTT requires it).");
                if (it.price <= 0) _errors.Add(label + ": price must be above 0 (WTT requires it).");
                if (it.loyalty < 0 || it.loyalty > 4) _errors.Add(label + ": loyalty level must be 0-4.");
                if (it.profileLevel < 0) _errors.Add(label + ": profile level cannot be negative.");
                if (it.trader < 0 && !EFTModBuilderCore.IsMongoId(it.customTraderId)) _errors.Add(label + ": custom trader id must be 24 hex characters.");
                if (it.customSide && !it.usec && !it.bear && !it.savage) _errors.Add(label + ": pick at least one side.");
                if (k == ClothingKind.Top)
                {
                    if (string.IsNullOrWhiteSpace(it.handsKey)) _errors.Add(label + ": choose the hands (arms) this top uses.");
                    else
                    {
                        var h = _p.items.FirstOrDefault(x => x.bundleKey == it.handsKey);
                        if (h == null && !IsKnownGameHands(it.handsKey) && !GameHands().Any(g => g.Value == it.handsKey))
                            _warnings.Add(label + $": hands '{it.handsKey}' is neither one of this project's bundles nor a game hands bundle - it must come from another mod (not verified).");
                        else if (h != null && h.missing) _errors.Add(label + $": hands bundle '{it.handsKey}' is missing from the project.");
                    }
                }
            }

            // files present and fresh. 1.7.4: a bundle this mod has never shipped and that isn't built yet is normal before the
            // first build - the periodic check (notBuiltAsNote) lists it as a note; the real build still refuses it.
            var shippedBefore = new HashSet<string>(_p.generatedFiles.Select(Norm));
            foreach (var it in requireBuilt ? shipped : new List<ModItemState>())
            {
                string src = Path.Combine(BuildFolderAbs, it.bundleKey);
                if (!File.Exists(src))
                {
                    if (notBuiltAsNote && !shippedBefore.Contains(Norm("bundles/" + it.bundleKey)))
                        _notBuilt.Add($"'{it.bundleKey}' is not built yet - 'Build bundles + mod' builds it.");
                    else
                        _errors.Add($"'{it.bundleKey}' has not been built (no file at {src}). Use 'Build bundles + mod'.");
                    continue;
                }
                if (!string.IsNullOrEmpty(it.prefabPath) && File.Exists(it.prefabPath) &&
                    File.GetLastWriteTimeUtc(it.prefabPath) > File.GetLastWriteTimeUtc(src))
                    _warnings.Add($"'{it.bundleKey}': the prefab changed after the bundle was built - use 'Build bundles + mod' to include the change.");
            }
            foreach (var h in _p.items.Where(i => i.kind == (int)ClothingKind.Hands && i.include && !i.missing).Except(UsedHands()))
                _warnings.Add($"Hands '{h.bundleKey}' is not used by any included top, so it is left out.");

            // key clashes with other mods (SPT: "Unable to add bundle")
            if (Directory.Exists(_modsFolder))
            {
                string mine = string.IsNullOrEmpty(ModFolder) ? "" : Path.GetFullPath(ModFolder).TrimEnd('\\', '/');
                var ourKeys = new HashSet<string>(shipped.Select(s => s.bundleKey));
                foreach (var dir in Directory.GetDirectories(_modsFolder))
                {
                    if (Path.GetFullPath(dir).TrimEnd('\\', '/').Equals(mine, StringComparison.OrdinalIgnoreCase)) continue;
                    string bj = Path.Combine(dir, "bundles.json");
                    if (!File.Exists(bj)) continue;
                    try
                    {
                        foreach (var key in EFTModBuilderCore.ReadManifestKeys(File.ReadAllText(bj)).Where(ourKeys.Contains))
                            _errors.Add($"Bundle key '{key}' is already used by mod folder '{Path.GetFileName(dir)}'. SPT refuses duplicate keys - rename the bundle (Auto Prefabber settings) and rebuild.");
                    }
                    catch { /* unreadable manifest: ignore */ }
                }
            }

            // never overwrite somebody else's mod
            if (!string.IsNullOrEmpty(ModFolder) && Directory.Exists(ModFolder))
            {
                // 1.7.2: a folder with files but without this builder's project file was not made by it (an asset-only mod
                // would have had its bundles.json / modinfo.json / db files overwritten - the DLL check below missed it)
                if (!File.Exists(Path.Combine(ModFolder, ProjectFile)) && Directory.EnumerateFileSystemEntries(ModFolder).Any())
                    _errors.Add($"'{ModFolder}' already has files and was not made by this builder (no {ProjectFile}) - it may be another mod. Use a new mod folder name.");
                var ours = new HashSet<string>(_p.generatedFiles.Select(Norm));
                foreach (var dll in Directory.GetFiles(ModFolder, "*.dll", SearchOption.TopDirectoryOnly))
                    if (!ours.Contains(Norm(Path.GetFileName(dll))))
                        _errors.Add($"'{ModFolder}' already contains '{Path.GetFileName(dll)}', which this builder did not create. Use a new mod folder name.");
            }
        }

        static string Norm(string rel) => (rel ?? "").Replace('\\', '/').ToLowerInvariant();

        // ------------------------------------------------------------------ dependency detection

        Dictionary<string, string> _cabToKey;
        string _cabIndexFor;

        string StreamingWindows()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_gameFolder)) candidates.Add(_gameFolder);
            if (!string.IsNullOrWhiteSpace(_modsFolder))
            {
                try
                {
                    var d = new DirectoryInfo(_modsFolder);          // .../<server>/user/mods
                    for (int i = 0; i < 4 && d != null; i++) { candidates.Add(d.FullName); d = d.Parent; }
                }
                catch { }
            }
            foreach (var c in candidates)
            {
                string p = Path.Combine(c, "EscapeFromTarkov_Data", "StreamingAssets", "Windows");
                if (Directory.Exists(p)) return p;
            }
            return null;
        }

        string CabIndexCachePath => Path.Combine("Library", "EFTModBuilder_CabIndex.txt");

        Dictionary<string, string> CabIndex()
        {
            string sw = StreamingWindows();
            if (sw == null) return null;
            if (_cabToKey != null && _cabIndexFor == sw) return _cabToKey;
            _cabToKey = new Dictionary<string, string>();
            _cabIndexFor = sw;
            if (File.Exists(CabIndexCachePath))
            {
                foreach (var line in File.ReadAllLines(CabIndexCachePath))
                {
                    int t = line.IndexOf('\t');
                    if (t > 0) _cabToKey[line.Substring(0, t)] = line.Substring(t + 1);
                }
            }
            foreach (var key in EFTModBuilderCore.KnownDependencyKeys)
            {
                string f = Path.Combine(sw, key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) continue;
                var bi = EFTModBuilderCore.ReadBundle(f, true);
                foreach (var cab in bi.Cabs) _cabToKey[cab] = key;
            }
            return _cabToKey;
        }

        void DeepScanGame()
        {
            string sw = StreamingWindows();
            if (sw == null) { Log("EFT StreamingAssets/Windows not found - set the EFT game folder."); return; }
            string manifest = Path.Combine(sw, "Windows.json");
            if (!File.Exists(manifest)) { Log("Windows.json not found in " + sw); return; }
            var keys = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(manifest), "\"([^\"]+)\":\\{\"FileName\"")
                        .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
            var idx = CabIndex() ?? new Dictionary<string, string>();
            try
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar("Indexing game bundles", keys[i], (float)i / keys.Count))
                    { Log("Deep scan cancelled."); break; }
                    string f = Path.Combine(sw, keys[i].Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(f)) continue;
                    var bi = EFTModBuilderCore.ReadBundle(f, true);
                    foreach (var cab in bi.Cabs) idx[cab] = keys[i];
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            File.WriteAllLines(CabIndexCachePath, idx.Select(kv => kv.Key + "\t" + kv.Value));
            Log($"Indexed {idx.Count} game bundle CABs (cached in {CabIndexCachePath}).");
        }

        List<string> DependencyKeysFor(string bundleFile, List<string> defaults, List<string> notes)
        {
            var keys = new List<string>(defaults);
            var bi = EFTModBuilderCore.ReadBundle(bundleFile, false);
            if (bi.Error != null) { notes.Add($"{Path.GetFileName(bundleFile)}: could not scan ({bi.Error}); using the default dependency keys only."); return keys; }
            if (bi.ExternalCabs.Count == 0) return keys;
            var idx = CabIndex();
            foreach (var cab in bi.ExternalCabs)
            {
                if (idx != null && idx.TryGetValue(cab, out var key)) { if (!keys.Contains(key)) keys.Add(key); }
                else notes.Add($"{Path.GetFileName(bundleFile)} references {cab}, which is not a known game bundle" +
                               (idx == null ? " (EFT game folder not found)." : ". Run 'Deep scan game bundles' once, or add its key to the dependency keys."));
            }
            return keys;
        }

        // ------------------------------------------------------------------ build

        void BuildMod() => BuildMod(false);

        /// <summary>true when the mod folder was written completely.</summary>
        bool BuildMod(bool alreadyCleaned)
        {
            Validate();
            if (_errors.Count > 0) { Log("Not built - fix the errors listed above."); return false; }
            if (_autoClean && !alreadyCleaned) Clean(true);

            string mod = ModFolder;
            string guid = Guid;
            var generated = new List<string>();
            var notes = new List<string>();
            try
            {
                Directory.CreateDirectory(mod);

                // ids (kept across rebuilds)
                foreach (var it in Included)
                {
                    var k = (ClothingKind)it.kind;
                    if (k == ClothingKind.Top) { Id(ref it.suiteId); Id(ref it.outfitId); Id(ref it.topId); Id(ref it.handsId); }
                    else if (k == ClothingKind.Bottom) { Id(ref it.suiteId); Id(ref it.outfitId); Id(ref it.bottomId); }
                    else if (k == ClothingKind.Head) Id(ref it.headId);
                }

                // bundles + manifest
                var defaults = EFTModBuilderCore.SplitKeys(_p.dependencyKeys);
                var manifest = new List<KeyValuePair<string, List<string>>>();
                var shipped = ShippedBundles();
                for (int i = 0; i < shipped.Count; i++)
                {
                    var it = shipped[i];
                    EditorUtility.DisplayProgressBar("EFT Mod Builder", "Copying " + it.bundleKey, (float)i / Math.Max(1, shipped.Count));
                    string src = Path.Combine(BuildFolderAbs, it.bundleKey);
                    string rel = "bundles/" + it.bundleKey;
                    string dst = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!IsUnder(mod, dst) || !IsUnder(BuildFolderAbs, src)) throw new InvalidOperationException("Bundle key '" + it.bundleKey + "' leads outside its folder.");
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                    generated.Add(rel);
                    manifest.Add(new KeyValuePair<string, List<string>>(it.bundleKey, DependencyKeysFor(src, defaults, notes)));
                }
                WriteText(mod, "bundles.json", EFTModBuilderCore.BundlesJson(manifest), generated);

                // clothing
                var clothing = new List<EFTModBuilderCore.ClothingEntry>();
                foreach (var it in Included.Where(i => i.kind == (int)ClothingKind.Top || i.kind == (int)ClothingKind.Bottom))
                {
                    bool top = it.kind == (int)ClothingKind.Top;
                    clothing.Add(new EFTModBuilderCore.ClothingEntry
                    {
                        Kind = top ? ClothingKind.Top : ClothingKind.Bottom,
                        SuiteId = it.suiteId, OutfitId = it.outfitId,
                        TopId = top ? it.topId : null, HandsId = top ? it.handsId : null, BottomId = top ? null : it.bottomId,
                        Name = it.name.Trim(), Description = it.description.Trim(),
                        BundlePath = it.bundleKey, HandsBundlePath = top ? it.handsKey : null,
                        Side = it.customSide ? Sides(it, true) : null,
                        Trader = it.trader >= 0 ? EFTModBuilderCore.TraderNames[it.trader] : it.customTraderId.Trim(),
                        LoyaltyLevel = it.loyalty, ProfileLevel = it.profileLevel, Standing = it.standing,
                        CurrencyTpl = EFTModBuilderCore.CurrencyTpls[Mathf.Clamp(it.currency, 0, EFTModBuilderCore.CurrencyTpls.Length - 1)],
                        Price = it.price,
                    });
                }
                if (clothing.Count > 0)
                    WriteText(mod, "db/CustomClothing/Clothes.json", EFTModBuilderCore.ClothingJson(clothing), generated);

                // heads
                var heads = Included.Where(i => i.kind == (int)ClothingKind.Head).Select(it => new EFTModBuilderCore.HeadEntry
                {
                    HeadId = it.headId, Name = it.name.Trim(), BundlePath = it.bundleKey,
                    Side = Sides(it, false), AddHeadToPlayer = it.addHeadToPlayer,
                }).ToList();
                if (heads.Count > 0)
                    WriteText(mod, "db/CustomHeads/Heads.json", EFTModBuilderCore.HeadsJson(heads), generated);

                // server DLL + metadata
                string dllName = EFTModBuilderCore.SafeFolderName(EFTModBuilderCore.Slug(_p.modName, "mod")) + ".dll";
                byte[] template = File.ReadAllBytes(TemplatePath());
                File.WriteAllBytes(Path.Combine(mod, dllName), EFTModBuilderCore.PatchModDll(template, guid));
                generated.Add(dllName);
                WriteText(mod, "modinfo.json", EFTModBuilderCore.ModInfoJson(guid, _p.modName.Trim(), _p.author.Trim(), _p.version.Trim(),
                    _p.sptVersion.Trim(), _p.wttVersion.Trim(), _p.license.Trim(), _p.url), generated);

                // remove files an earlier build of this mod created that are no longer part of it
                var now = new HashSet<string>(generated.Select(Norm));
                foreach (var old in _p.generatedFiles.Where(o => !now.Contains(Norm(o))))
                {
                    string f = Path.Combine(mod, old.Replace('/', Path.DirectorySeparatorChar));
                    if (!IsUnder(mod, f)) { Log("Skipped removing '" + old + "': outside the mod folder."); continue; }
                    if (File.Exists(f)) { File.Delete(f); Log("Removed old file " + old); }
                }
                _p.generatedFiles = generated;
                if (_p.autoGuid) _p.guid = guid;
                File.WriteAllText(Path.Combine(mod, ProjectFile), JsonUtility.ToJson(_p, true));
            }
            catch (Exception e)
            {
                Log("BUILD FAILED: " + e.Message);
                Debug.LogException(e);
                return false;
            }
            finally { EditorUtility.ClearProgressBar(); }

            foreach (var n in notes) Log("note: " + n);
            Log($"Built '{_p.modName}' ({guid}) -> {mod}  [{generated.Count} files]");
            return true;
        }

        /// <summary>Builds this mod's bundles (LZ4 by default) into the build folder, then the mod.</summary>
        public bool BuildBundlesAndMod()
        {
            Validate(false);
            if (_errors.Count > 0) { Log("Not built - fix the errors listed above."); return false; }
            if (_autoClean) Clean(true);

            var keys = ShippedBundles().Select(i => i.bundleKey).ToList();
            var log = new List<string>();
            bool ok;
            try
            {
                EditorUtility.DisplayProgressBar("EFT Mod Builder", "Building AssetBundles...", 0.3f);
                ok = EFTBundleBuilder.Build(keys, BuildFolderAbs, _compression,
                                            _win64 ? BuildTarget.StandaloneWindows64 : BuildTarget.StandaloneWindows,
                                            _forceRebuild, log, out var built);
                var missing = keys.Except(built).ToList();
                if (ok && missing.Count > 0) { log.Add("Not built: " + string.Join(", ", missing)); ok = false; }
            }
            finally { EditorUtility.ClearProgressBar(); }
            foreach (var l in log) Log(l);
            if (!ok) { Log("Bundles failed to build; the mod was not changed."); return false; }
            return BuildMod(true);
        }

        /// <summary>One-click pipeline entry (Auto Prefabber): opens this window, rescans the project and builds bundles + mod.</summary>
        public static void RunPipeline()
        {
            var w = GetWindow<EFTModBuilderWindow>();
            w.titleContent = new GUIContent("EFT Mod Builder");
            w.Show();
            if (w._p.items.Count == 0) w.LoadProjectFromModFolder(); else w.ScanBundles();
            if (!w.BuildBundlesAndMod())
                w.Log("Pipeline stopped at the mod step - fill in what the errors above ask for, then press 'Build bundles + mod'.");
            w.Focus();
        }

        // ------------------------------------------------------------------ clean

        /// <summary>SPT_Runtime folder = mods folder's grandparent (…/SPT_Runtime/user/mods), else null.</summary>
        string SptRuntime()
        {
            try
            {
                var mods = new DirectoryInfo(_modsFolder);
                if (mods.Exists && mods.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) &&
                    mods.Parent != null && mods.Parent.Name.Equals("user", StringComparison.OrdinalIgnoreCase) && mods.Parent.Parent != null)
                    return mods.Parent.Parent.FullName;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// What it touches (SPT 4.1.6 behaviour checked in the server/client code):
        ///  1. Unity: removes AssetBundle names no asset uses any more.
        ///  2. Build folder: clothing/head/hands bundles (they contain LoddedSkin) or empty bundles whose name is no longer
        ///     assigned in this project - asks before deleting.
        ///  3. Client bundle cache (SPT_Runtime/user/cache/bundles/&lt;crc&gt;/&lt;key&gt;): the client loads a bundle from there
        ///     instead of the mod folder when a matching entry exists; entries for this mod's keys are deleted.
        ///  (The server's user/cache/bundleHashCache.json is not touched: 4.1.6 re-hashes a bundle whenever its size or
        ///  modified time changes and rewrites the file with only the bundles it loaded, so it cannot serve old data.)
        ///  4. Reports (never deletes) files in this mod's bundles folder that this builder did not create, and - on the
        ///     manual Clean only - other mods that contain a copy of one of this project's prefabs.
        /// </summary>
        void Clean(bool auto)
        {
            int changes = 0;
            int before = AssetDatabase.GetAllAssetBundleNames().Length;
            AssetDatabase.RemoveUnusedAssetBundleNames();
            int removedNames = before - AssetDatabase.GetAllAssetBundleNames().Length;
            if (removedNames > 0) { Log($"Clean: removed {removedNames} unused AssetBundle name(s) from the project."); changes++; }

            // 2. stale bundles in the build folder (never when the build folder is inside the mods or game folder)
            bool buildInsideGame = IsUnder(_modsFolder, BuildFolderAbs) || IsUnder(_gameFolder, BuildFolderAbs) ||
                                   (SptRuntime() is string rtb && IsUnder(Path.GetDirectoryName(rtb), BuildFolderAbs));
            if (buildInsideGame && !string.IsNullOrEmpty(BuildFolderAbs))
                Log("Clean: the build folder is inside the SPT/EFT folders, so old bundles in it are not touched.");
            else if (!string.IsNullOrEmpty(BuildFolderAbs) && Directory.Exists(BuildFolderAbs))
            {
                var current = new HashSet<string>(AssetDatabase.GetAllAssetBundleNames(), StringComparer.OrdinalIgnoreCase);
                var stale = new List<string>();
                string root = BuildFolderAbs.TrimEnd('\\', '/');
                // only the folders the Auto Prefabber's bundle-name patterns write to (default clothing/ and heads/),
                // so unrelated bundles elsewhere in the build folder are never opened
                var dirs = EFTAutoPrefabCore.DefaultBundlePatterns
                    .Select(kv => EditorPrefs.GetString("EFTAutoPrefab.bundle." + kv.Key, kv.Value))
                    .Select(pat => { int i = pat.Replace('\\', '/').LastIndexOf('/'); return i > 0 ? pat.Substring(0, i) : ""; })
                    .Where(d => d.IndexOf('{') < 0).Distinct().ToList();
                var files = dirs.SelectMany(d =>
                {
                    string dir = d.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsUnder(root, dir) || !Directory.Exists(dir)) return new string[0];
                    try { return Directory.GetFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly); } catch { return new string[0]; }
                }).Distinct().Where(f => IsUnder(root, f));
                foreach (var f in files)
                {
                    string key = f.Substring(root.Length + 1).Replace('\\', '/');
                    if (current.Contains(key)) continue;
                    // ours = clothing/head/hands (a LoddedSkin inside) or empty (no asset in its container: an unused
                    // bundle name the AssetBundle Browser still built)
                    var bi = EFTModBuilderCore.ReadBundle(f, false, new[] { "LoddedSkin", "assets/" });
                    if (bi.Error != null) continue;
                    if (bi.Found.Contains("loddedskin") || !bi.Found.Contains("assets/")) stale.Add(f);
                }
                if (stale.Count > 0)
                {
                    string list = string.Join("\n", stale.Take(20).Select(f => "  " + f.Substring(root.Length + 1))) +
                                  (stale.Count > 20 ? $"\n  ... and {stale.Count - 20} more" : "");
                    if (EditorUtility.DisplayDialog("EFT Mod Builder - old bundles",
                            $"The build folder has {stale.Count} clothing/head/hands or empty bundle(s) whose bundle name is no longer used " +
                            $"in this project (renamed or deleted prefabs):\n\n{list}\n\nDelete them?", "Delete", "Keep"))
                    {
                        foreach (var f in stale) { DeleteBuildFile(f); DeleteBuildFile(f + ".manifest"); }
                        Log($"Clean: deleted {stale.Count} old bundle(s) from the build folder.");
                        changes++;
                    }
                    else Log($"Clean: kept {stale.Count} old bundle(s) in the build folder.");
                }
            }

            string rt = SptRuntime();
            var ourKeys = new HashSet<string>(ShippedBundles().Select(i => i.bundleKey).Where(k => !string.IsNullOrEmpty(k)));
            if (rt != null)
            {
                // 3. client cache
                string cache = Path.Combine(rt, "user", "cache", "bundles");
                if (Directory.Exists(cache))
                    foreach (var crcDir in Directory.GetDirectories(cache))
                        foreach (var key in ourKeys)
                        {
                            string f = Path.Combine(crcDir, key.Replace('/', Path.DirectorySeparatorChar));
                            if (!IsUnder(crcDir, f) || !File.Exists(f)) continue;
                            try { File.Delete(f); Log("Clean: deleted cached copy " + f); changes++; }
                            catch (Exception e) { Log("Clean: could not delete " + f + " (" + e.Message + ") - is the game running?"); }
                        }
            }

            // 4. files in our mod folder we did not create
            if (!string.IsNullOrEmpty(ModFolder) && Directory.Exists(Path.Combine(ModFolder, "bundles")))
            {
                var ours = new HashSet<string>(_p.generatedFiles.Select(Norm));
                string mroot = ModFolder.TrimEnd('\\', '/');
                string[] modFiles;
                try { modFiles = Directory.GetFiles(Path.Combine(mroot, "bundles"), "*", SearchOption.AllDirectories); }
                catch { modFiles = new string[0]; }
                foreach (var f in modFiles)
                {
                    string rel = f.Substring(mroot.Length + 1).Replace('\\', '/');
                    if (!ours.Contains(Norm(rel))) Log($"note: {rel} in the mod folder was not created by this builder (left alone; SPT only loads what bundles.json lists).");
                }
            }
            if (!auto) ScanOtherModsForCopies();
            if (!auto || changes > 0) Log(changes > 0 ? "Clean: done." : "Clean: nothing to clean.");
        }

        /// <summary>True when 'path' is 'root' itself or inside it (after resolving '..').</summary>
        static bool IsUnder(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            try
            {
                string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                string p = Path.GetFullPath(path);
                return (p + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void DeleteBuildFile(string abs)
        {
            if (!File.Exists(abs)) return;
            string proj = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/') + "/";
            string p = abs.Replace('\\', '/');
            if (p.StartsWith(proj + "Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string rel = p.Substring(proj.Length);
                if (AssetDatabase.DeleteAsset(rel)) return; // also removes the .meta
            }
            File.Delete(abs);
        }

        void ScanOtherModsForCopies()
        {
            if (string.IsNullOrEmpty(_modsFolder) || !Directory.Exists(_modsFolder)) return;
            var needles = _p.items.Where(i => !string.IsNullOrEmpty(i.prefabPath))
                                  .Select(i => "/" + Path.GetFileName(i.prefabPath).ToLowerInvariant()).Distinct().ToList();
            if (needles.Count == 0) return;
            string mine = string.IsNullOrEmpty(ModFolder) ? "" : Path.GetFullPath(ModFolder).TrimEnd('\\', '/');
            var files = Directory.GetDirectories(_modsFolder)
                .Where(d => !Path.GetFullPath(d).TrimEnd('\\', '/').Equals(mine, StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => { try { return Directory.GetFiles(Path.GetFullPath(d), "*.bundle", SearchOption.AllDirectories); } catch { return new string[0]; } })
                .ToList();
            int hits = 0;
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Looking for old copies in other mods", Path.GetFileName(files[i]), (float)i / files.Count))
                    { Log("Clean: search in other mods cancelled."); break; }
                    var bi = EFTModBuilderCore.ReadBundle(files[i], false, needles);
                    foreach (var n in bi.Found)
                    {
                        hits++;
                        string relToMods = files[i].Substring(Path.GetFullPath(_modsFolder).TrimEnd('\\', '/').Length).TrimStart('\\', '/');
                        int sep = relToMods.IndexOfAny(new[] { '\\', '/' });
                        string modName = sep > 0 ? relToMods.Substring(0, sep) : relToMods;
                        Log($"note: mod '{modName}' contains prefab '{n.TrimStart('/')}' in {files[i]} - if that is an old copy of this outfit, remove that mod (not done automatically).");
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (hits == 0) Log($"Clean: no other mod contains this project's prefabs ({files.Count} bundle(s) checked).");
        }

        static void Id(ref string id) { if (!EFTModBuilderCore.IsMongoId(id)) id = EFTModBuilderCore.NewMongoId(); }

        static List<string> Sides(ModItemState it, bool allowSavage)
        {
            var s = new List<string>();
            if (it.usec) s.Add("Usec");
            if (it.bear) s.Add("Bear");
            if (allowSavage && it.savage) s.Add("Savage");
            return s;
        }

        static void WriteText(string mod, string rel, string text, List<string> generated)
        {
            string f = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, text, new System.Text.UTF8Encoding(false));
            generated.Add(rel);
        }

        string TemplatePath()
        {
            var script = MonoScript.FromScriptableObject(this);
            string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(script)) ?? "Assets/Editor/EFTAutoPrefabber";
            string p = Path.Combine(dir, "EFTAutoModTemplate.bytes");
            if (!File.Exists(p)) throw new FileNotFoundException("Template DLL missing: " + p);
            return p;
        }

        // ------------------------------------------------------------------ GUI

        static GUIStyle _err, _warn;

        void OnGUI()
        {
            EFTToolsVersion.DrawHeader();
            if (_err == null)
            {
                _err = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = new Color(1f, 0.4f, 0.35f) } };
                _warn = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = new Color(1f, 0.78f, 0.25f) } };
            }

            EditorGUILayout.HelpBox("1) Set the folders and press Load.  2) Fill in names, prices and which hands each top uses.  " +
                                    "3) Build bundles + mod (builds only this mod's AssetBundles, then the mod).", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            FolderField("AssetBundle build folder", ref _buildFolder, true);
            FolderField("SPT mods folder", ref _modsFolder, false);
            _modFolderName = EditorGUILayout.DelayedTextField(new GUIContent("Mod folder name", "Folder created inside the SPT mods folder. Changing it switches to that mod's own bundle list."), _modFolderName);
            FolderField("EFT game folder (optional)", ref _gameFolder, false);
            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                // another mod folder = another bundle list
                if (!string.IsNullOrEmpty(ModFolder) && FolderKey(ModFolder) != _projectFor && (_p.items.Count > 0 || !string.IsNullOrEmpty(_projectFor)))
                    EditorApplication.delayCall += LoadProjectFromModFolder;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Load", "Reads modbuilder.json from the mod folder (if it exists) and scans the project's bundles"), GUILayout.Height(24)))
                    LoadProjectFromModFolder();
                if (GUILayout.Button("Rescan bundles", GUILayout.Height(24), GUILayout.Width(120))) ScanBundles();
                if (GUILayout.Button(new GUIContent("Deep scan game bundles", "Indexes every game bundle once so references to vanilla bundles other than shaders/cubemaps can be named"), GUILayout.Height(24), GUILayout.Width(170)))
                    DeepScanGame();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawMod();
            DrawDefaults();
            DrawItems();
            EditorGUILayout.EndScrollView();

            // validation touches the disk (bundle files, other mods' bundles.json), so don't run it on every repaint
            double now = EditorApplication.timeSinceStartup;
            if (GUI.changed || now - _lastValidate > 1.0) { Validate(true, true); _lastValidate = now; }
            foreach (var e in _errors) GUILayout.Label("ERROR: " + e, _err);
            foreach (var w in _notBuilt) GUILayout.Label("note: " + w, _warn);
            foreach (var w in _warnings) GUILayout.Label("note: " + w, _warn);

            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                _compression = (BundleCompression)EditorGUILayout.EnumPopup(new GUIContent("Bundle compression", "LZ4: loaded straight from disk (recommended). LZMA: smaller files but the game must decompress them into memory first."), _compression);
                _win64 = EditorGUILayout.Popup(_win64 ? 1 : 0, new[] { "StandaloneWindows", "StandaloneWindows64" }, GUILayout.Width(150)) == 1;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _forceRebuild = EditorGUILayout.ToggleLeft(new GUIContent("Force full rebuild", "Ignore Unity's build cache"), _forceRebuild, GUILayout.Width(150));
                _autoClean = EditorGUILayout.ToggleLeft(new GUIContent("Clean automatically on build", "See the Clean button's tooltip"), _autoClean);
            }
            if (EditorGUI.EndChangeCheck()) SavePrefs();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _p.items.Count > 0;
                if (GUILayout.Button(new GUIContent("Build bundles + mod", "Builds this mod's AssetBundles into the build folder, then the mod"), GUILayout.Height(30))) BuildBundlesAndMod();
                GUI.enabled = _errors.Count == 0 && _notBuilt.Count == 0;
                if (GUILayout.Button(new GUIContent("Build Mod only", "Uses the bundles already in the build folder"), GUILayout.Height(30), GUILayout.Width(120))) BuildMod();
                GUI.enabled = true;
                if (GUILayout.Button(new GUIContent("Clean", "Removes: unused AssetBundle names; old clothing or empty bundles in the build folder whose name is no longer used (asks first); " +
                        "cached copies of this mod's bundles in SPT_Runtime/user/cache/bundles. " +
                        "Reports (never deletes) other mods that contain a copy of this project's prefabs."), GUILayout.Height(30), GUILayout.Width(70))) Clean(false);
                GUI.enabled = !string.IsNullOrEmpty(ModFolder) && Directory.Exists(ModFolder);
                if (GUILayout.Button("Open mod folder", GUILayout.Height(30), GUILayout.Width(120))) EditorUtility.RevealInFinder(ModFolder);
                GUI.enabled = true;
            }

            if (_log.Length > 0)
            {
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(100));
                EditorGUILayout.SelectableLabel(_log, EditorStyles.textArea, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        void FolderField(string label, ref string value, bool projectRelative)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.DelayedTextField(label, value);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string start = string.IsNullOrEmpty(value) ? Directory.GetCurrentDirectory() : value;
                    string picked = EditorUtility.OpenFolderPanel(label, start, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        if (projectRelative)
                        {
                            string proj = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/') + "/";
                            string pk = picked.Replace('\\', '/');
                            value = pk.StartsWith(proj, StringComparison.OrdinalIgnoreCase) ? pk.Substring(proj.Length) : pk;
                        }
                        else value = picked;
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        void DrawMod()
        {
            _showMod = EditorGUILayout.Foldout(_showMod, "Mod", true);
            if (!_showMod) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _p.modName = EditorGUILayout.TextField("Name", _p.modName);
                _p.author = EditorGUILayout.TextField("Author", _p.author);
                _p.version = EditorGUILayout.TextField("Version", _p.version);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _p.autoGuid = EditorGUILayout.ToggleLeft("Auto GUID", _p.autoGuid, GUILayout.Width(110));
                    GUI.enabled = !_p.autoGuid;
                    string g = EditorGUILayout.TextField(_p.autoGuid ? Guid : _p.guid);
                    if (!_p.autoGuid) _p.guid = g;
                    GUI.enabled = true;
                }
                _p.license = EditorGUILayout.TextField("License", _p.license);
                _p.sptVersion = EditorGUILayout.TextField("SPT version", _p.sptVersion);
                _p.wttVersion = EditorGUILayout.TextField("WTT-CommonLib version", _p.wttVersion);
                _p.url = EditorGUILayout.TextField("URL (optional)", _p.url);
                _p.dependencyKeys = EditorGUILayout.TextField(new GUIContent("Dependency keys", "Added to every bundle in bundles.json; references to other game bundles found inside a bundle are added automatically"), _p.dependencyKeys);
            }
        }

        void DrawDefaults()
        {
            _showDefaults = EditorGUILayout.Foldout(_showDefaults, "Defaults for new clothing", true);
            if (!_showDefaults) return;
            using (new EditorGUI.IndentLevelScope())
            {
                _p.defTrader = Mathf.Max(0, EditorGUILayout.Popup("Trader", _p.defTrader, EFTModBuilderCore.TraderNames));
                _p.defLoyalty = EditorGUILayout.IntSlider("Loyalty level", _p.defLoyalty, 0, 4);
                _p.defProfileLevel = Mathf.Max(0, EditorGUILayout.IntField("Profile level", _p.defProfileLevel));
                _p.defStanding = EditorGUILayout.FloatField("Standing", _p.defStanding);
                _p.defCurrency = EditorGUILayout.Popup("Currency", _p.defCurrency, EFTModBuilderCore.CurrencyNames);
                _p.defPrice = Mathf.Max(1, EditorGUILayout.IntField("Price", _p.defPrice));
                if (GUILayout.Button("Apply to all tops and bottoms", GUILayout.Width(220)))
                    foreach (var it in _p.items.Where(i => i.kind == (int)ClothingKind.Top || i.kind == (int)ClothingKind.Bottom))
                    {
                        it.trader = _p.defTrader; it.loyalty = _p.defLoyalty; it.profileLevel = _p.defProfileLevel;
                        it.standing = _p.defStanding; it.currency = _p.defCurrency; it.price = _p.defPrice;
                    }
            }
        }

        void DrawItems()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Bundles", EditorStyles.boldLabel);
            if (_p.items.Count == 0) { EditorGUILayout.LabelField("Press Load to find your clothing, head and hands bundles.", EditorStyles.miniLabel); return; }

            var handsOptions = _p.items.Where(i => i.kind == (int)ClothingKind.Hands && !i.missing).Select(i => i.bundleKey).ToList();
            var gameHands = GameHands();

            var removed = _p.items.Where(i => i.missing).ToList();
            if (removed.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _showRemoved = EditorGUILayout.Foldout(_showRemoved, $"No longer in the project ({removed.Count}) - not built; ids kept in case they come back", true);
                    if (GUILayout.Button(new GUIContent("Forget all", "Removes them from this list and their ids from modbuilder.json on the next build"), GUILayout.Width(80)))
                    { _p.items.RemoveAll(i => i.missing); GUIUtility.ExitGUI(); }
                }
                if (_showRemoved)
                    foreach (var it in removed)
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(18);
                            GUILayout.Label(it.bundleKey + "  (" + KindNames[Mathf.Clamp(it.kind, 0, KindNames.Length - 1)] + ")", EditorStyles.miniLabel);
                            if (GUILayout.Button("Forget", GUILayout.Width(60))) { _p.items.Remove(it); GUIUtility.ExitGUI(); }
                        }
            }

            foreach (var it in _p.items.Where(i => !i.missing))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        it.include = EditorGUILayout.Toggle(it.include, GUILayout.Width(16));
                        it.foldout = EditorGUILayout.Foldout(it.foldout, it.bundleKey + (it.missing ? "   (not in project)" : ""), true);
                        it.kind = EditorGUILayout.Popup(it.kind, KindNames, GUILayout.Width(70));
                        if (!string.IsNullOrEmpty(it.prefabPath) && GUILayout.Button("Ping", GUILayout.Width(40)))
                            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(it.prefabPath));
                    }
                    if (!it.foldout || !it.include) continue;

                    var k = (ClothingKind)it.kind;
                    using (new EditorGUI.IndentLevelScope())
                    {
                        if (k == ClothingKind.Hands)
                        {
                            var users = _p.items.Where(t => t.include && t.kind == (int)ClothingKind.Top && t.handsKey == it.bundleKey).Select(t => t.name).ToList();
                            EditorGUILayout.LabelField(users.Count > 0 ? "Used by: " + string.Join(", ", users) : "Not used by any top yet.", EditorStyles.miniLabel);
                            continue;
                        }
                        if (k == ClothingKind.Unknown) { EditorGUILayout.LabelField("Pick what this bundle is (Top / Bottom / Head / Hands).", _warn); continue; }

                        it.name = EditorGUILayout.TextField(k == ClothingKind.Head ? "Head name" : "Name", it.name);
                        if (k == ClothingKind.Head)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.PrefixLabel("Sides");
                                it.usec = EditorGUILayout.ToggleLeft("USEC", it.usec, GUILayout.Width(60));
                                it.bear = EditorGUILayout.ToggleLeft("BEAR", it.bear, GUILayout.Width(60));
                            }
                            it.addHeadToPlayer = EditorGUILayout.Toggle(new GUIContent("Add head to player", "WTT addHeadToPlayer: makes it selectable for the player"), it.addHeadToPlayer);
                            continue;
                        }

                        it.description = EditorGUILayout.TextField("Description", it.description);
                        if (k == ClothingKind.Top)
                        {
                            // this project's hands bundles, then the game's own hands (customization.json names -> bundle path)
                            var keys = handsOptions.ToList();
                            var labels = handsOptions.ToList();
                            foreach (var gh in gameHands) { keys.Add(gh.Value); labels.Add("Game hands/" + gh.Key); }
                            // 1.7.4: the automatic default is listed even when the game's list can't be read (no SPT folder set)
                            if (!it.customHands && !string.IsNullOrEmpty(it.handsKey) && !keys.Contains(it.handsKey))
                            { keys.Add(it.handsKey); labels.Add((it.defaultHands ? "Default: " : "Current: ") + it.handsKey.Replace('/', '\\')); }
                            keys.Add(null); labels.Add("Other key...");
                            int sel = it.customHands ? keys.Count - 1 : keys.IndexOf(it.handsKey);
                            int ns = EditorGUILayout.Popup(new GUIContent("Hands (arms)", "First-person arms shown with this top"), sel, labels.ToArray());
                            if (ns != sel && ns >= 0)
                            {
                                it.customHands = ns == keys.Count - 1;
                                if (!it.customHands) it.handsKey = keys[ns];
                                it.defaultHands = false;
                            }
                            if (it.defaultHands)
                                EditorGUILayout.LabelField(" ", "Picked automatically (this top has no hands of its own) - change it above if you like.", EditorStyles.miniLabel);
                            if (it.customHands)
                                it.handsKey = EditorGUILayout.TextField(new GUIContent("Hands bundle key", "Key of a hands bundle from the game or another mod"), it.handsKey);
                        }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PrefixLabel("Sides");
                            it.customSide = !EditorGUILayout.ToggleLeft("WTT default", !it.customSide, GUILayout.Width(100));
                            if (it.customSide)
                            {
                                it.usec = EditorGUILayout.ToggleLeft("USEC", it.usec, GUILayout.Width(60));
                                it.bear = EditorGUILayout.ToggleLeft("BEAR", it.bear, GUILayout.Width(60));
                                it.savage = EditorGUILayout.ToggleLeft("Scav", it.savage, GUILayout.Width(60));
                            }
                        }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var traders = EFTModBuilderCore.TraderNames.Concat(new[] { "Custom id..." }).ToArray();
                            int ti = it.trader < 0 ? traders.Length - 1 : it.trader;
                            ti = EditorGUILayout.Popup("Trader", ti, traders);
                            it.trader = ti == traders.Length - 1 ? -1 : ti;
                            if (it.trader < 0) it.customTraderId = EditorGUILayout.TextField(it.customTraderId);
                        }
                        it.loyalty = EditorGUILayout.IntSlider("Loyalty level", it.loyalty, 0, 4);
                        it.profileLevel = Mathf.Max(0, EditorGUILayout.IntField("Profile level", it.profileLevel));
                        it.standing = EditorGUILayout.FloatField("Standing", it.standing);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            it.currency = EditorGUILayout.Popup("Price", it.currency, EFTModBuilderCore.CurrencyNames);
                            it.price = EditorGUILayout.IntField(it.price, GUILayout.Width(90));
                        }
                    }
                }
            }
        }
    }
}
