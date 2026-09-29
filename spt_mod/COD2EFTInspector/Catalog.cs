// Read-only catalog of every top / pants / head / hands the SPT server knows, and where each comes from:
//   vanilla  <server>\SPT_Data\database\templates\customization.json   (_props.BodyPart + _props.Prefab.path)
//   mods     <server>\user\mods\<mod>\db\CustomClothing\*.json (WTT: topId/bottomId/handsId + *BundlePath)
//            <server>\user\mods\<mod>\db\CustomHeads\*.json    (WTT: { headId: { path, locales } })
// Also checks that each bundle file exists. Groundwork for the outfit browser (stage 2); applying outfits is not done here.
// No Unity types: unit-tested with mono (tests/CatalogTest.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace COD2EFTInspector
{
    internal sealed class Outfit
    {
        public string Id, Name, Part, Bundle, Source, File;   // Part: Top / Pants / Head / Hands; Source: "vanilla" or the mod folder
        public bool? BundleFound;                              // null = not checked (folder unknown)
        public override string ToString() =>
            $"[{Source}] {Part} '{Name}' id {Id} bundle {Bundle}" + (BundleFound == false ? "  (BUNDLE FILE MISSING)" : "");
    }

    internal sealed class Catalog
    {
        public string ServerDir, GameDir;
        public List<Outfit> Items = new List<Outfit>();
        public List<string> Notes = new List<string>();

        static readonly Dictionary<string, string> VanillaPart = new Dictionary<string, string>
            { { "Body", "Top" }, { "Feet", "Pants" }, { "Head", "Head" }, { "Hands", "Hands" } };

        public static string PartOfBodyKey(string bodySkinsKey) =>
            bodySkinsKey != null && VanillaPart.TryGetValue(bodySkinsKey, out var p) ? p : bodySkinsKey;

        static string VanillaCustomization(string dir)
        {
            foreach (var rel in new[] { @"SPT_Data\database\templates\customization.json", @"SPT_Data\Server\database\templates\customization.json" })
            {
                var f = Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar));
                if (File.Exists(f)) return f;
            }
            return null;
        }

        static bool IsServerDir(string d) =>
            !string.IsNullOrEmpty(d) && Directory.Exists(d) &&
            (Directory.Exists(Path.Combine(d, "user", "mods")) || VanillaCustomization(d) != null);

        /// <summary>The SPT server folder: the configured one, else next to / inside the game folder (2 levels deep).</summary>
        public static string FindServerDir(string configured, string gameDir, List<string> notes)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsServerDir(configured)) return configured;
                notes.Add($"Configured server folder has no user\\mods or SPT_Data: {configured}");
            }
            var cands = new List<string> { gameDir };
            try
            {
                foreach (var d1 in Directory.GetDirectories(gameDir))
                {
                    cands.Add(d1);
                    try { cands.AddRange(Directory.GetDirectories(d1)); } catch { }
                }
                var parent = Directory.GetParent(gameDir)?.FullName;
                if (parent != null) { cands.Add(parent); cands.AddRange(Directory.GetDirectories(parent)); }
            }
            catch (Exception e) { notes.Add("Searching for the server folder failed: " + e.Message); }
            var hit = cands.FirstOrDefault(IsServerDir);
            if (hit == null) notes.Add($"SPT server folder not found next to {gameDir}; set it in the config (4. Outfits / Server folder).");
            return hit;
        }

        public static Catalog Load(string configuredServer, string gameDir)
        {
            var c = new Catalog { GameDir = gameDir };
            c.ServerDir = FindServerDir(configuredServer, gameDir, c.Notes);
            if (c.ServerDir == null) return c;
            c.Notes.Add("Server folder: " + c.ServerDir);
            c.LoadVanilla();
            c.LoadMods();
            c.CheckBundles();
            return c;
        }

        void LoadVanilla()
        {
            var f = VanillaCustomization(ServerDir);
            if (f == null) { Notes.Add("customization.json not found under SPT_Data"); return; }
            try
            {
                var root = Json.Parse(File.ReadAllText(f));
                var rd = root as Dictionary<string, object>;
                IEnumerable<object> entries = rd != null ? (IEnumerable<object>)rd.Values : root as List<object> ?? new List<object>();
                int n = 0;
                foreach (var e in entries)
                {
                    string bp = Json.Str(e, "_props", "BodyPart"), path = Json.Str(e, "_props", "Prefab", "path");
                    if (bp == null || path == null || !VanillaPart.ContainsKey(bp) || path.Length == 0) continue;
                    Items.Add(new Outfit { Id = Json.Str(e, "_id"), Name = Json.Str(e, "_name"), Part = VanillaPart[bp], Bundle = path, Source = "vanilla", File = f });
                    n++;
                }
                Notes.Add($"vanilla: {n} entries from {f}");
            }
            catch (Exception ex) { Notes.Add($"Reading {f} failed: {ex.Message}"); }
        }

        static string LocaleName(object entry)
        {
            var en = Json.At(entry, "locales", "en");
            return en as string ?? Json.Str(en, "name");
        }

        void LoadMods()
        {
            var mods = Path.Combine(ServerDir, "user", "mods");
            if (!Directory.Exists(mods)) { Notes.Add("No user\\mods folder"); return; }
            foreach (var mod in Directory.GetDirectories(mods).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(mod);
                int before = Items.Count, bad = 0;
                foreach (var f in JsonFiles(Path.Combine(mod, "db", "CustomClothing")))
                {
                    try
                    {
                        var root = Json.Parse(File.ReadAllText(f));
                        var rd = root as Dictionary<string, object>;
                        IEnumerable<object> entries = root as List<object> ?? (rd != null ? (IEnumerable<object>)rd.Values : Enumerable.Empty<object>());
                        foreach (var e in entries)
                        {
                            string n = LocaleName(e) ?? Json.Str(e, "suiteId") ?? "?";
                            string top = Json.Str(e, "topId"), bottom = Json.Str(e, "bottomId"), hands = Json.Str(e, "handsId"), hb = Json.Str(e, "handsBundlePath");
                            if (top != null)
                                Items.Add(new Outfit { Id = top, Name = n, Part = "Top", Bundle = Json.Str(e, "topBundlePath"), Source = name, File = f });
                            if (bottom != null)
                                Items.Add(new Outfit { Id = bottom, Name = n, Part = "Pants", Bundle = Json.Str(e, "bottomBundlePath"), Source = name, File = f });
                            if (hands != null && !string.IsNullOrEmpty(hb))
                                Items.Add(new Outfit { Id = hands, Name = n + " (hands)", Part = "Hands", Bundle = hb, Source = name, File = f });
                        }
                    }
                    catch (Exception ex) { bad++; Notes.Add($"{name}: {Path.GetFileName(f)} not readable: {ex.Message}"); }
                }
                foreach (var f in JsonFiles(Path.Combine(mod, "db", "CustomHeads")))
                {
                    try
                    {
                        var heads = Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
                        if (heads != null)
                            foreach (var kv in heads)
                                Items.Add(new Outfit { Id = kv.Key, Name = LocaleName(kv.Value) ?? kv.Key, Part = "Head", Bundle = Json.Str(kv.Value, "path"), Source = name, File = f });
                    }
                    catch (Exception ex) { bad++; Notes.Add($"{name}: {Path.GetFileName(f)} not readable: {ex.Message}"); }
                }
                if (Items.Count > before || bad > 0) Notes.Add($"mod {name}: {Items.Count - before} entries" + (bad > 0 ? $", {bad} file(s) unreadable" : ""));
            }
        }

        static IEnumerable<string> JsonFiles(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>();

        void CheckBundles()
        {
            string streaming = GameDir == null ? null : Path.Combine(GameDir, "EscapeFromTarkov_Data", "StreamingAssets", "Windows");
            foreach (var o in Items)
            {
                if (string.IsNullOrEmpty(o.Bundle)) { o.BundleFound = false; continue; }
                var rel = o.Bundle.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var places = new List<string>();
                if (o.Source == "vanilla") { if (streaming != null) places.Add(Path.Combine(streaming, rel)); }
                else
                {
                    var mod = Path.Combine(ServerDir, "user", "mods", o.Source);
                    places.Add(Path.Combine(mod, "bundles", rel));
                    places.Add(Path.Combine(mod, rel));
                }
                o.BundleFound = places.Count == 0 ? (bool?)null : places.Any(File.Exists);
            }
            int missing = Items.Count(o => o.BundleFound == false);
            if (missing > 0) Notes.Add($"{missing} entr{(missing == 1 ? "y has" : "ies have")} no bundle file where expected");
        }

        public Outfit ById(string id) => id == null ? null : Items.FirstOrDefault(o => o.Id == id);

        /// <summary>Entries whose bundle file name (without extension) equals a skin object name, e.g. "suit_top_x".</summary>
        public List<Outfit> ByBundleStem(string stem) =>
            string.IsNullOrEmpty(stem) ? new List<Outfit>() :
            Items.Where(o => o.Bundle != null && string.Equals(Path.GetFileNameWithoutExtension(o.Bundle.Replace('\\', '/').Split('/').Last()), stem, StringComparison.OrdinalIgnoreCase)).ToList();

        public IEnumerable<string> Sources => Items.Select(o => o.Source).Distinct().OrderBy(s => s == "vanilla" ? "" : s, StringComparer.OrdinalIgnoreCase);

        public string Report()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var n in Notes) sb.AppendLine(n);
            sb.AppendLine();
            foreach (var g in Items.GroupBy(o => o.Source).OrderBy(g => g.Key == "vanilla" ? "" : g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"=== {g.Key}: " + string.Join(", ", g.GroupBy(o => o.Part).Select(p => $"{p.Count()} {p.Key}")));
                foreach (var o in g.OrderBy(o => o.Part).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"  {o.Part,-6} {o.Name}  id {o.Id}  bundle {o.Bundle}" + (o.BundleFound == false ? "  <-- BUNDLE FILE MISSING" : ""));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
