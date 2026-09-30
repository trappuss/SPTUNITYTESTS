// Comparison sheet (0.10.0): the A/B turntables as one labelled PNG, made in game (before this, tools/contact_sheet.py
// in the cloud). Rows = outfits, columns = the 4 angles; each cell is the screenshot scaled down (bilinear), transparent
// captures are put on grey. Labels use a built-in 5x7 bitmap font (no font asset needed; unknown characters become '?').
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace COD2EFTInspector
{
    internal static class Sheet
    {
        static readonly Dictionary<char, byte[]> Font = new Dictionary<char, byte[]>
        {
                { 'A', new byte[] { 14,17,17,31,17,17,17 } },
                { 'B', new byte[] { 30,17,17,30,17,17,30 } },
                { 'C', new byte[] { 14,17,16,16,16,17,14 } },
                { 'D', new byte[] { 30,17,17,17,17,17,30 } },
                { 'E', new byte[] { 31,16,16,30,16,16,31 } },
                { 'F', new byte[] { 31,16,16,30,16,16,16 } },
                { 'G', new byte[] { 14,17,16,23,17,17,15 } },
                { 'H', new byte[] { 17,17,17,31,17,17,17 } },
                { 'I', new byte[] { 14,4,4,4,4,4,14 } },
                { 'J', new byte[] { 7,2,2,2,18,18,12 } },
                { 'K', new byte[] { 17,18,20,24,20,18,17 } },
                { 'L', new byte[] { 16,16,16,16,16,16,31 } },
                { 'M', new byte[] { 17,27,21,21,17,17,17 } },
                { 'N', new byte[] { 17,17,25,21,19,17,17 } },
                { 'O', new byte[] { 14,17,17,17,17,17,14 } },
                { 'P', new byte[] { 30,17,17,30,16,16,16 } },
                { 'Q', new byte[] { 14,17,17,17,21,18,13 } },
                { 'R', new byte[] { 30,17,17,30,20,18,17 } },
                { 'S', new byte[] { 15,16,16,14,1,1,30 } },
                { 'T', new byte[] { 31,4,4,4,4,4,4 } },
                { 'U', new byte[] { 17,17,17,17,17,17,14 } },
                { 'V', new byte[] { 17,17,17,17,17,10,4 } },
                { 'W', new byte[] { 17,17,17,21,21,21,10 } },
                { 'X', new byte[] { 17,17,10,4,10,17,17 } },
                { 'Y', new byte[] { 17,17,10,4,4,4,4 } },
                { 'Z', new byte[] { 31,1,2,4,8,16,31 } },
                { '0', new byte[] { 14,17,19,21,25,17,14 } },
                { '1', new byte[] { 4,12,4,4,4,4,14 } },
                { '2', new byte[] { 14,17,1,2,4,8,31 } },
                { '3', new byte[] { 30,1,1,14,1,1,30 } },
                { '4', new byte[] { 2,6,10,18,31,2,2 } },
                { '5', new byte[] { 31,16,30,1,1,17,14 } },
                { '6', new byte[] { 6,8,16,30,17,17,14 } },
                { '7', new byte[] { 31,1,2,4,8,8,8 } },
                { '8', new byte[] { 14,17,17,14,17,17,14 } },
                { '9', new byte[] { 14,17,17,15,1,2,12 } },
                { ' ', new byte[] { 0,0,0,0,0,0,0 } },
                { '-', new byte[] { 0,0,0,31,0,0,0 } },
                { '_', new byte[] { 0,0,0,0,0,0,31 } },
                { '.', new byte[] { 0,0,0,0,0,12,12 } },
                { ':', new byte[] { 0,12,12,0,12,12,0 } },
                { '(', new byte[] { 2,4,8,8,8,4,2 } },
                { ')', new byte[] { 8,4,2,2,2,4,8 } },
                { '[', new byte[] { 14,8,8,8,8,8,14 } },
                { ']', new byte[] { 14,2,2,2,2,2,14 } },
                { '/', new byte[] { 1,1,2,4,8,16,16 } },
                { '+', new byte[] { 0,4,4,31,4,4,0 } },
                { '=', new byte[] { 0,0,31,0,31,0,0 } },
                { '?', new byte[] { 14,17,1,2,4,0,4 } },
                { ',', new byte[] { 0,0,0,0,12,4,8 } }
        };

        static void Text(Color32[] px, int w, int h, int x, int y, string s, int scale, Color32 c)
        {
            // y = top of the text, image rows go bottom-up
            foreach (char ch0 in (s ?? "").ToUpperInvariant())
            {
                byte[] g;
                if (!Font.TryGetValue(ch0, out g)) g = Font['?'];
                for (int row = 0; row < 7; row++)
                    for (int col = 0; col < 5; col++)
                        if ((g[row] >> (4 - col) & 1) != 0)
                            for (int sy = 0; sy < scale; sy++)
                                for (int sx = 0; sx < scale; sx++)
                                {
                                    int X = x + col * scale + sx, Y = h - 1 - (y + row * scale + sy);
                                    if (X >= 0 && X < w && Y >= 0 && Y < h) px[Y * w + X] = c;
                                }
                x += 6 * scale;
                if (x > w - 6 * scale) break;
            }
        }

        /// <summary>Writes the sheet; rows = (label, 4 png paths). Returns the file or null.</summary>
        public static string Make(string outFile, string title, List<KeyValuePair<string, List<string>>> rows)
        {
            const int cellW = 480, pad = 6, labelH = 24, titleH = 34;
            int cols = 0;
            foreach (var r in rows) cols = Math.Max(cols, r.Value.Count);
            if (rows.Count == 0 || cols == 0) return null;
            int cellH = 270;
            // cell height from the first readable image's aspect
            foreach (var r in rows) foreach (var p in r.Value)
                if (File.Exists(p)) { var t0 = Load(p); if (t0 != null) { cellH = Mathf.RoundToInt(cellW * t0.height / (float)t0.width); UnityEngine.Object.Destroy(t0); } goto found; }
            found:
            int W = pad + cols * (cellW + pad), H = titleH + rows.Count * (labelH + cellH + pad) + pad;
            var px = new Color32[W * H];
            var bg = new Color32(24, 26, 29, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            Text(px, W, H, pad, 8, title, 3, new Color32(235, 235, 235, 255));
            var grey = new Color(0.5f, 0.5f, 0.5f);
            for (int ri = 0; ri < rows.Count; ri++)
            {
                int top = titleH + ri * (labelH + cellH + pad);
                Text(px, W, H, pad, top + 5, rows[ri].Key, 2, new Color32(120, 180, 255, 255));
                for (int ci = 0; ci < rows[ri].Value.Count; ci++)
                {
                    var tex = File.Exists(rows[ri].Value[ci]) ? Load(rows[ri].Value[ci]) : null;
                    if (tex == null) continue;
                    int x0 = pad + ci * (cellW + pad), y0 = top + labelH;
                    for (int y = 0; y < cellH; y++)
                        for (int x = 0; x < cellW; x++)
                        {
                            var c = tex.GetPixelBilinear((x + 0.5f) / cellW, 1f - (y + 0.5f) / cellH);
                            if (c.a < 1f) c = Color.Lerp(grey, new Color(c.r, c.g, c.b, 1f), c.a);
                            px[(H - 1 - (y0 + y)) * W + x0 + x] = c;
                        }
                    UnityEngine.Object.Destroy(tex);
                }
            }
            var outTex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            try
            {
                outTex.SetPixels32(px);
                outTex.Apply(false);
                File.WriteAllBytes(outFile, outTex.EncodeToPNG());
            }
            finally { UnityEngine.Object.Destroy(outTex); }
            return outFile;
        }

        static Texture2D Load(string path)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (t.LoadImage(File.ReadAllBytes(path))) return t;
            UnityEngine.Object.Destroy(t);
            return null;
        }
    }
}
