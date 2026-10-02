// Panel look (0.12.0 rework): one compact dark theme and one widget per job, used the same way on every page.
//   Page tabs (header) > sub-tabs (Seg row) > sections (small caps header + optional "reset" link) > rows.
//   Buttons: Primary = the main action of a page, Button = everything else, Link = small text actions (reset, view ...).
// IMGUI only (no assets): every texture is a 1x1 colour made once. Styles are built on the first OnGUI (GUI.skin exists
// only there). Glyphs used are in Arial / WGL4 (the IMGUI default font): ◄ ► ▼ ● ×.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class Ui
    {
        public static readonly Color Bg = new Color(0.075f, 0.08f, 0.09f, 0.97f);
        public static readonly Color Bar = new Color(0.11f, 0.12f, 0.135f, 1f);
        public static readonly Color Card = new Color(0.13f, 0.14f, 0.16f, 1f);
        public static readonly Color RowHover = new Color(0.17f, 0.18f, 0.21f, 1f);
        public static readonly Color Line = new Color(0.21f, 0.23f, 0.26f, 1f);
        public static readonly Color Text = new Color(0.90f, 0.91f, 0.92f);
        public static readonly Color Muted = new Color(0.58f, 0.61f, 0.65f);
        public static readonly Color Accent = new Color(0.30f, 0.58f, 0.95f);
        public static readonly Color Good = new Color(0.36f, 0.78f, 0.45f);
        public static readonly Color Warn = new Color(0.95f, 0.72f, 0.25f);
        public static readonly Color Bad = new Color(0.95f, 0.38f, 0.35f);

        public static GUIStyle Window, Title, Chip, Section, Label, Bold, Small, Value, Button, Primary, Link, Icon, Page, PageOn,
                               Seg, SegOn, CardBox, Row, Status, Badge, Toggle, Field, Banner, BarBox, Body;
        static bool _made;

        public const float RowH = 24f;   // list rows (virtualised lists rely on this fixed height)

        public static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        static GUIStyle Flat(GUIStyle from, Color normal, Color hover, Color active, Color text, int size)
        {
            var s = new GUIStyle(from) { fontSize = size, alignment = TextAnchor.MiddleCenter, border = new RectOffset(0, 0, 0, 0),
                                         padding = new RectOffset(9, 9, 4, 4), margin = new RectOffset(2, 2, 2, 2), wordWrap = false };
            s.normal.background = Tex(normal); s.hover.background = Tex(hover); s.active.background = Tex(active); s.focused.background = s.normal.background;
            s.onNormal.background = Tex(active); s.onHover.background = s.hover.background; s.onActive.background = s.active.background;
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = text;
            s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = text;
            return s;
        }

        static GUIStyle Lbl(int size, Color c, bool bold = false, bool wrap = true)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = wrap, richText = true, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                                                   margin = new RectOffset(2, 2, 1, 1), padding = new RectOffset(2, 2, 1, 1) };
            s.normal.textColor = c;
            return s;
        }

        /// <summary>Builds the styles once (call at the top of OnGUI). Base size 12 px before the panel scale.</summary>
        public static void Init()
        {
            if (_made) return;
            _made = true;
            Window = new GUIStyle(GUI.skin.window) { padding = new RectOffset(0, 0, 0, 0), border = new RectOffset(0, 0, 0, 0), overflow = new RectOffset(0, 0, 0, 0) };
            Window.normal.background = Window.onNormal.background = Tex(Bg);
            Window.normal.textColor = Window.onNormal.textColor = Text;
            Title = Lbl(13, Text, true, false);
            Chip = Lbl(11, Muted, false, false);
            Section = Lbl(10, Muted, true, false); Section.margin = new RectOffset(2, 2, 8, 2);
            Label = Lbl(12, Text);
            Bold = Lbl(12, Text, true);
            Small = Lbl(11, Muted);
            Value = Lbl(11, Text, false, false); Value.alignment = TextAnchor.MiddleRight;
            Badge = Lbl(9, Color.white, true, false); Badge.padding = new RectOffset(5, 5, 1, 1); Badge.alignment = TextAnchor.MiddleCenter; Badge.margin = new RectOffset(3, 3, 4, 4);
            Button = Flat(GUI.skin.button, new Color(0.19f, 0.21f, 0.24f), new Color(0.26f, 0.28f, 0.33f), new Color(0.15f, 0.16f, 0.19f), Text, 12);
            Primary = Flat(GUI.skin.button, Accent, new Color(0.38f, 0.65f, 0.99f), new Color(0.22f, 0.46f, 0.80f), Color.white, 12);
            Primary.fontStyle = FontStyle.Bold;
            Link = Lbl(11, Accent, false, false); Link.hover.textColor = new Color(0.55f, 0.75f, 1f); Link.active.textColor = Color.white;
            Link.alignment = TextAnchor.MiddleRight;
            Icon = Flat(GUI.skin.button, new Color(0f, 0f, 0f, 0f), new Color(0.24f, 0.26f, 0.30f), new Color(0.15f, 0.16f, 0.19f), Muted, 12);
            Icon.padding = new RectOffset(4, 4, 2, 2); Icon.hover.textColor = Text;
            Page = Flat(GUI.skin.button, Bar, new Color(0.15f, 0.16f, 0.19f), Bg, Muted, 12);
            Page.padding = new RectOffset(6, 6, 7, 7); Page.margin = new RectOffset(0, 0, 0, 0);
            PageOn = Flat(GUI.skin.button, Bg, Bg, Bg, Text, 12);
            PageOn.padding = Page.padding; PageOn.margin = Page.margin; PageOn.fontStyle = FontStyle.Bold;
            Seg = Flat(GUI.skin.button, new Color(0.15f, 0.16f, 0.19f), new Color(0.23f, 0.25f, 0.29f), new Color(0.13f, 0.14f, 0.16f), Text, 11);
            Seg.margin = new RectOffset(0, 1, 1, 1); Seg.padding = new RectOffset(6, 6, 3, 3);
            SegOn = Flat(GUI.skin.button, Accent, Accent, Accent, Color.white, 11);
            SegOn.margin = Seg.margin; SegOn.padding = Seg.padding; SegOn.fontStyle = FontStyle.Bold;
            CardBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 5, 6), margin = new RectOffset(0, 0, 3, 3), border = new RectOffset(0, 0, 0, 0) };
            CardBox.normal.background = Tex(Card);
            Row = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, wordWrap = false, clipping = TextClipping.Clip,
                                                 alignment = TextAnchor.MiddleLeft, padding = new RectOffset(6, 4, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            Row.normal.textColor = Text;
            Row.hover.background = Tex(RowHover); Row.hover.textColor = Text;
            Status = Lbl(11, Text, false, true); Status.padding = new RectOffset(10, 10, 5, 5); Status.margin = new RectOffset(0, 0, 0, 0);
            Toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 12, wordWrap = false };
            Toggle.normal.textColor = Toggle.onNormal.textColor = Toggle.hover.textColor = Toggle.onHover.textColor = Text;
            Toggle.active.textColor = Toggle.onActive.textColor = Text;
            Banner = Lbl(13, Color.white, true, false); Banner.alignment = TextAnchor.MiddleCenter;
            Field = new GUIStyle(GUI.skin.textField) { fontSize = 12, padding = new RectOffset(6, 6, 3, 3) };
            BarBox = new GUIStyle { padding = new RectOffset(8, 6, 4, 4), margin = new RectOffset(0, 0, 0, 0) };
            BarBox.normal.background = Tex(Bar);
            Body = new GUIStyle { padding = new RectOffset(8, 8, 6, 4), margin = new RectOffset(0, 0, 0, 0) };
        }

        // ------------------------------------------------------------------ widgets (the same on every page)

        static readonly Dictionary<Color, Texture2D> _texCache = new Dictionary<Color, Texture2D>();
        public static Texture2D Tex1(Color c)
        {
            Texture2D t;
            if (!_texCache.TryGetValue(c, out t) || t == null) _texCache[c] = t = Tex(c);
            return t;
        }

        public static void Rule() { var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true)); GUI.DrawTexture(r, Tex1(Line)); }

        /// <summary>A coloured "pill" (WORN, MISSING, ...).</summary>
        public static void Pill(string text, Color c)
        {
            var r = GUILayoutUtility.GetRect(new GUIContent(text), Badge, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(r, Tex1(c));
            GUI.Label(r, text, Badge);
        }

        /// <summary>Section header in small caps; with a reset action a "reset" link on the right. Returns true when reset was clicked.</summary>
        public static bool Header(string title, string resetTip = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(title.ToUpperInvariant(), Section);
            GUILayout.FlexibleSpace();
            bool hit = resetTip != null && GUILayout.Button(new GUIContent("reset", resetTip), Link, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            return hit;
        }

        /// <summary>A row of buttons, the selected one highlighted. Returns the clicked index or -1.</summary>
        public static int Segs(string label, string[] items, int selected, string[] tips = null)
        {
            int hit = -1;
            GUILayout.BeginHorizontal();
            if (label != null) GUILayout.Label(label, Small, GUILayout.Width(78));
            for (int i = 0; i < items.Length; i++)
                if (GUILayout.Button(new GUIContent(items[i], tips != null && i < tips.Length ? tips[i] : null), i == selected ? SegOn : Seg)) hit = i;
            GUILayout.EndHorizontal();
            return hit;
        }

        /// <summary>A slider row: label, slider, value; with a default a small "R" puts it back.</summary>
        public static float Slider(string label, float v, float min, float max, float? def = null, string fmt = "0.##", string tip = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tip), Label, GUILayout.Width(98));
            v = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(80));
            GUILayout.Label(v.ToString(fmt), Value, GUILayout.Width(44));
            if (def.HasValue)
            {
                GUI.enabled = !Mathf.Approximately(v, def.Value);
                if (GUILayout.Button(new GUIContent("R", "Back to " + def.Value.ToString(fmt)), Icon, GUILayout.Width(20))) v = def.Value;
                GUI.enabled = true;
            }
            else GUILayout.Space(24);
            GUILayout.EndHorizontal();
            return v;
        }

        public static bool Check(bool v, string text, string tip = null) => GUILayout.Toggle(v, new GUIContent(" " + text, tip), Toggle);

        /// <summary>A short muted help line.</summary>
        public static void Help(string text) => GUILayout.Label(text, Small);

        /// <summary>Empty-state card: what is missing and what to do.</summary>
        public static void Empty(string what, string todo)
        {
            GUILayout.BeginVertical(CardBox);
            GUILayout.Label(what, Bold);
            if (todo != null) GUILayout.Label(todo, Small);
            GUILayout.EndVertical();
        }
    }
}
