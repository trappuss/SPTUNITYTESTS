// EFT Auto Prefabber - pure logic (no UnityEngine types, so it can be unit-tested outside Unity).
//
// Evidence this is built on (checked against SPT 4.1 game files, 2026-09-27):
//  * Diz.Skinning.Skin.ApplySkin (game Assembly-CSharp): for i in _bonePaths -> skeleton.Bones.TryGetValue(path),
//    and THROWS "no bone with name {path} found" if any path is missing; then rootBone = Bones[_rootBonePath].
//    => every path must exist, and _bonePaths[i] must describe smr.bones[i] (same order/length).
//  * PlayerBody.SetSkin: hands use PlayerBody.SkeletonHands (keys relative to Root_Joint, e.g. "Base HumanPelvis/..."),
//    body/feet/head use SkeletonRootJoint (keys "Root_Joint/Base HumanPelvis/...").
//  * Vanilla prefabs (273 clothing/head bundles + 108 hands bundles): root = LoddedSkin (+ LODGroup, not on hands);
//    each mesh child = SkinnedMeshRenderer + Skin + HotObject (+ RainCondensator on hands); SMR bones/rootBone are
//    empty in the bundle (filled at runtime); LoddedSkin._lods lists every Skin, including several meshes per LOD
//    (e.g. top_pmc_cultist: base_lod0, base_lod1, hood_lod0, hood_lod1).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EFTAutoPrefab
{
    public enum BodyPart { Ignore, Upper, Lower, Head, Hands }

    /// <summary>
    /// Alternative meshes the game swaps in at runtime (same bones, same material slots):
    /// tops (TorsoSkin): Armor = armor worn, Vest = rig / vest-like armor / backpack worn;
    /// heads (HeadSkin): FaceCover = a full (non-half) face cover is worn.
    /// </summary>
    public enum MeshState { Base, Armor, Vest, FaceCover }

    public sealed class NameParseResult
    {
        public BodyPart Part = BodyPart.Ignore;
        public string Variant = "";
        public string Piece = "";
        public int Lod;
        public MeshState State = MeshState.Base;
    }

    public static class EFTAutoPrefabCore
    {
        // ---------------------------------------------------------------- name parsing

        public static readonly Dictionary<BodyPart, string> DefaultAliases = new Dictionary<BodyPart, string>
        {
            { BodyPart.Upper, "upper" },
            { BodyPart.Lower, "lower" },
            { BodyPart.Head,  "head" },
            { BodyPart.Hands, "hands, hand, arms, arm" },
        };

        static readonly Regex BlenderDup = new Regex(@"\.\d{3}$", RegexOptions.CultureInvariant);
        static readonly Regex LodSuffix = new Regex(@"[_\-\. ]?lod[_\-]?(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string[] SplitAliases(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return new string[0];
            return csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(a => a.Trim().ToLowerInvariant())
                      .Where(a => a.Length > 0)
                      .Distinct()
                      .ToArray();
        }

        // Vanilla mesh names (SPT 4.1 prefabs): tops *_base_LOD0 / *_AR_LOD0 (armor) / *_CR_LOD0 (rig), heads *_lod0_base / *_lod0_custom.
        static readonly Regex StateSuffix = new Regex(@"[_\-\. ](armou?r|ar|vest|rig|cr|facecover|face_cover|custom|base)$",
                                                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // COD2EFT sub-meshes: "<name>_<Part>_<label>", written by 'Separate by COD material'
        // ("mp_milsim_us_sf_1_1_Lower_material_5c0b2a55701ee9c2", "..._Upper_mtl_c_t9_vest") and with 'Join parts' off
        // ("..._Upper_00"). COD2EFT always capitalises the part word, so this is matched case-sensitively.
        static readonly Regex CodSubMesh = new Regex(@"^(?<name>.+?)_(?<part>Head|Upper|Lower|Hands)_(?<tail>.+)$",
                                                     RegexOptions.CultureInvariant);
        // tails that keep their documented meaning: a state word, a LOD, or a one-character variant ("Upper_1")
        static readonly Regex CodSubMeshKeep = new Regex(@"^(armou?r|ar|vest|rig|cr|facecover|face_cover|custom|base|lod\d+|\d|[a-z])$",
                                                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static MeshState StateFromWord(string w)
        {
            switch ((w ?? "").ToLowerInvariant())
            {
                case "armor": case "armour": case "ar": return MeshState.Armor;
                case "vest": case "rig": case "cr": return MeshState.Vest;
                case "facecover": case "face_cover": case "custom": return MeshState.FaceCover;
                default: return MeshState.Base;
            }
        }

        /// <summary>Which states a part can have (tops: armor/vest, heads: face cover).</summary>
        public static bool StateAllowed(BodyPart part, MeshState st) =>
            st == MeshState.Base ||
            (part == BodyPart.Upper && (st == MeshState.Armor || st == MeshState.Vest)) ||
            (part == BodyPart.Head && st == MeshState.FaceCover);

        /// <summary>
        /// Parses a mesh object name such as "test1_upper", "kleo_uppera", "upper_1", "arms", "head_lod1",
        /// "upper_armor", "jacket_upper_vest_lod1", "head_facecover",
        /// and COD2EFT sub-meshes "kleo_Upper_material_5c0b2a55" / "kleo_Upper_00" (a piece of kleo's Upper).
        /// Pattern: [piece + separator] + part alias + [optional '_'/'-'] + [variant = digits or ONE letter]
        ///          + [_state] + [_lodN]   (state and LOD suffix may come in either order).
        /// Different pieces (prefixes) with the same part+variant end up in the same prefab;
        /// a different variant suffix makes a separate prefab; a state suffix makes an alternative mesh of the same piece.
        /// </summary>
        public static NameParseResult Parse(string objectName, IDictionary<BodyPart, string> aliasCsv)
        {
            var r = new NameParseResult();
            if (string.IsNullOrWhiteSpace(objectName)) return r;
            objectName = BlenderDup.Replace(objectName.Trim(), "");   // "jacket_upper.001" -> "jacket_upper"
            string n = objectName;
            // "<name>_<Part>_<label>" -> "<name>_<label>_<Part>": the label becomes part of the piece, so the sub-mesh
            // joins its part's prefab instead of being ignored (or read as a variant / state)
            var cm = CodSubMesh.Match(n);
            if (cm.Success && !CodSubMeshKeep.IsMatch(cm.Groups["tail"].Value))
                n = objectName = cm.Groups["name"].Value + "_" + cm.Groups["tail"].Value + "_" + cm.Groups["part"].Value;

            bool lodDone = false, stateDone = false;
            for (int pass = 0; pass < 2; pass++)
            {
                if (!lodDone)
                {
                    var lm = LodSuffix.Match(n);
                    if (lm.Success && lm.Index > 0)
                    {
                        r.Lod = int.Parse(lm.Groups[1].Value);
                        n = n.Substring(0, lm.Index);
                        lodDone = true;
                    }
                }
                if (!stateDone)
                {
                    var sm = StateSuffix.Match(n);
                    if (sm.Success && sm.Index > 0)
                    {
                        r.State = StateFromWord(sm.Groups[1].Value);
                        n = n.Substring(0, sm.Index);
                        stateDone = true;
                    }
                }
            }

            if (MatchPart(n, aliasCsv, r)) return r;
            if (stateDone)
            {
                // a trailing word such as "base"/"custom" was part of the name, not a state: retry without state handling
                var r2 = new NameParseResult();
                string n2 = objectName.Trim();
                var lm = LodSuffix.Match(n2);
                if (lm.Success && lm.Index > 0) { r2.Lod = int.Parse(lm.Groups[1].Value); n2 = n2.Substring(0, lm.Index); }
                if (MatchPart(n2, aliasCsv, r2)) return r2;
            }
            return new NameParseResult();
        }

        static bool MatchPart(string n, IDictionary<BodyPart, string> aliasCsv, NameParseResult r)
        {
            // longest aliases first so "arms" wins over "arm" and "hands" over "hand"
            var all = new List<KeyValuePair<string, BodyPart>>();
            foreach (var kv in aliasCsv)
                foreach (var a in SplitAliases(kv.Value))
                    all.Add(new KeyValuePair<string, BodyPart>(a, kv.Key));
            if (all.Count == 0) return false;
            all.Sort((x, y) => y.Key.Length.CompareTo(x.Key.Length));

            string alt = string.Join("|", all.Select(a => Regex.Escape(a.Key)));
            var rx = new Regex(@"^(?:(?<piece>.*?)[_\-\. ]+)?(?<part>" + alt + @")(?:[_\-]?(?<var>\d+|[a-z]))?$",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var m = rx.Match(n);
            if (!m.Success) return false;
            string partTok = m.Groups["part"].Value.ToLowerInvariant();
            r.Part = all.First(a => a.Key == partTok).Value;
            r.Variant = m.Groups["var"].Success ? m.Groups["var"].Value.ToLowerInvariant() : "";
            r.Piece = m.Groups["piece"].Success ? m.Groups["piece"].Value : "";
            return true;
        }

        public static string PartWord(BodyPart p)
        {
            switch (p)
            {
                case BodyPart.Upper: return "upper";
                case BodyPart.Lower: return "lower";
                case BodyPart.Head: return "head";
                case BodyPart.Hands: return "hands";
                default: return "";
            }
        }

        // ---------------------------------------------------------------- naming

        public static readonly Dictionary<BodyPart, string> DefaultBundlePatterns = new Dictionary<BodyPart, string>
        {
            { BodyPart.Upper, "clothing/{model}_top{variant}" },
            { BodyPart.Lower, "clothing/{model}_bottom{variant}" },
            { BodyPart.Head,  "heads/{model}_head{variant}" },
            { BodyPart.Hands, "clothing/{model}_hands{variant}" },
        };

        public const string DefaultPrefabPattern = "{model}_{part}{variant}";

        /// <summary>Fills {model} {part} {variant}. Bundle names are lower-cased (Unity does this anyway) and spaces become '_'.</summary>
        public static string Format(string pattern, string model, BodyPart part, string variant, bool isBundleName)
        {
            string s = (pattern ?? "")
                .Replace("{model}", model ?? "")
                .Replace("{part}", PartWord(part))
                .Replace("{variant}", variant ?? "");
            s = s.Trim();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { sb.Append('_'); continue; }
                if (isBundleName)
                {
                    // keep folder separators, drop characters that break paths / JSON
                    if (c == '\\') { sb.Append('/'); continue; }
                    if ("<>:\"|?*".IndexOf(c) >= 0) continue;
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    if ("<>:\"|?*/\\".IndexOf(c) >= 0) continue;
                    sb.Append(c);
                }
            }
            string outS = sb.ToString();
            if (isBundleName)
            {
                while (outS.Contains("//")) outS = outS.Replace("//", "/");
                outS = outS.Trim('/');
                if (outS.EndsWith(".bundle")) outS = outS.Substring(0, outS.Length - ".bundle".Length); // variant adds it
            }
            return outS;
        }

        // ---------------------------------------------------------------- bones

        static Dictionary<string, string> _byName;

        /// <summary>leaf bone name -> path relative to Root_Joint (from the game's own skeleton).</summary>
        public static Dictionary<string, string> BoneMap
        {
            get
            {
                if (_byName == null)
                {
                    _byName = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in EFTSkeletonData.RootJointRelativePaths)
                    {
                        int i = p.LastIndexOf('/');
                        string leaf = i >= 0 ? p.Substring(i + 1) : p;
                        _byName[leaf] = p;
                    }
                }
                return _byName;
            }
        }

        public static string SkeletonPrefix(BodyPart part) => part == BodyPart.Hands ? "" : "Root_Joint/";

        /// <summary>
        /// Maps the mesh's own bone list (in SMR order) to game skeleton paths relative to Root_Joint.
        /// Uses bone NAMES, so the armature's own hierarchy/naming above the bones (e.g. " skeleton",
        /// "Body EFT Armature") does not matter.
        /// </summary>
        public static bool MapBones(IList<string> boneNames, string rootBoneName,
                                    out string[] relPaths, out string rootRel,
                                    List<string> errors, List<string> warnings)
        {
            relPaths = null; rootRel = null;
            if (boneNames == null || boneNames.Count == 0)
            {
                errors.Add("Mesh has no bones (not skinned to an armature).");
                return false;
            }

            var map = BoneMap;
            var result = new string[boneNames.Count];
            var empty = new List<int>();
            var unknown = new List<string>();
            var seen = new HashSet<string>();
            var dups = new List<string>();
            for (int i = 0; i < boneNames.Count; i++)
            {
                string b = boneNames[i];
                if (b == null) { empty.Add(i); continue; }
                string key = b.Trim();
                if (!map.TryGetValue(key, out var rel)) { unknown.Add(b); continue; }
                if (!seen.Add(key)) dups.Add(key);
                result[i] = rel;
            }
            if (empty.Count > 0)
                errors.Add("Bone slot(s) " + string.Join(", ", empty) + " are empty (missing bone Transform).");
            if (unknown.Count > 0)
                errors.Add("Not in the EFT skeleton: " + string.Join(", ", unknown.Distinct().Select(u => "'" + u + "'")) +
                           ". The game throws 'no bone with name ... found' for these and the outfit fails to load.");
            if (dups.Count > 0)
                warnings.Add("Bone listed twice: " + string.Join(", ", dups.Distinct()));

            if (string.IsNullOrEmpty(rootBoneName))
            {
                if (result[0] != null)
                {
                    rootRel = result[0];
                    warnings.Add("Mesh has no Root Bone set; using the first bone (" + boneNames[0] + ").");
                }
            }
            else if (!map.TryGetValue(rootBoneName.Trim(), out rootRel))
            {
                errors.Add("Root bone '" + rootBoneName + "' is not in the EFT skeleton.");
                rootRel = null;
            }

            if (errors.Count > 0) return false;
            relPaths = result;
            return true;
        }

        // ---------------------------------------------------------------- LODs / heat

        /// <summary>Vanilla: LOD0 0.6, last LOD 0.015 (heads 0.035); a single LOD uses the "last" value.</summary>
        public static float[] LodHeights(int count, bool isHead)
        {
            float last = isHead ? 0.035f : 0.015f;
            if (count <= 0) return new float[0];
            if (count == 1) return new[] { last };
            var h = new float[count];
            const float first = 0.6f;
            for (int i = 0; i < count; i++)
            {
                float t = (float)i / (count - 1);
                h[i] = (float)(first * Math.Pow(last / first, t)); // geometric steps, strictly decreasing
            }
            h[count - 1] = last;
            return h;
        }

        /// <summary>Most common HotObject.Temperature (min,max,factor) per part across vanilla bundles.</summary>
        public static float[] DefaultTemperature(BodyPart p)
        {
            switch (p)
            {
                case BodyPart.Upper: return new[] { 0.6f, 1f, 3.2f };   // 76 of 288 top skins
                case BodyPart.Lower: return new[] { 0.1f, 1f, 3.5f };   // 57 pants skins
                case BodyPart.Head: return new[] { 0.1f, 1f, 3.5f };    // 54 head skins
                case BodyPart.Hands: return new[] { 0.81f, 1f, 4.5f };  // 28 hands skins
                default: return new[] { 0.1f, 1f, 3.5f };
            }
        }
    }
}
