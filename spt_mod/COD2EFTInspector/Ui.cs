// Panel look (0.8.0): one dark theme, readable sizes, flat buttons with an accent colour, section cards, status colours.
// IMGUI only (no assets): every texture is a 1x1 colour made once. Styles are built on the first OnGUI (GUI.skin
// exists only there). Glyphs used are in Arial / WGL4 (the IMGUI default font): ► ▼ ● ×.
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class Ui
    {
        public static readonly Color Bg = new Color(0.09f, 0.10f, 0.11f, 0.97f);
        public static readonly Color Card = new Color(0.14f, 0.15f, 0.17f, 1f);
        public static readonly Color Line = new Color(0.22f, 0.24f, 0.27f, 1f);
        public static readonly Color Text = new Color(0.90f, 0.91f, 0.92f);
        public static readonly Color Muted = new Color(0.60f, 0.63f, 0.67f);
        public static readonly Color Accent = new Color(0.27f, 0.55f, 0.92f);
        public static readonly Color Good = new Color(0.36f, 0.78f, 0.45f);
        public static readonly Color Warn = new Color(0.95f, 0.72f, 0.25f);
        public static readonly Color Bad = new Color(0.95f, 0.38f, 0.35f);

        public static GUIStyle Window, Title, Chip, H, Label, Small, Mono, Button, Primary, Tiny, Tab, TabOn, Seg, SegOn,
                               CardBox, SectionHead, Value, Status, Badge, Toggle, Field, Banner;
        static bool _made;

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
                                         padding = new RectOffset(10, 10, 5, 5), margin = new RectOffset(3, 3, 3, 3) };
            s.normal.background = Tex(normal); s.hover.background = Tex(hover); s.active.background = Tex(active); s.focused.background = s.normal.background;
            s.onNormal.background = Tex(active); s.onHover.background = s.hover.background; s.onActive.background = s.active.background;
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = text;
            s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = text;
            return s;
        }

        static GUIStyle Lbl(int size, Color c, bool bold = false, bool wrap = true)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = wrap, richText = true, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                                                   margin = new RectOffset(3, 3, 2, 2), padding = new RectOffset(2, 2, 1, 1) };
            s.normal.textColor = c;
            return s;
        }

        /// <summary>Builds the styles once (call at the top of OnGUI). base size = 13 px before the panel scale.</summary>
        public static void Init()
        {
            if (_made) return;
            _made = true;
            Window = new GUIStyle(GUI.skin.window) { padding = new RectOffset(10, 10, 6, 8), border = new RectOffset(0, 0, 0, 0), overflow = new RectOffset(0, 0, 0, 0) };
            Window.normal.background = Window.onNormal.background = Tex(Bg);
            Window.normal.textColor = Window.onNormal.textColor = Text;
            Title = Lbl(15, Text, true, false);
            Chip = Lbl(11, Muted, false, false);
            H = Lbl(14, Text, true);
            Label = Lbl(13, Text);
            Small = Lbl(11, Muted);
            Mono = Lbl(11, Muted);
            Value = Lbl(12, Text, false, false); Value.alignment = TextAnchor.MiddleRight;
            Badge = Lbl(10, Color.white, true, false); Badge.padding = new RectOffset(5, 5, 1, 1); Badge.alignment = TextAnchor.MiddleCenter;
            Status = Lbl(12, Text); Status.padding = new RectOffset(8, 8, 5, 5);
            Status.normal.background = Tex(new Color(0.12f, 0.13f, 0.15f, 1f));
            Button = Flat(GUI.skin.button, new Color(0.21f, 0.23f, 0.26f), new Color(0.28f, 0.30f, 0.35f), new Color(0.17f, 0.18f, 0.21f), Text, 13);
            Primary = Flat(GUI.skin.button, Accent, new Color(0.36f, 0.63f, 0.98f), new Color(0.20f, 0.44f, 0.78f), Color.white, 13);
            Primary.fontStyle = FontStyle.Bold;
            Tiny = Flat(GUI.skin.button, new Color(0.18f, 0.19f, 0.22f), new Color(0.28f, 0.30f, 0.35f), new Color(0.15f, 0.16f, 0.18f), Muted, 11);
            Tiny.padding = new RectOffset(5, 5, 2, 2);
            Tab = Flat(GUI.skin.button, Bg, new Color(0.16f, 0.17f, 0.20f), Card, Muted, 13);
            Tab.padding = new RectOffset(14, 14, 7, 7); Tab.margin = new RectOffset(0, 2, 0, 0);
            TabOn = Flat(GUI.skin.button, Card, Card, Card, Text, 13);
            TabOn.padding = Tab.padding; TabOn.margin = Tab.margin; TabOn.fontStyle = FontStyle.Bold;
            Seg = Flat(GUI.skin.button, new Color(0.18f, 0.19f, 0.22f), new Color(0.26f, 0.28f, 0.32f), new Color(0.15f, 0.16f, 0.18f), Text, 12);
            Seg.margin = new RectOffset(0, 1, 2, 2); Seg.padding = new RectOffset(8, 8, 4, 4);
            SegOn = Flat(GUI.skin.button, Accent, Accent, Accent, Color.white, 12);
            SegOn.margin = Seg.margin; SegOn.padding = Seg.padding; SegOn.fontStyle = FontStyle.Bold;
            CardBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 6, 8), margin = new RectOffset(0, 0, 4, 4), border = new RectOffset(0, 0, 0, 0) };
            CardBox.normal.background = Tex(Card);
            SectionHead = new GUIStyle(Lbl(13, Text, true, false)) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(2, 2, 3, 3) };
            SectionHead.hover.textColor = Accent;
            SectionHead.active.textColor = Accent;
            Toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 13, wordWrap = true };
            Toggle.normal.textColor = Toggle.onNormal.textColor = Toggle.hover.textColor = Toggle.onHover.textColor = Text;
            Toggle.active.textColor = Toggle.onActive.textColor = Text;
            Banner = Lbl(14, Color.white, true, false); Banner.alignment = TextAnchor.MiddleCenter;
            Field = new GUIStyle(GUI.skin.textField) { fontSize = 13, padding = new RectOffset(6, 6, 4, 4) };
        }

        /// <summary>A coloured "pill" (WORN, MISSING, ...).</summary>
        public static void Pill(string text, Color c)
        {
            var old = GUI.backgroundColor;
            var r = GUILayoutUtility.GetRect(new GUIContent(text), Badge, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(r, Tex1(c));
            GUI.Label(r, text, Badge);
            GUI.backgroundColor = old;
        }

        static readonly System.Collections.Generic.Dictionary<Color, Texture2D> _texCache = new System.Collections.Generic.Dictionary<Color, Texture2D>();
        public static Texture2D Tex1(Color c)
        {
            Texture2D t;
            if (!_texCache.TryGetValue(c, out t) || t == null) _texCache[c] = t = Tex(c);
            return t;
        }

        public static void Rule() { var r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true)); GUI.DrawTexture(r, Tex1(Line)); }
    }
}
