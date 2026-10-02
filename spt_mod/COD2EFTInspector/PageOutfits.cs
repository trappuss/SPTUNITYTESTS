// Outfits page (0.12.0): everything about what the character wears, in one place.
//   Wearing now: the four parts, which are try-ons, Restore / Keep this head.
//   Mod outfits: one row per outfit set of the installed mods, newest mod first, Wear puts the whole set on.
//   All items:  the full catalog (vanilla + mods), search / part / source filters, one Wear per piece.
// Wearing one piece keeps the other pieces already tried on (0.11.0).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace COD2EFTInspector
{
    public partial class InspectorPlugin
    {
        int _outSub;                       // 0 mod outfits, 1 all items
        bool _allSets;
        string _filter = "", _lastFilter = null, _part = "All", _source = "All";
        List<Outfit> _shown = new List<Outfit>();
        readonly ListState _setList = new ListState(), _itemList = new ListState();
        static readonly string[] PartFilters = { "All", "Top", "Pants", "Head", "Hands" };

        void DrawOutfitsPage()
        {
            DrawTargetPicker();
            DrawWearingNow();
            var cat = GetCatalog();
            int hit = Ui.Segs(null, new[] { $"Mod outfits ({_sets.Count})", $"All items ({cat.Items.Count})" }, _outSub,
                             new[] { "Whole outfits (top + pants + head) of the installed clothing mods", "Every top / pants / head / hands the server knows, vanilla too" });
            if (hit >= 0) Later(() => _outSub = hit);
            if (_outSub == 0) DrawModOutfits(); else DrawAllItems(cat);
        }

        void DrawWearingNow()
        {
            GUILayout.BeginVertical(Ui.CardBox);
            GUILayout.BeginHorizontal();
            GUILayout.Label("WEARING NOW", Ui.Section);
            GUILayout.FlexibleSpace();
            bool anyTry = _wearingByPart.Values.Any(IsTryOn);
            GUI.enabled = _canWear && (_inMenu ? MenuTryOn.Available : Wearer.HasOriginal);
            if (GUILayout.Button(new GUIContent("Restore my outfit", "Put your real outfit back (try-ons are never saved)"), anyTry ? Ui.Primary : Ui.Button))
            {
                if (_inMenu) StartCoroutine(MenuTryOn.Reshow(msg => { _status = msg; Refresh(true); }));
                else WearItems(Wearer.OriginalOutfit(GetCatalog()));
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (_inMenu && !MenuTryOn.Available)
                Ui.Help("Open the Character or Inventory screen once: Wear then dresses its preview. In the hideout / raid it dresses you.");
            foreach (var part in new[] { "Top", "Pants", "Head", "Hands" })
            {
                Outfit o;
                _wearingByPart.TryGetValue(part, out o);
                GUILayout.BeginHorizontal();
                GUILayout.Label(part, Ui.Small, GUILayout.Width(44));
                GUILayout.Label(o == null ? "<color=#949ca6>-</color>" : $"{o.Name}  <color=#949ca6>{o.Source}</color>", Ui.Row, GUILayout.ExpandWidth(true));
                if (IsTryOn(o)) Ui.Pill("TRY-ON", Ui.Accent);
                GUILayout.EndHorizontal();
            }
            Outfit head;
            if (!_inMenu && _wearingByPart.TryGetValue("Head", out head) && IsTryOn(head))
            {
                var cat = GetCatalog();
                GUILayout.BeginHorizontal();
                GUI.enabled = cat.HasHeadVoiceSelector && !Wearer.Busy;
                if (GUILayout.Button(new GUIContent("Keep this head", cat.HasHeadVoiceSelector
                        ? $"Saves {head.Name} to your PMC profile (kept after a restart). If its mod is removed later, SPT has to fix the profile."
                        : "Needs the WTT HeadVoiceSelector server mod"), Ui.Button, GUILayout.ExpandWidth(false)))
                    StartCoroutine(Wearer.SaveHead(head.Id, msg => _status = msg));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        void DrawModOutfits()
        {
            if (_sets.Count == 0)
            {
                Ui.Empty("No mod outfit found.", "Build and install a mod with the EFT Mod Builder, then press Reload in Settings > Diagnostics (or restart the game).");
                return;
            }
            var list = _allSets ? _sets : _sets.Take(8).ToList();
            // two-line rows: name / source + pieces
            _setList.Scroll = GUILayout.BeginScrollView(_setList.Scroll, GUILayout.ExpandHeight(true));
            foreach (var set in list)
            {
                GUILayout.BeginHorizontal(Ui.CardBox);
                GUILayout.BeginVertical();
                GUILayout.Label($"<b>{set.Name}</b>", Ui.Row);
                GUILayout.Label($"{set.Source}  ·  {string.Join(" + ", set.Pieces.Where(o => o.Part != "Hands").Select(o => o.Part))}", Ui.Small);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                int st;
                _setState.TryGetValue(set, out st);
                if (st == 2) Ui.Pill("WORN", Ui.Good); else if (st == 1) Ui.Pill("PART", Ui.Warn);
                GUI.enabled = _canWear;
                if (GUILayout.Button(new GUIContent("Wear", "Try this outfit on (not saved)" + (_canWear ? "" : WearHint())), Ui.Primary, GUILayout.Width(60))) WearItems(set.Pieces);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (_sets.Count > 8) { bool all = Ui.Check(_allSets, $"Show all {_sets.Count} outfits (newest mod first)"); if (all != _allSets) Later(() => _allSets = all); }
            GUILayout.EndScrollView();
        }

        string WearHint() =>
            Wearer.Busy || MenuTryOn.Busy ? " - busy with the last Wear" :
            _inMenu && !MenuTryOn.Available ? " - open the Character or Inventory screen first" : "";

        void DrawAllItems(Catalog cat)
        {
            GUILayout.BeginHorizontal();
            _filter = GUILayout.TextField(_filter ?? "", Ui.Field, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(new GUIContent("×", "Clear the search (name, id or bundle path)"), Ui.Icon, GUILayout.Width(22))) _filter = "";
            GUILayout.EndHorizontal();
            int hp = Ui.Segs("Part", PartFilters, Array.IndexOf(PartFilters, _part));
            if (hp >= 0) _part = PartFilters[hp];
            var sources = new List<string> { "All", "Mods only" };
            sources.AddRange(cat.Sources);
            int si = Math.Max(0, sources.IndexOf(_source));
            GUILayout.BeginHorizontal();
            GUILayout.Label("From", Ui.Small, GUILayout.Width(78));
            if (GUILayout.Button("◄", Ui.Seg, GUILayout.Width(26))) _source = sources[(si + sources.Count - 1) % sources.Count];
            GUILayout.Label($"<b>{_source}</b>", Ui.Label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("►", Ui.Seg, GUILayout.Width(26))) _source = sources[(si + 1) % sources.Count];
            GUILayout.EndHorizontal();

            string key = _filter + "|" + _part + "|" + _source;
            if (Event.current.type == EventType.Layout && key != _lastFilter)
            {
                _lastFilter = key;
                string f = (_filter ?? "").Trim();
                _shown = cat.Items.Where(o =>
                        (_part == "All" || o.Part == _part) &&
                        (_source == "All" || (_source == "Mods only" ? o.Source != "vanilla" : o.Source == _source)) &&
                        (f.Length == 0 || (o.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (o.Id ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (o.Bundle ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(o => o.Source == "vanilla").ThenBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(o => o.Part).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{_shown.Count} of {cat.Items.Count}" + (_missingBundles > 0 ? $"  ·  <color=#f26159>{_missingBundles} with a missing bundle</color>" : ""), Ui.Small);
            GUILayout.FlexibleSpace();
            _topsBringHands.Value = Ui.Check(_topsBringHands.Value, "Tops bring their hands", "Off: wear hands separately (Part: Hands)");
            GUILayout.EndHorizontal();
            var shown = _shown;
            VirtualList(_itemList, shown.Count, i =>
            {
                var o = shown[i];
                GUI.enabled = _canWear && o.BundleFound != false;
                if (GUILayout.Button(new GUIContent("Wear", (o.Part == "Top" && _topsBringHands.Value ? "Try on with its hands (not saved)" : "Try on (not saved)") + (_canWear ? "" : WearHint())),
                                     Ui.Button, GUILayout.Width(52), GUILayout.Height(Ui.RowH - 4))) WearItems(new List<Outfit> { o });
                GUI.enabled = true;
                GUILayout.Label(new GUIContent($"{o.Name}  <color=#949ca6>{o.Part} · {o.Source}</color>", $"{o.Part} '{o.Name}'  id {o.Id}  bundle {o.Bundle}"),
                                Ui.Row, GUILayout.ExpandWidth(true), GUILayout.Height(Ui.RowH));
                if (IsWorn(o)) Ui.Pill(IsTryOn(o) ? "TRY-ON" : "WORN", IsTryOn(o) ? Ui.Accent : Ui.Good);
                if (o.BundleFound == false) Ui.Pill("NO BUNDLE", Ui.Bad);
            });
        }
    }
}
