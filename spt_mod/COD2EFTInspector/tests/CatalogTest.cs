// Unit test for Json.cs + Catalog.cs (no Unity). Mod files are written by the real Mod Builder code
// (unity/EFTAutoPrefabber/EFTModBuilderCore.cs), so this also checks the Mod Builder -> Inspector contract.
// Run (Linux, mono): see tests/run_tests.sh. Not part of the plugin build (the .csproj excludes tests/).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using COD2EFTInspector;
using EFTAutoPrefab;

static class CatalogTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }

    static int Main()
    {
        // JSON reader edge cases
        var j = Json.Parse("{\"a\": [1, 2.5e1, -3], \"b\": {\"c\": \"x\\\"y\\u00e9\"}, \"t\": true, \"n\": null, \"trail\": [1,],}");
        Check(Json.Str(j, "b", "c") == "x\"yé", "json string escapes");
        Check(((List<object>)Json.At(j, "a")).Count == 3 && (double)((List<object>)Json.At(j, "a"))[1] == 25, "json numbers");
        Check((bool)Json.At(j, "t") && Json.At(j, "n") == null && Json.At(j, "missing", "x") == null, "json literals / missing path");
        try { Json.Parse("{\"a\": }"); Check(false, "json rejects bad input"); } catch (FormatException) { Check(true, "json rejects bad input"); }

        string root = Path.Combine(Path.GetTempPath(), "cod2eft_catalog_test_" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "SPT4.1 Game"), server = Path.Combine(game, "SPT");
        string tpl = Path.Combine(server, "SPT_Data", "database", "templates");
        Directory.CreateDirectory(tpl);
        // vanilla: shape of SPT's customization.json (id -> template); a suite (no BodyPart) and a voice must be skipped
        File.WriteAllText(Path.Combine(tpl, "customization.json"), @"{
  ""5cde95d97d6c8b647a3769b0"": { ""_id"": ""5cde95d97d6c8b647a3769b0"", ""_name"": ""usec_top_default"", ""_parent"": ""5cc0868e14c02e000c6bea68"", ""_type"": ""Item"",
    ""_props"": { ""Name"": ""usec"", ""Side"": [""Usec""], ""BodyPart"": ""Body"", ""Prefab"": { ""path"": ""assets/content/characters/character/prefabs/usec_top_default.bundle"", ""rcid"": """" }, ""WatchPrefab"": { ""path"": """", ""rcid"": """" }, ""IntegratedArmorVest"": false } },
  ""5cc085bb14c02e000e67a5c5"": { ""_id"": ""5cc085bb14c02e000e67a5c5"", ""_name"": ""usec_hands"", ""_type"": ""Item"",
    ""_props"": { ""BodyPart"": ""Hands"", ""Prefab"": { ""path"": ""assets/content/hands/usec/usec_hands.bundle"", ""rcid"": """" } } },
  ""suite1"": { ""_id"": ""suite1"", ""_name"": ""DefaultUsecUpper"", ""_type"": ""Item"", ""_props"": { ""Body"": ""5cde95d97d6c8b647a3769b0"", ""Hands"": ""5cc085bb14c02e000e67a5c5"" } },
  ""voice1"": { ""_id"": ""voice1"", ""_name"": ""Usec_1"", ""_type"": ""Item"", ""_props"": { ""Prefab"": ""voice_usec_1"", ""Side"": [""Usec""] } }
}");
        // the vanilla top's bundle exists, the hands' doesn't
        var vb = Path.Combine(game, "EscapeFromTarkov_Data", "StreamingAssets", "Windows", "assets", "content", "characters", "character", "prefabs");
        Directory.CreateDirectory(vb);
        File.WriteAllText(Path.Combine(vb, "usec_top_default.bundle"), "x");

        // a mod written by the Mod Builder's own serializers
        string mod = Path.Combine(server, "user", "mods", "COD2EFT-Kleo");
        var clothes = new List<EFTModBuilderCore.ClothingEntry> {
            new EFTModBuilderCore.ClothingEntry { Kind = ClothingKind.Top, SuiteId = "s1", OutfitId = "o1", TopId = "top1", HandsId = "hands1",
                Name = "Kleo \"MW2\" top", Description = "d", BundlePath = "cod2eft/kleo_top.bundle", HandsBundlePath = "cod2eft/kleo_hands.bundle", Trader = "t", CurrencyTpl = "c" },
            new EFTModBuilderCore.ClothingEntry { Kind = ClothingKind.Bottom, SuiteId = "s2", OutfitId = "o2", BottomId = "bot1",
                Name = "Kleo pants", Description = "d", BundlePath = "cod2eft/kleo_lower.bundle", Trader = "t", CurrencyTpl = "c" },
        };
        var heads = new List<EFTModBuilderCore.HeadEntry> { new EFTModBuilderCore.HeadEntry { HeadId = "head1", Name = "Kleo head", BundlePath = "cod2eft/kleo_head.bundle", Side = new List<string> { "Usec" } } };
        Directory.CreateDirectory(Path.Combine(mod, "db", "CustomClothing"));
        Directory.CreateDirectory(Path.Combine(mod, "db", "CustomHeads"));
        Directory.CreateDirectory(Path.Combine(mod, "bundles", "cod2eft"));
        File.WriteAllText(Path.Combine(mod, "db", "CustomClothing", "Clothes.json"), EFTModBuilderCore.ClothingJson(clothes));
        File.WriteAllText(Path.Combine(mod, "db", "CustomHeads", "Heads.json"), EFTModBuilderCore.HeadsJson(heads));
        foreach (var b in new[] { "kleo_top", "kleo_hands", "kleo_head" }) File.WriteAllText(Path.Combine(mod, "bundles", "cod2eft", b + ".bundle"), "x");
        // a broken mod file must not stop the rest
        string bad = Path.Combine(server, "user", "mods", "Broken", "db", "CustomClothing");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "x.json"), "{ nope");

        // a mod that keeps its bundles in another sub-folder (found by file name)
        string other = Path.Combine(server, "user", "mods", "OtherLoader");
        Directory.CreateDirectory(Path.Combine(other, "db", "CustomClothing"));
        Directory.CreateDirectory(Path.Combine(other, "assets", "x"));
        File.WriteAllText(Path.Combine(other, "db", "CustomClothing", "c.json"), "[{\"topId\": \"t9\", \"topBundlePath\": \"gorka_top.bundle\", \"locales\": {\"en\": {\"name\": \"Gorka\"}}}]");
        File.WriteAllText(Path.Combine(other, "assets", "x", "gorka_top.bundle"), "x");
        var c = Catalog.Load("", game);
        foreach (var n in c.Notes) Console.WriteLine("     note: " + n);
        Check(c.ServerDir == server, "server folder found inside the game folder");
        var v = c.Items.Where(o => o.Source == "vanilla").ToList();
        Check(v.Count == 2, "vanilla: 2 wearable entries (suite and voice skipped), got " + v.Count);
        Check(c.ById("5cde95d97d6c8b647a3769b0")?.Part == "Top" && c.ById("5cde95d97d6c8b647a3769b0")?.BundleFound == true, "vanilla top: part + bundle found");
        Check(c.ById("5cc085bb14c02e000e67a5c5")?.BundleFound == false, "vanilla hands: missing bundle flagged");
        var k = c.Items.Where(o => o.Source == "COD2EFT-Kleo").ToList();
        Check(k.Count == 4, "mod: top + hands + pants + head, got " + k.Count);
        Check(c.ById("top1")?.Name == "Kleo \"MW2\" top" && c.ById("top1")?.Bundle == "cod2eft/kleo_top.bundle", "mod top: name + bundle");
        Check(c.ById("hands1")?.Part == "Hands" && c.ById("bot1")?.Part == "Pants" && c.ById("head1")?.Part == "Head", "mod parts");
        Check(c.ById("bot1")?.BundleFound == false && c.ById("head1")?.BundleFound == true, "mod bundle check (pants missing, head present)");
        Check(c.ById("t9")?.BundleFound == true && c.ById("t9").BundleElsewhere, "bundle found elsewhere in the mod folder");
        Check(c.ByBundleStem("kleo_top").Count == 1 && c.ByBundleStem("USEC_TOP_DEFAULT").Count == 1, "match by bundle file name");
        Check(c.Notes.Any(n => n.StartsWith("Broken:")), "broken mod file reported, others still read");
        Check(Catalog.Load(Path.Combine(root, "nope"), game).Notes.Any(n => n.Contains("Configured server folder")), "bad configured folder reported, falls back to search");
        Console.WriteLine(c.Report().Split('\n').Length > 5 ? "ok   report text" : "FAIL report text");
        Directory.Delete(root, true);
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
