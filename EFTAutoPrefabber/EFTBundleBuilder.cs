// EFT Bundle Builder - builds just the chosen AssetBundles (plus the bundles they depend on) through the SDK's
// imposter build (com.bmpq.assetbundlebrowser-imposter), exactly like the AssetBundle Browser's Build button, but:
//   * only the bundles you pass in are written (the imposter "shaders"/"cubemaps" bundles are still built so the
//     references point at the game's own bundles, and are then deleted by the imposter builder as usual),
//   * LZ4 (chunk-based) compression by default: the game loads LZ4 bundles directly from disk, LZMA ones must be
//     decompressed into memory first.
// Used by the EFT Mod Builder ("Build bundles + mod") and the Auto Prefabber's one-click pipeline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetBundleBrowser.AssetBundleDataSource;
using AssetBundleBrowser.Imposter;
using UnityEditor;

namespace EFTAutoPrefab
{
    public enum BundleCompression { LZ4, Uncompressed, LZMA }

    public static class EFTBundleBuilder
    {
        /// <summary>"name" + "." + variant, as AssetDatabase.GetAllAssetBundleNames() lists it.</summary>
        static string FullName(string name, string variant) => string.IsNullOrEmpty(variant) ? name : name + "." + variant;

        /// <summary>The given bundles plus every bundle that holds an asset they use (recursively).</summary>
        public static List<string> WithDependencies(IEnumerable<string> bundles)
        {
            var known = new HashSet<string>(AssetDatabase.GetAllAssetBundleNames());
            var result = new List<string>();
            var queue = new Queue<string>(bundles.Where(known.Contains));
            var seen = new HashSet<string>();
            while (queue.Count > 0)
            {
                string b = queue.Dequeue();
                if (!seen.Add(b)) continue;
                result.Add(b);
                var assets = AssetDatabase.GetAssetPathsFromAssetBundle(b);
                foreach (var dep in AssetDatabase.GetDependencies(assets, true))
                {
                    string n = AssetDatabase.GetImplicitAssetBundleName(dep);
                    if (string.IsNullOrEmpty(n)) continue;
                    string full = FullName(n, AssetDatabase.GetImplicitAssetBundleVariantName(dep));
                    if (full != b && known.Contains(full) && !seen.Contains(full)) queue.Enqueue(full);
                }
            }
            return result;
        }

        /// <summary>
        /// Builds 'bundles' (and what they depend on) into outputDir. Returns false on failure.
        /// 'built' gets the bundle names that were written (imposter bundles excluded, they are deleted after the build).
        /// </summary>
        public static bool Build(IEnumerable<string> bundles, string outputDir, BundleCompression compression, BuildTarget target,
                                 bool forceRebuild, List<string> log, out List<string> built)
        {
            built = new List<string>();
            var wanted = bundles.Distinct().ToList();
            var known = new HashSet<string>(AssetDatabase.GetAllAssetBundleNames());
            foreach (var w in wanted.Where(w => !known.Contains(w)))
                log.Add("Bundle '" + w + "' has no assets assigned in this project; skipped.");
            var set = WithDependencies(wanted);
            if (set.Count == 0) { log.Add("No bundles to build."); return false; }
            foreach (var extra in set.Except(wanted)) log.Add("Also building dependency bundle '" + extra + "'.");

            Directory.CreateDirectory(outputDir);
            var opts = BuildAssetBundleOptions.None;
            if (compression == BundleCompression.LZ4) opts |= BuildAssetBundleOptions.ChunkBasedCompression;
            else if (compression == BundleCompression.Uncompressed) opts |= BuildAssetBundleOptions.UncompressedAssetBundle;
            if (forceRebuild) opts |= BuildAssetBundleOptions.ForceRebuildAssetBundle;

            var done = new List<string>();
            var info = new ABBuildInfo
            {
                outputDirectory = outputDir,
                options = opts,
                buildTarget = target,
                onBuild = name => done.Add(name),
            };
            bool ok;
            try
            {
                AssetDatabase.SaveAssets();
                ok = ImposterBuilder.BuildAssetBundles(info, new SubsetDataSource(set));
            }
            catch (Exception e)
            {
                log.Add("AssetBundle build threw: " + e.Message);
                UnityEngine.Debug.LogException(e);
                return false;
            }
            if (!ok) { log.Add("AssetBundle build failed - see the Console for Unity's errors."); return false; }

            foreach (var n in done)
            {
                if (ImposterBuilder.IsImposterBundle(n)) continue;
                string f = Path.Combine(outputDir, n.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(f)) built.Add(n);
                else log.Add("Expected output missing: " + f);
            }
            log.Add($"Built {built.Count} bundle(s) ({compression}, {target}) into {outputDir}.");
            return true;
        }

        /// <summary>The project's bundle assignments, limited to a subset of bundle names.</summary>
        sealed class SubsetDataSource : ABDataSource
        {
            readonly HashSet<string> _names;
            public SubsetDataSource(IEnumerable<string> names) { _names = new HashSet<string>(names); }

            public string Name => "EFT subset";
            public string ProviderName => "EFTAutoPrefab";
            public string[] GetAssetPathsFromAssetBundle(string assetBundleName) => AssetDatabase.GetAssetPathsFromAssetBundle(assetBundleName);
            public string GetAssetBundleName(string assetPath)
            {
                var imp = AssetImporter.GetAtPath(assetPath);
                if (imp == null) return string.Empty;
                return FullName(imp.assetBundleName, imp.assetBundleVariant);
            }
            public string GetImplicitAssetBundleName(string assetPath) => AssetDatabase.GetImplicitAssetBundleName(assetPath);
            public string[] GetAllAssetBundleNames() => AssetDatabase.GetAllAssetBundleNames().Where(_names.Contains).ToArray();
            public bool IsReadOnly() => true;
            public void SetAssetBundleNameAndVariant(string assetPath, string bundleName, string variantName) { }
            public void RemoveUnusedAssetBundleNames() { }
            public bool CanSpecifyBuildTarget => true;
            public bool CanSpecifyBuildOutputDirectory => true;
            public bool CanSpecifyBuildOptions => true;
            public bool BuildAssetBundles(ABBuildInfo info) => ImposterBuilder.BuildAssetBundles(info, this);
        }
    }
}
