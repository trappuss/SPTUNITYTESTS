// Finds the characters on screen and sorts their renderers into groups:
//   body parts = PlayerBody.BodySkins (Head / Top / Pants / Hands; alternative armor/vest/face-cover meshes included,
//                 inactive ones too), everything else under the player = gear (grouped by item) or "other".
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace COD2EFTInspector
{
    internal sealed class Entry
    {
        public Renderer R;
        public string Path;     // relative to the group's root, for reports
    }

    internal sealed class Group
    {
        public string Name;      // e.g. "Top: suit_top_x"
        public string Kind;      // "body", "gear", "other"
        public string Part;      // BodySkins key (Head/Body/Feet/Hands) for body groups
        public string Source;    // skin / item object name, "(Clone)" stripped
        public List<Entry> Entries = new List<Entry>();
        public bool Open;
    }

    internal sealed class Target
    {
        public Component Body;   // EFT.PlayerBody (null if the type wasn't found)
        public Component Player; // EFT.Player (null for the menu preview)
        public Transform Root;
        public string Label;
        public bool Local;
    }

    internal sealed class Scan
    {
        public Target Target;
        public List<Group> Groups = new List<Group>();
        public string Signature = "";
        public IEnumerable<Group> Body => Groups.Where(g => g.Kind == "body");
    }

    internal static class BodyScan
    {
        public static string Clean(string n) => (n ?? "").Replace("(Clone)", "").Trim();

        static string PathOf(Transform t, int depth)
        {
            var parts = new List<string>();
            for (; t != null && parts.Count < depth; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        public static string PathOf(Transform t) => PathOf(t, 64);

        /// <summary>"Body=name,Feet=name,..." of a PlayerBody's BodySkins (what it shows now).</summary>
        public static string Skins(object body)
        {
            var skins = Game.Get(body, "BodySkins") as IDictionary;
            if (skins == null) return "";
            var names = new List<string>();
            foreach (DictionaryEntry de in skins) { var c = de.Value as Component; names.Add(de.Key + "=" + (c != null ? Clean(c.gameObject.name) : "-")); }
            return string.Join(",", names);
        }

        /// <summary>Hideout vs menu diagnosis: every PlayerBody in the loaded scenes (also inactive ones), who owns it, what it shows.</summary>
        public static void LogBodies(Component tryOnTarget)
        {
            var bodyT = Game.FindType("EFT.PlayerBody");
            if (bodyT == null) return;
            var playerT = Game.FindType("EFT.Player");
            var viewT = Game.FindType("EFT.UI.PlayerModelView");
            var main = Game.MainPlayer();
            var lines = new List<string>();
            foreach (var o in Resources.FindObjectsOfTypeAll(bodyT))
            {
                var b = o as Component;
                if (b == null || !b.gameObject.scene.IsValid()) continue;   // prefabs / assets
                var pl = playerT != null ? b.GetComponentInParent(playerT) : null;
                Component view = null;
                if (viewT != null) for (var t = b.transform; t != null && view == null; t = t.parent) view = t.GetComponent(viewT);
                string owner = pl != null ? (pl == main ? "YOUR PLAYER " : "player ") + pl.GetType().Name + " '" + pl.name + "'"
                             : view != null ? "menu view '" + PathOf(view.transform, 4) + "'" : "no owner";
                lines.Add($"  #{b.GetInstanceID()} {(b.gameObject.activeInHierarchy ? "active" : "INACTIVE")} {owner}{(b == tryOnTarget ? "  <- try-on target" : "")}" +
                          $"\n      at {PathOf(b.transform)} (scene {b.gameObject.scene.name})\n      shows {Skins(b)}");
            }
            InspectorPlugin.Log.LogInfo($"Bodies: {lines.Count} PlayerBody component(s) (main player: {(main != null ? main.GetType().Name + " at " + (Game.Location(main) ?? "?") : "none")}):\n" + string.Join("\n", lines));
        }

        public static string RelPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        public static string PartLabel(string key)
        {
            switch (key)
            {
                case "Body": return "Top";
                case "Feet": return "Pants";
                case "Head": return "Head";
                case "Hands": return "Hands (first-person)";
                default: return key;
            }
        }

        static int PartOrder(string key) => key == "Head" ? 0 : key == "Body" ? 1 : key == "Feet" ? 2 : key == "Hands" ? 3 : 4;

        public static List<Target> FindTargets(bool includeOthers)
        {
            var list = new List<Target>();
            var main = Game.MainPlayer();
            var bodyT = Game.FindType("EFT.PlayerBody");
            var playerT = Game.FindType("EFT.Player");
            if (bodyT != null)
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(bodyT))
                {
                    var body = o as Component;
                    if (body == null) continue;
                    Component player = null;
                    if (playerT != null) player = body.GetComponentInParent(playerT);
                    bool local = player != null && main != null && player == main;
                    if (player != null && !local && !includeOthers) continue;
                    list.Add(new Target
                    {
                        Body = body, Player = player, Local = local,
                        Root = player != null ? player.transform : body.transform,
                        Label = local ? $"You ({Game.Location(main) ?? "?"})"
                              : player != null ? "Other player: " + player.name
                              : "Menu preview: " + PathOf(body.transform, 3),
                    });
                }
            }
            if (list.Count == 0 && main != null)
            {
                Game.LogOnce("nobody", "No PlayerBody component found; falling back to every renderer under the main player.");
                list.Add(new Target { Player = main, Root = main.transform, Local = true, Label = "You (no PlayerBody found)" });
            }
            return list.OrderByDescending(t => t.Local).ThenBy(t => t.Label).ToList();
        }

        /// <summary>Renderers of a LoddedSkin: its children, plus anything its _lods list points at (in case they were re-parented).</summary>
        static IEnumerable<Renderer> SkinRenderers(Component skin)
        {
            var set = new HashSet<Renderer>(skin.GetComponentsInChildren<Renderer>(true));
            var lods = Game.Get(skin, "_lods") as IEnumerable;
            if (lods != null)
                foreach (var l in lods)
                {
                    var c = l as Component;
                    if (c != null)
                        foreach (var r in c.GetComponentsInChildren<Renderer>(true)) set.Add(r);
                }
            return set;
        }

        static bool IsItemName(string n)
        {
            n = n.ToLowerInvariant();
            return n.StartsWith("item_") || n.StartsWith("weapon_") || n.Contains("equipment");
        }

        public static Scan Run(Target t)
        {
            var scan = new Scan { Target = t };
            var assigned = new HashSet<Renderer>();
            if (t.Body != null)
            {
                var skins = Game.Get(t.Body, "BodySkins") as IDictionary;
                if (skins == null)
                    Game.LogOnce("bodyskins", $"PlayerBody.BodySkins not found or not a dictionary (got {Game.Get(t.Body, "BodySkins")?.GetType().FullName ?? "null"}); body parts will show as 'other'.");
                else
                {
                    foreach (DictionaryEntry de in skins)
                    {
                        string part = de.Key?.ToString() ?? "?";
                        var skin = de.Value as Component;
                        var g = new Group { Kind = "body", Part = part, Open = true, Source = skin != null ? Clean(skin.gameObject.name) : "(none)" };
                        g.Name = $"{PartLabel(part)}: {g.Source}";
                        if (skin != null)
                            foreach (var r in SkinRenderers(skin).OrderBy(r => RelPath(r.transform, skin.transform)))
                                if (assigned.Add(r)) g.Entries.Add(new Entry { R = r, Path = RelPath(r.transform, skin.transform) });
                        scan.Groups.Add(g);
                    }
                    scan.Groups = scan.Groups.OrderBy(g => PartOrder(g.Part)).ThenBy(g => g.Part).ToList();
                }
            }

            // everything else under the character: gear by item (the top-most item_/weapon_ object above it), else "other"
            var extra = new Dictionary<string, Group>();
            if (t.Root != null)
            {
                foreach (var r in t.Root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || assigned.Contains(r)) continue;
                    Transform item = null;
                    for (var p = r.transform; p != null && p != t.Root; p = p.parent)
                        if (IsItemName(p.name)) item = p;
                    string key = item != null ? "Gear: " + Clean(item.name) : "Other: " + Clean(r.transform.parent != null ? r.transform.parent.name : r.name);
                    if (!extra.TryGetValue(key, out var g))
                        extra[key] = g = new Group { Name = key, Kind = item != null ? "gear" : "other", Source = Clean((item ?? r.transform).name) };
                    g.Entries.Add(new Entry { R = r, Path = RelPath(r.transform, item ?? t.Root) });
                }
            }
            scan.Groups.AddRange(extra.Values.OrderBy(g => g.Kind).ThenBy(g => g.Name));
            scan.Signature = t.Label + "|" + string.Join("|", scan.Groups.Select(g => g.Name + ":" + g.Entries.Count));
            return scan;
        }

        public static string OutfitNames(Scan s)
        {
            var names = s?.Body.Where(g => g.Part != "Hands").Select(g => g.Source).Where(n => n != "(none)").ToList() ?? new List<string>();
            string joined = names.Count > 0 ? string.Join("+", names) : "no_outfit";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) joined = joined.Replace(c, '-');
            return joined.Length > 120 ? joined.Substring(0, 120) : joined;
        }
    }
}
