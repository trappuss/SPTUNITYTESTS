// Panel frame (0.12.0 rework): header (title, where you are, close), 4 page tabs, the page, and a fixed bottom bar
// (photo mode, screenshot, reset all, status line). Pages: PageOutfits.cs, PagePhoto.cs, PageInspect.cs, PageSettings.cs.
//
// Speed rules for every page (the 0.11 panel lagged because it broke them):
//  * nothing expensive inside OnGUI: lists derived from the catalog / scan / materials are built in EnsureView() on the
//    Layout event, only after InvalidateView() (a scan, a Wear, a catalog reload, a filter change);
//  * long lists draw only the rows in view (VirtualList);
//  * game lookups go through Game, which caches reflection and the main player per frame.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace COD2EFTInspector
{
    public partial class InspectorPlugin
    {
        const int PageOutfits = 0, PagePhoto = 1, PageInspect = 2, PageSettings = 3;
        static readonly string[] PageNames = { "Outfits", "Photo", "Inspect", "Settings" };
        const float MinW = 400f, MinH = 380f;
        int _page = PageOutfits;
        bool _statusOpen;

        // ------------------------------------------------------------------ view model (rebuilt on demand, on the Layout event)

        bool _viewDirty = true;
        bool _inMenu, _canWear;
        string _where = "";
        HashSet<string> _worn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Catalog.OutfitSet> _sets = new List<Catalog.OutfitSet>();
        readonly Dictionary<Catalog.OutfitSet, int> _setState = new Dictionary<Catalog.OutfitSet, int>();   // 0 none, 1 part, 2 all worn
        readonly Dictionary<string, Outfit> _wearingByPart = new Dictionary<string, Outfit>();
        int _missingBundles;

        // IMGUI lays the panel out on the Layout event and replays that layout for the input / repaint events of the same
        // frame. A click that adds or removes controls (a page switch, a fold, a toggle that shows more options) must wait
        // for the next Layout, or Unity throws "Getting control N's position in a group with only N controls".
        readonly List<Action> _later = new List<Action>();
        void Later(Action a) => _later.Add(a);

        void RunLater()
        {
            if (_later.Count == 0) return;
            var todo = _later.ToArray();
            _later.Clear();
            foreach (var a in todo) try { a(); } catch (Exception e) { Game.LogOnce("later:" + e.Message, "Panel action failed: " + e); }
        }

        void InvalidateView() { _viewDirty = true; _lastFilter = null; _matsDirty = true; }

        /// <summary>Cheap per-frame values, then the derived lists if something changed. Called on the Layout event only,
        /// so Layout and the events after it see the same rows.</summary>
        void EnsureView()
        {
            var main = Game.MainPlayer();
            _inMenu = main == null;
            _canWear = CanWear();
            if (_ticksSeen != _ticks || _where.Length == 0) { _ticksSeen = _ticks; _where = Where(); }
            if (!_viewDirty) return;
            _viewDirty = false;
            var cat = GetCatalog();
            _worn = Worn();
            _sets = cat.ModSets();
            _setState.Clear();
            foreach (var set in _sets)
            {
                var shown = set.Pieces.Where(o => o.Part != "Hands").ToList();   // hands are often shared (the game's default hands)
                int n = shown.Count(IsWorn);
                _setState[set] = n == 0 ? 0 : n == shown.Count ? 2 : 1;
            }
            _wearingByPart.Clear();
            foreach (var part in new[] { "Top", "Pants", "Head", "Hands" })
            {
                string tryId = _inMenu ? MenuTryOn.TryOnId(part) : Wearer.TryOnId(Wearer.BodyKey(part));
                var o = (tryId != null ? cat.ById(tryId) : null) ?? cat.Items.FirstOrDefault(x => x.Part == part && IsWorn(x));
                if (o != null) _wearingByPart[part] = o;
            }
            _missingBundles = cat.Items.Count(o => o.BundleFound == false);
        }
        int _ticksSeen = -1;

        /// <summary>Where we are, in a few words (header): decides what Wear / photo mode act on.</summary>
        string Where()
        {
            var p = Game.MainPlayer();
            if (p != null) { var loc = Game.Location(p); return loc == "hideout" ? "Hideout" : "Raid" + (string.IsNullOrEmpty(loc) ? "" : " · " + loc); }
            return MenuTryOn.Available ? "Main menu · preview ready" : "Main menu";
        }

        bool IsTryOn(Outfit o)
        {
            if (o == null) return false;
            string id = _inMenu ? MenuTryOn.TryOnId(o.Part) : Wearer.TryOnId(Wearer.BodyKey(o.Part));
            return id != null && id == o.Id;
        }

        // ------------------------------------------------------------------ window

        void OnGUI()
        {
            if (_photo.Play && !_capturing)
            {
                Ui.Init();
                var r = new Rect(Screen.width / 2f - 170f, 18f, 340f, 30f);
                GUI.DrawTexture(r, Ui.Tex1(new Color(0f, 0f, 0f, 0.6f)));
                GUI.Label(r, "PLAY MODE  ·  Esc to leave" + (_frozen ? "  ·  FROZEN" : _speed < 1f ? $"  ·  {_speed:0.##}x" : ""), Ui.Banner);
                return;
            }
            if (!_open || _capturing) return;
            if (_unlockCursor.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            Ui.Init();
            if (Event.current.type == EventType.Layout) { RunLater(); try { EnsureView(); } catch (Exception e) { Game.LogOnce("view:" + e.Message, "Panel view failed: " + e); } }
            var m = GUI.matrix;
            float s = Mathf.Clamp(_scale.Value, 0.75f, 2.5f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float sw = Screen.width / s, sh = Screen.height / s;
            if (_resizing)
            {
                if (!Input.GetMouseButton(0)) { _resizing = false; _winW.Value = _win.width; _winH.Value = _win.height; }
                else
                {
                    var mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / s;
                    _win.width = Mathf.Clamp(mp.x - _win.x + 6f, MinW, Mathf.Max(MinW, sw));
                    _win.height = Mathf.Clamp(mp.y - _win.y + 6f, MinH, Mathf.Max(MinH, sh));
                }
            }
            if (_win.x < 0f) _win.x = Mathf.Max(0f, sw - _win.width - 12f);   // docked right (default)
            // keep the panel on screen (a smaller resolution or a bigger scale can push it off)
            _win.width = Mathf.Min(_win.width, Mathf.Max(MinW, sw));
            _win.height = Mathf.Min(_win.height, Mathf.Max(MinH, sh));
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, sw - 80f));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, sh - 40f));
            var before = _win.position;
            _win = GUILayout.Window(0x0C0D2EF, _win, DrawWindow, GUIContent.none, Ui.Window, GUILayout.Width(_win.width), GUILayout.Height(_win.height));
            if (_win.position != before) _moved = true;
            if (_moved && !Input.GetMouseButton(0)) { _moved = false; _winX.Value = _win.x; _winY.Value = _win.y; }
            GUI.matrix = m;
        }
        bool _moved;

        void DockRight() { _win.x = -1f; _winX.Value = -1f; }

        void DrawWindow(int id)
        {
            try
            {
                DrawHeader();
                DrawPageTabs();
                GUILayout.BeginVertical(Ui.Body, GUILayout.ExpandHeight(true));
                switch (_page)
                {
                    case PagePhoto: DrawPhotoPage(); break;
                    case PageInspect: DrawInspectPage(); break;
                    case PageSettings: DrawSettingsPage(); break;
                    default: DrawOutfitsPage(); break;
                }
                GUILayout.EndVertical();
                DrawBottomBar();
            }
            catch (Exception e)
            {
                // a layout exception leaves groups open; the next frame starts clean
                Game.LogOnce("gui:" + e.Message, "Panel error: " + e);
                if (!(e is ArgumentException)) GUILayout.Label("Panel error (see BepInEx log): " + e.Message, Ui.Label);
            }
            // resize grip, bottom-right corner (the size is kept in the config)
            var grip = new Rect(_win.width - 14f, _win.height - 14f, 14f, 14f);
            GUI.Label(grip, "◢", Ui.Small);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && grip.Contains(Event.current.mousePosition)) { _resizing = true; Event.current.Use(); }
            GUI.DragWindow(new Rect(0, 0, _win.width - 30f, 28f));
        }

        void DrawHeader()
        {
            GUILayout.BeginHorizontal(Ui.BarBox, GUILayout.Height(28f));
            GUILayout.Label("COD2EFT Inspector", Ui.Title, GUILayout.ExpandWidth(false));
            GUILayout.Label($"v{Version}  ·  {_where}" + (_photo.Active ? "  ·  <color=#f2b840>PHOTO MODE</color>" : ""), Ui.Chip, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(new GUIContent("×", $"Close the panel ({_panelKey.Value}, or Esc)"), Ui.Icon, GUILayout.Width(24))) Later(TogglePanel);
            GUILayout.EndHorizontal();
        }

        void DrawPageTabs()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < PageNames.Length; i++)
                if (GUILayout.Button(PageNames[i], i == _page ? Ui.PageOn : Ui.Page, GUILayout.ExpandWidth(true)) && _page != i)
                {
                    int to = i;
                    Later(() => { _page = to; if (to == PageInspect) RescanTarget(); });   // fresh mesh list when you look at it
                }
            GUILayout.EndHorizontal();
        }

        /// <summary>The character the Outfits / Inspect pages show: ◄ name ► and a rescan button.</summary>
        void DrawTargetPicker()
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = _targets.Count > 1;
            if (GUILayout.Button(new GUIContent("◄", "Previous character"), Ui.Icon, GUILayout.Width(24))) Later(() => SelectTarget(_ti - 1));
            GUI.enabled = true;
            GUILayout.Label(_targets.Count > 0 ? $"<b>{_targets[_ti].Label}</b>" + (_targets.Count > 1 ? $"  <color=#949ca6>{_ti + 1}/{_targets.Count}</color>" : "")
                                               : "<color=#949ca6>No character on screen</color>", Ui.Label, GUILayout.ExpandWidth(true));
            GUI.enabled = _targets.Count > 1;
            if (GUILayout.Button(new GUIContent("►", "Next character"), Ui.Icon, GUILayout.Width(24))) Later(() => SelectTarget(_ti + 1));
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Rescan", "Look for characters again (after opening the Character screen, entering the hideout ...)"), Ui.Button, GUILayout.Width(64))) Later(() => Refresh(true));
            GUILayout.EndHorizontal();
        }

        void DrawBottomBar()
        {
            GUILayout.BeginVertical(Ui.BarBox);
            GUILayout.BeginHorizontal();
            GUI.enabled = _photo.Active || !_inMenu;
            if (GUILayout.Button(new GUIContent(_photo.Active ? "Exit photo mode" : "Photo mode",
                    _photo.Active ? "Back to the normal camera; camera, lights, background and character are restored"
                                  : _inMenu ? "Photo mode works on your own character: raid or hideout" : "Orbit camera and studio lights around your character"),
                    _photo.Active ? Ui.Button : Ui.Primary, GUILayout.Width(118))) Later(TogglePhoto);
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent($"Screenshot ({_shotKey.Value})", "PNG + a .txt of the outfit; the panel and HUD are hidden for the capture"), Ui.Button)) TakeScreenshot();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Reset all", "Photo mode off and everything as before: camera, lights, background, pose, meshes, materials, outfit"), Ui.Button)) Later(ResetAll);
            GUILayout.EndHorizontal();
            DrawStatusLine();
            GUILayout.EndVertical();
        }

        /// <summary>What the mouse is over (tooltip), else the last message coloured by outcome; click for the full text.</summary>
        void DrawStatusLine()
        {
            string tip = GUI.tooltip;
            bool busy = Wearer.Busy || MenuTryOn.Busy || _capturing;
            string text; Color c;
            if (!string.IsNullOrEmpty(tip)) { text = tip; c = Ui.Muted; }
            else if (string.IsNullOrEmpty(_status)) { text = busy ? "Working ..." : "Ready. Hover over anything for help."; c = busy ? Ui.Warn : Ui.Muted; }
            else
            {
                string low = _status.ToLowerInvariant();
                c = busy ? Ui.Warn : low.Contains("fail") || low.Contains("error") || low.Contains("not found") ? Ui.Bad
                  : low.StartsWith("note") || low.Contains("warning") ? Ui.Warn : Ui.Good;
                int max = _statusOpen ? 2000 : 120;
                text = _status.Length <= max ? _status : _status.Substring(0, max) + (_statusOpen ? " ..." : "  [more]");
                if (_statusOpen && _status.Length > 120) text += "\n(full text also in BepInEx\\LogOutput.log)";
            }
            Ui.Status.normal.textColor = Ui.Status.hover.textColor = Ui.Status.active.textColor = c;
            if (GUILayout.Button(new GUIContent("●  " + text), Ui.Status, GUILayout.ExpandWidth(true))) _statusOpen = !_statusOpen;
        }

        // ------------------------------------------------------------------ lists that draw only the rows in view

        sealed class ListState { public Vector2 Scroll; public float ViewH = 300f; public int First, Count = 20; }

        /// <summary>A scroll list of n fixed-height rows (Ui.RowH) that only lays out the visible ones. The range is fixed on
        /// the Layout event, so every event of a frame sees the same rows.</summary>
        static void VirtualList(ListState st, int n, Action<int> row, params GUILayoutOption[] opts)
        {
            if (Event.current.type == EventType.Layout)
            {
                st.First = Mathf.Clamp((int)(st.Scroll.y / Ui.RowH), 0, Math.Max(0, n - 1));
                st.Count = Mathf.CeilToInt(st.ViewH / Ui.RowH) + 2;
            }
            st.Scroll = GUILayout.BeginScrollView(st.Scroll, opts.Length > 0 ? opts : new[] { GUILayout.ExpandHeight(true) });
            int first = Math.Min(st.First, Math.Max(0, n - 1)), last = Math.Min(n, first + st.Count);
            if (first > 0) GUILayout.Space(first * Ui.RowH);
            for (int i = first; i < last; i++)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(Ui.RowH));
                row(i);
                GUILayout.EndHorizontal();
            }
            if (n > last) GUILayout.Space((n - last) * Ui.RowH);
            GUILayout.EndScrollView();
            if (Event.current.type == EventType.Repaint) st.ViewH = Mathf.Max(60f, GUILayoutUtility.GetLastRect().height);
        }
    }
}
