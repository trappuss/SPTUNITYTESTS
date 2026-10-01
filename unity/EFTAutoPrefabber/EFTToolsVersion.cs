// EFT Tools version - one number for the whole EFTAutoPrefabber folder (Auto Prefabber, Mod Builder, material fixer,
// bundle builder). Every update to any file in this folder bumps it; CHANGELOG.md next to this file lists what changed.
//
// Shown at the top of both windows. If the number written in this file on disk differs from the one Unity has compiled,
// the windows say so and offer a Refresh (Unity only recompiles when its window gets focus / on Refresh).

using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace EFTAutoPrefab
{
    [InitializeOnLoad]
    public static class EFTToolsVersion
    {
        public const string Version = "1.7.7";
        public const string Date = "2026-10-01";

        static EFTToolsVersion()
        {
            Debug.Log($"[EFT Tools] v{Version} ({Date}) loaded");
        }

        static string _srcPath, _srcVersion;
        static long _srcTicks;

        /// <summary>The Version written in this script's file on disk (null if it can't be read).</summary>
        public static string SourceVersion()
        {
            if (_srcPath == null)
            {
                foreach (var guid in AssetDatabase.FindAssets("EFTToolsVersion t:MonoScript"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(p) == "EFTToolsVersion.cs") { _srcPath = p; break; }
                }
                if (_srcPath == null) return null;
            }
            try
            {
                long t = File.GetLastWriteTimeUtc(_srcPath).Ticks;
                if (t != _srcTicks)
                {
                    _srcTicks = t;
                    var m = Regex.Match(File.ReadAllText(_srcPath), "Version\\s*=\\s*\"([^\"]+)\"");
                    _srcVersion = m.Success ? m.Groups[1].Value : null;
                }
            }
            catch { _srcVersion = null; }
            return _srcVersion;
        }

        /// <summary>Version line for the top of a window, with a warning + Refresh when Unity runs older code than is on disk.</summary>
        public static void DrawHeader()
        {
            string disk = SourceVersion();
            bool stale = disk != null && disk != Version;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"EFT Tools v{Version}  ({Date})", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (!stale && GUILayout.Button(new GUIContent("Changelog", "Opens CHANGELOG.md next to the scripts"), EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    string cl = _srcPath != null ? Path.Combine(Path.GetDirectoryName(_srcPath) ?? "", "CHANGELOG.md") : null;
                    if (cl != null && File.Exists(cl)) EditorUtility.OpenWithDefaultApp(cl);
                }
            }
            if (stale)
            {
                EditorGUILayout.HelpBox($"Unity is still running v{Version}, but v{disk} is on disk - it hasn't recompiled yet.", MessageType.Warning);
                if (GUILayout.Button("Refresh and recompile now", GUILayout.Height(22))) AssetDatabase.Refresh();
            }
        }
    }
}
