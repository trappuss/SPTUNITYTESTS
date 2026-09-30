// Transparent screenshots by difference matting: the same frame rendered once on a black and once on a grey background
// (0.7.0 used white: its bloom lit the character, which came out 2-8 % see-through; from_pc/20260929-195957).
// Where the character covers the background both images agree; where it doesn't they differ by (white bg - black bg).
//   alpha = 1 - (W - B) / (Wbg - Bbg)        colour = (B - (1 - alpha) * Bbg) / alpha
// Wbg / Bbg are measured in the image corners, so a post effect that turns the "white" background grey (tonemapping)
// still gives the right alpha. It doesn't depend on the game's render pipeline keeping an alpha channel.
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class Matte
    {
        static Color Corners(Texture2D t)
        {
            int w = t.width - 1, h = t.height - 1;
            var c = t.GetPixel(0, 0) + t.GetPixel(w, 0) + t.GetPixel(0, h) + t.GetPixel(w, h);
            return c / 4f;
        }

        public static Texture2D Make(Texture2D black, Texture2D white)
        {
            if (black == null || white == null || black.width != white.width || black.height != white.height)
                throw new System.InvalidOperationException("the two captures differ in size");
            var B = black.GetPixels32();
            var W = white.GetPixels32();
            Color bb = Corners(black), wb = Corners(white);
            float dr = Mathf.Max(wb.r - bb.r, 0.05f), dg = Mathf.Max(wb.g - bb.g, 0.05f), db = Mathf.Max(wb.b - bb.b, 0.05f);
            var o = new Color32[B.Length];
            for (int i = 0; i < B.Length; i++)
            {
                float br = B[i].r / 255f, bg = B[i].g / 255f, bl = B[i].b / 255f;
                float a = 1f - ((W[i].r / 255f - br) / dr + (W[i].g / 255f - bg) / dg + (W[i].b / 255f - bl) / db) / 3f;
                a = Mathf.Clamp01(a);
                if (a > 0.96f) a = 1f;   // leftover glow from the background pass: solid
                if (a < 0.004f) { o[i] = new Color32(0, 0, 0, 0); continue; }
                float r = Mathf.Clamp01((br - (1f - a) * bb.r) / a), g = Mathf.Clamp01((bg - (1f - a) * bb.g) / a), b = Mathf.Clamp01((bl - (1f - a) * bb.b) / a);
                o[i] = new Color32((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f), (byte)(a * 255f + 0.5f));
            }
            var t = new Texture2D(black.width, black.height, TextureFormat.RGBA32, false);
            t.SetPixels32(o);
            t.Apply(false);
            return t;
        }
    }
}
