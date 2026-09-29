// EFT Mod Builder - pure logic (no UnityEngine types, unit-testable outside Unity).
//
// Evidence this follows (checked 2026-09-27 against SPT 4.1 + WTT-CommonLib 3.0.6 installed on this PC):
//  * SPT BundleLoader: reads <mod>/bundles.json = { "manifest": [ { "key", "dependencyKeys" } ] }, file at
//    <mod>/bundles/<key>. Keys are global: a key already added by another mod logs "Unable to add bundle".
//  * WTT CustomClothingService: loads every JSON array in <mod>/db/CustomClothing. type "top" needs suiteId,
//    outfitId, topId, handsId, topBundlePath, handsBundlePath; "bottom" needs suiteId, outfitId, bottomId,
//    bottomBundlePath and must NOT define top/hands fields. Every id must be new (24 hex chars); a second top that
//    reuses a handsId fails ("already exists"), so each top gets its own handsId even when bundles are shared.
//    traderId = name from WTT TraderIds (case-insensitive) or a 24-hex id. currencyId = 24-hex tpl.
//  * WTT CustomHeadService: loads JSON objects { "<headId>": { path, addHeadToPlayer, side, locales{lang:name} } }
//    from <mod>/db/CustomHeads.
//  * Vanilla dependency keys come from EFT's StreamingAssets/Windows/Windows.json (bear_body -> cubemaps, shaders);
//    a bundle's external references are "archive:/CAB-<32 hex>/..." strings, and the CAB name of a vanilla bundle is
//    the first entry of its UnityFS directory. (bear_body references exactly the cubemaps + shaders CABs.)
//  * SPT ModLoader loads every mod DLL into ONE AssemblyLoadContext and SPTarkov.DI de-duplicates [Injectable]
//    types by "Namespace.Name", so each generated mod DLL gets a unique assembly name + namespace (see PatchModDll).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EFTAutoPrefab
{
    public enum ClothingKind { Unknown, Top, Bottom, Head, Hands }

    public static class EFTModBuilderCore
    {
        // ------------------------------------------------------------------ constants

        public static readonly string[] TraderNames =
            { "RAGMAN", "PRAPOR", "THERAPIST", "SKIER", "PEACEKEEPER", "MECHANIC", "JAEGER", "FENCE", "REF", "BADGER" };

        public static readonly string[] CurrencyNames = { "Roubles", "Dollars", "Euros", "GP coins" };
        public static readonly string[] CurrencyTpls =
            { "5449016a4bdc2d6f028b456f", "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568", "5d235b4d86f7742e017bc88a" };

        public const string DefaultDependencyKeys = "shaders, cubemaps, assets/commonassets/physics/physicsmaterials.bundle";
        public static readonly string[] KnownDependencyKeys =
            { "shaders", "cubemaps", "assets/commonassets/physics/physicsmaterials.bundle" };

        // ------------------------------------------------------------------ ids

        static int _counter = new Random().Next(0, 0xFFFFFF);
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary>24 lowercase hex chars, ObjectId layout (4-byte time, 5 random, 3 counter).</summary>
        public static string NewMongoId()
        {
            var b = new byte[12];
            uint t = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            b[0] = (byte)(t >> 24); b[1] = (byte)(t >> 16); b[2] = (byte)(t >> 8); b[3] = (byte)t;
            var r = new byte[5]; Rng.GetBytes(r); Array.Copy(r, 0, b, 4, 5);
            int c = System.Threading.Interlocked.Increment(ref _counter) & 0xFFFFFF;
            b[9] = (byte)(c >> 16); b[10] = (byte)(c >> 8); b[11] = (byte)c;
            return ToHex(b);
        }

        public static bool IsMongoId(string s) =>
            s != null && s.Length == 24 && s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'));

        public static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>SPT ModValidator accepts only ^[a-zA-Z0-9-]+(\.[a-zA-Z0-9-]+)*$ (no underscores), and one bad
        /// GUID makes SPT load NO mods at all ("Errors were found with mods, NO MODS WILL BE LOADED").</summary>
        public static readonly Regex SptGuidRule = new Regex(@"^[a-zA-Z0-9-]+(\.[a-zA-Z0-9-]+)*$", RegexOptions.CultureInvariant);

        public static string MakeGuid(string author, string modName)
        {
            string a = Slug(author, "author", '-'), n = Slug(modName, "mod", '-');
            return "com." + a + "." + n;
        }

        public static string Slug(string s, string fallback, char sep = '_')
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != sep) sb.Append(sep);
            string r = sb.ToString().Trim(sep);
            return r.Length == 0 ? fallback : r;
        }

        public static string SafeFolderName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var r = new string((s ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return r.Length == 0 ? "NewMod" : r;
        }

        // ------------------------------------------------------------------ classification

        /// <summary>Kind from the prefab: hands Skins use paths without "Root_Joint/" (SkeletonHands); else by name.</summary>
        public static ClothingKind Classify(string prefabName, string bundleKey, bool? skinUsesRootJoint,
                                            IDictionary<BodyPart, string> aliases)
        {
            if (skinUsesRootJoint == false) return ClothingKind.Hands;
            var p = EFTAutoPrefabCore.Parse(prefabName ?? "", aliases).Part;
            if (p == BodyPart.Ignore)
            {
                string k = (bundleKey ?? "").ToLowerInvariant();
                string file = Path.GetFileNameWithoutExtension(k);
                if (k.StartsWith("heads/") || file.EndsWith("_head")) p = BodyPart.Head;
                else if (file.EndsWith("_top") || file.EndsWith("_upper")) p = BodyPart.Upper;
                else if (file.EndsWith("_bottom") || file.EndsWith("_lower")) p = BodyPart.Lower;
                else if (file.EndsWith("_hands") || file.EndsWith("_arms")) p = BodyPart.Hands;
            }
            switch (p)
            {
                case BodyPart.Upper: return ClothingKind.Top;
                case BodyPart.Lower: return ClothingKind.Bottom;
                case BodyPart.Head: return ClothingKind.Head;
                case BodyPart.Hands: return skinUsesRootJoint == true ? ClothingKind.Unknown : ClothingKind.Hands;
                default: return ClothingKind.Unknown;
            }
        }

        public static List<string> SplitKeys(string csv) =>
            (csv ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

        // ------------------------------------------------------------------ UnityFS reading

        public sealed class BundleInfo
        {
            public List<string> Cabs = new List<string>();          // own CAB names (directory entries without .resS etc.)
            public List<string> ExternalCabs = new List<string>();  // CABs referenced via archive:/CAB-xxx
            public HashSet<string> Found = new HashSet<string>();   // which of the 'find' strings occur (case-insensitive)
            public string Error;
        }

        static readonly Regex ArchiveRef = new Regex(@"archive:/(CAB-[0-9a-f]{32})/", RegexOptions.CultureInvariant);

        /// <summary>Reads a UnityFS bundle. headerOnly: just the directory (own CAB names), no data decompression.</summary>
        public static BundleInfo ReadBundle(string path, bool headerOnly) => ReadBundle(path, headerOnly, null);

        /// <summary>As ReadBundle, also reporting which of 'find' occur in the decompressed data (ASCII, case-insensitive).</summary>
        public static BundleInfo ReadBundle(string path, bool headerOnly, IEnumerable<string> find)
        {
            var needles = (find ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x.ToLowerInvariant()).Distinct().ToList();
            int carryLen = Math.Max(64, needles.Count > 0 ? needles.Max(x => x.Length) : 0);
            var info = new BundleInfo();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    string sig = CStr(br);
                    if (sig != "UnityFS") { info.Error = "not a UnityFS bundle (" + sig + ")"; return info; }
                    uint version = BE32(br);
                    CStr(br); CStr(br);
                    long size = BE64(br);
                    uint compSize = BE32(br), uncompSize = BE32(br), flags = BE32(br);
                    if (version >= 7) Align(fs, 16);
                    long afterHeader = fs.Position;

                    byte[] biRaw;
                    if ((flags & 0x80) != 0) { fs.Position = fs.Length - compSize; biRaw = br.ReadBytes((int)compSize); fs.Position = afterHeader; }
                    else biRaw = br.ReadBytes((int)compSize);
                    byte[] bi = Decompress(biRaw, (int)uncompSize, (int)(flags & 0x3F));
                    if (bi == null) { info.Error = "unsupported block-info compression " + (flags & 0x3F); return info; }

                    int p = 16;
                    int blockCount = BE32(bi, p); p += 4;
                    var blocks = new List<(uint u, uint c, ushort f)>();
                    for (int i = 0; i < blockCount; i++) { blocks.Add((BE32u(bi, p), BE32u(bi, p + 4), (ushort)((bi[p + 8] << 8) | bi[p + 9]))); p += 10; }
                    int nodeCount = BE32(bi, p); p += 4;
                    for (int i = 0; i < nodeCount; i++)
                    {
                        p += 8 + 8 + 4;
                        int e = Array.IndexOf(bi, (byte)0, p);
                        string name = Encoding.UTF8.GetString(bi, p, e - p);
                        p = e + 1;
                        if (name.StartsWith("CAB-") && name.IndexOf('.') < 0) info.Cabs.Add(name);
                    }
                    if (headerOnly) return info;

                    if ((flags & 0x200) != 0) Align(fs, 16);
                    var found = new HashSet<string>();
                    string carry = ""; // a reference can straddle two blocks
                    foreach (var b in blocks)
                    {
                        byte[] raw = br.ReadBytes((int)b.c);
                        byte[] data = Decompress(raw, (int)b.u, b.f & 0x3F);
                        if (data == null) { info.Error = "unsupported data compression " + (b.f & 0x3F); return info; }
                        // latin1 keeps a 1:1 byte->char mapping so the regex sees the raw ASCII
                        string text = carry + Latin1(data);
                        foreach (Match m in ArchiveRef.Matches(text)) found.Add(m.Groups[1].Value);
                        if (needles.Count > 0)
                        {
                            string lower = text.ToLowerInvariant();
                            foreach (var n in needles) if (!info.Found.Contains(n) && lower.Contains(n)) info.Found.Add(n);
                        }
                        carry = text.Length > carryLen ? text.Substring(text.Length - carryLen) : text;
                    }
                    foreach (var c in info.Cabs) found.Remove(c);
                    info.ExternalCabs = found.OrderBy(x => x).ToList();
                }
            }
            catch (Exception ex) { info.Error = ex.Message; }
            return info;
        }

        static string Latin1(byte[] d)
        {
            var ch = new char[d.Length];
            for (int i = 0; i < d.Length; i++) ch[i] = (char)d[i];
            return new string(ch);
        }

        static byte[] Decompress(byte[] src, int uncompressed, int type)
        {
            switch (type)
            {
                case 0: return src;
                case 2:
                case 3:
                    var dst = new byte[uncompressed];
                    int n = LZ4Decode(src, dst);
                    if (n != uncompressed) throw new InvalidDataException($"LZ4 size mismatch {n} != {uncompressed}");
                    return dst;
                case 1: return EFTLzma.Decode(src, uncompressed);
                default: return null;
            }
        }

        /// <summary>LZ4 raw block decoder (no frame).</summary>
        public static int LZ4Decode(byte[] src, byte[] dst)
        {
            int s = 0, d = 0, send = src.Length;
            while (s < send)
            {
                int token = src[s++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = src[s++]; lit += b; } while (b == 255); }
                Buffer.BlockCopy(src, s, dst, d, lit); s += lit; d += lit;
                if (s >= send) break;
                int off = src[s] | (src[s + 1] << 8); s += 2;
                int ml = token & 15;
                if (ml == 15) { int b; do { b = src[s++]; ml += b; } while (b == 255); }
                ml += 4;
                int m = d - off;
                if (off <= 0 || m < 0) throw new InvalidDataException("bad LZ4 offset");
                for (int i = 0; i < ml; i++) dst[d++] = dst[m++];
            }
            return d;
        }

        static string CStr(BinaryReader br)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = br.ReadByte()) != 0) bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        static uint BE32(BinaryReader br) { var b = br.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        static long BE64(BinaryReader br) { long hi = BE32(br); long lo = BE32(br); return (hi << 32) | lo; }
        static int BE32(byte[] b, int p) => b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3];
        static uint BE32u(byte[] b, int p) => (uint)BE32(b, p);
        static void Align(Stream s, int a) { long r = s.Position % a; if (r != 0) s.Position += a - r; }

        // ------------------------------------------------------------------ mod DLL

        public const string DllPlaceholder = "EFTAutoMod_ZZZZZZZZZZZZZZZZZZZZZZZZ";
        // MVID of the shipped EFTAutoModTemplate.bytes (read with System.Reflection.Metadata when it was built)
        public static readonly byte[] TemplateMvid =
        {
            0x34, 0x74, 0x0B, 0x9E, 0xDB, 0x7A, 0x2A, 0x4A, 0xB6, 0xBB, 0xD9, 0xEB, 0x16, 0x4F, 0xD5, 0xBA
        };

        public static string DllIdentity(string modGuid)
        {
            using (var md5 = MD5.Create())
                return "EFTAutoMod_" + ToHex(md5.ComputeHash(Encoding.UTF8.GetBytes((modGuid ?? "").ToLowerInvariant()))).Substring(0, 24);
        }

        /// <summary>
        /// Gives the template DLL a per-mod assembly name + namespace (same-length replacement of the placeholder in the
        /// metadata string heap, the attribute blob and the version resource) and a per-mod MVID.
        /// Verified: two patched copies load side by side in one AssemblyLoadContext with distinct DI keys.
        /// </summary>
        public static byte[] PatchModDll(byte[] template, string modGuid)
        {
            string ident = DllIdentity(modGuid);
            var outB = (byte[])template.Clone();
            int a = ReplaceAll(outB, Encoding.ASCII.GetBytes(DllPlaceholder), Encoding.ASCII.GetBytes(ident));
            int u = ReplaceAll(outB, Encoding.Unicode.GetBytes(DllPlaceholder), Encoding.Unicode.GetBytes(ident));
            byte[] mvid;
            using (var md5 = MD5.Create()) mvid = md5.ComputeHash(Encoding.UTF8.GetBytes("mvid:" + (modGuid ?? "").ToLowerInvariant()));
            int m = ReplaceAll(outB, TemplateMvid, mvid);
            if (a != 3 || u != 2 || m != 1)
                throw new InvalidDataException($"Template DLL did not match the expected layout (names {a}/3, resource {u}/2, mvid {m}/1).");
            return outB;
        }

        static int ReplaceAll(byte[] h, byte[] n, byte[] r)
        {
            int c = 0;
            for (int i = 0; i <= h.Length - n.Length; i++)
            {
                int j = 0;
                while (j < n.Length && h[i + j] == n[j]) j++;
                if (j == n.Length) { Buffer.BlockCopy(r, 0, h, i, r.Length); c++; i += n.Length - 1; }
            }
            return c;
        }

        // ------------------------------------------------------------------ JSON writing

        public static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string Num(double d) => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        public sealed class ClothingEntry
        {
            public ClothingKind Kind;           // Top or Bottom
            public string SuiteId, OutfitId, TopId, HandsId, BottomId;
            public string Name, Description;
            public string BundlePath;           // top or bottom bundle key
            public string HandsBundlePath;      // tops only
            public List<string> Side;           // null = WTT default
            public string Trader;
            public int LoyaltyLevel, ProfileLevel;
            public double Standing;
            public string CurrencyTpl;
            public int Price;
        }

        public sealed class HeadEntry
        {
            public string HeadId, Name, BundlePath;
            public List<string> Side;
            public bool AddHeadToPlayer = true;
        }

        public static string ClothingJson(IList<ClothingEntry> items)
        {
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                var f = new List<string>();
                bool top = c.Kind == ClothingKind.Top;
                f.Add("\"type\": " + Q(top ? "top" : "bottom"));
                f.Add("\"suiteId\": " + Q(c.SuiteId));
                f.Add("\"outfitId\": " + Q(c.OutfitId));
                if (top) { f.Add("\"topId\": " + Q(c.TopId)); f.Add("\"handsId\": " + Q(c.HandsId)); }
                else f.Add("\"bottomId\": " + Q(c.BottomId));
                if (c.Side != null) f.Add("\"side\": [" + string.Join(", ", c.Side.Select(Q)) + "]");
                f.Add("\"locales\": {\n      \"en\": {\n        \"name\": " + Q(c.Name) + ",\n        \"description\": " + Q(c.Description) + "\n      }\n    }");
                if (top) { f.Add("\"topBundlePath\": " + Q(c.BundlePath)); f.Add("\"handsBundlePath\": " + Q(c.HandsBundlePath)); }
                else f.Add("\"bottomBundlePath\": " + Q(c.BundlePath));
                f.Add("\"traderId\": " + Q(c.Trader));
                f.Add("\"loyaltyLevel\": " + c.LoyaltyLevel);
                f.Add("\"profileLevel\": " + c.ProfileLevel);
                f.Add("\"standing\": " + Num(c.Standing));
                f.Add("\"currencyId\": " + Q(c.CurrencyTpl));
                f.Add("\"price\": " + c.Price);
                sb.Append("  {\n    ").Append(string.Join(",\n    ", f)).Append("\n  }").Append(i < items.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("]\n").ToString();
        }

        public static string HeadsJson(IList<HeadEntry> items)
        {
            var sb = new StringBuilder("{\n");
            for (int i = 0; i < items.Count; i++)
            {
                var h = items[i];
                sb.Append("  ").Append(Q(h.HeadId)).Append(": {\n")
                  .Append("    \"path\": ").Append(Q(h.BundlePath)).Append(",\n")
                  .Append("    \"addHeadToPlayer\": ").Append(h.AddHeadToPlayer ? "true" : "false").Append(",\n")
                  .Append("    \"side\": [").Append(string.Join(", ", (h.Side ?? new List<string>()).Select(Q))).Append("],\n")
                  .Append("    \"locales\": {\n      \"en\": ").Append(Q(h.Name)).Append("\n    }\n")
                  .Append("  }").Append(i < items.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("}\n").ToString();
        }

        public static string BundlesJson(IList<KeyValuePair<string, List<string>>> entries)
        {
            var sb = new StringBuilder("{\n  \"manifest\": [\n");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("    {\n      \"key\": ").Append(Q(e.Key)).Append(",\n      \"dependencyKeys\": [");
                if (e.Value.Count > 0)
                    sb.Append("\n        ").Append(string.Join(",\n        ", e.Value.Select(Q))).Append("\n      ");
                sb.Append("]\n    }").Append(i < entries.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("  ]\n}\n").ToString();
        }

        public static string ModInfoJson(string guid, string name, string author, string version, string sptVersion,
                                         string wttVersion, string license, string url)
        {
            var f = new List<string>
            {
                "\"guid\": " + Q(guid), "\"name\": " + Q(name), "\"author\": " + Q(author), "\"version\": " + Q(version),
                "\"sptVersion\": " + Q(sptVersion), "\"wttVersion\": " + Q(wttVersion), "\"license\": " + Q(license),
            };
            if (!string.IsNullOrWhiteSpace(url)) f.Add("\"url\": " + Q(url));
            return "{\n  " + string.Join(",\n  ", f) + "\n}\n";
        }

        /// <summary>Reads "key" entries from any bundles.json (loose regex; enough to detect key clashes).</summary>
        public static List<string> ReadManifestKeys(string bundlesJsonText)
        {
            var keys = new List<string>();
            foreach (Match m in Regex.Matches(bundlesJsonText ?? "", "\"key\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                keys.Add(m.Groups[1].Value.Replace("\\/", "/").Replace("\\\\", "\\"));
            return keys;
        }

        public static bool IsValidSemVer(string v) =>
            Regex.IsMatch(v ?? "", @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");
    }
}
