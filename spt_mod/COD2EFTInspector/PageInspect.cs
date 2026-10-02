// Inspect page (0.12.0): the shown character's meshes (show / hide / solo) and materials (live shader values, texture
// channel view, save tuning). The material lists are built once per scan / option change (_matsDirty), not per event.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace COD2EFTInspector
{
    public partial class InspectorPlugin
    {
        int _inspSub;
        Vector2 _meshScroll, _matScroll;
        static readonly string[] InspectSubs = { "Meshes", "Materials" };

        void DrawInspectPage()
        {
            DrawTargetPicker();
            int hit = Ui.Segs(null, InspectSubs, _inspSub, new[] { "Show / hide each mesh of the character", "Shader values of the outfit's materials, live" });
            if (hit >= 0) Later(() => _inspSub = hit);
            if (_scan == null)
            {
                Ui.Empty("No character to inspect.", "In raid or the hideout this is you. In the main menu, open the Character or Inventory screen, then press Rescan.");
                return;
            }
            if (_inspSub == 1) DrawMaterialsTab(); else DrawMeshesTab();
        }

        // ------------------------------------------------------------------ meshes

        void DrawMeshesTab()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Show all", "Show every mesh you hid"), Ui.Button)) ShowAll();
            if (GUILayout.Button(new GUIContent("Hide gear", "Hide everything that is not a body part"), Ui.Button)) HideGear();
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{_hidden.Count} hidden", Ui.Small, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            Ui.Help("Tick = shown.  solo = only that mesh.  [inactive] = switched off by the game,  [shadow] = first-person body.");
            _meshScroll = GUILayout.BeginScrollView(_meshScroll, GUILayout.ExpandHeight(true));
            foreach (var g in _scan.Groups)
            {
                var live = g.Entries.Where(e => e.R != null).ToList();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(g.Open ? "▼" : "►", Ui.Icon, GUILayout.Width(20)))
                {
                    var grp = g;
                    Later(() => { grp.Open = !grp.Open; if (!_flipped.Remove(grp.Name)) _flipped.Add(grp.Name); });
                }
                bool allShown = live.All(e => !IsHidden(e.R));
                bool nv = Ui.Check(allShown, g.Name, "Show / hide the whole group");
                if (nv != allShown) foreach (var e in live) SetHidden(e.R, !nv);
                GUILayout.FlexibleSpace();
                GUILayout.Label(live.Count.ToString(), Ui.Small, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
                if (!g.Open) continue;
                foreach (var e in live)
                {
                    bool shown = !IsHidden(e.R);
                    string flags = (e.R.gameObject.activeInHierarchy ? "" : "  [inactive]") + (e.R.enabled ? "" : "  [off]") +
                                   (e.R.shadowCastingMode == ShadowCastingMode.ShadowsOnly ? "  [shadow]" : "");
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(22);
                    bool n2 = Ui.Check(shown, e.Path + flags);
                    if (n2 != shown) SetHidden(e.R, !n2);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("solo", "Show only this mesh (Show all brings the rest back)"), Ui.Link, GUILayout.Width(34))) Solo(e.R);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ materials

        bool _matsDirty = true;
        List<Renderer> _mRends = new List<Renderer>();
        List<Material> _mMats = new List<Material>();
        List<string> _mSlots = new List<string>();
        string[] _mViewNames = { "Normal" };
        readonly Dictionary<Material, string> _mUsedBy = new Dictionary<Material, string>();
        Material _propsFor;
        List<MaterialTuner.Prop> _props = new List<MaterialTuner.Prop>();

        void EnsureMaterials()
        {
            if (!_matsDirty) return;
            _matsDirty = false;
            _mRends = TunerRenderers();
            _mMats = _mRends.SelectMany(RealMaterials).Where(m => m != null).Distinct().ToList();
            _mSlots = MaterialTuner.TextureSlots(_mMats);
            _mViewNames = new[] { "Normal" }.Concat(_mSlots.Select(MaterialTuner.Pretty)).ToArray();
            _mUsedBy.Clear();
            foreach (var m in _mMats)
                _mUsedBy[m] = string.Join(", ", _mRends.Where(r => RealMaterials(r).Contains(m)).Select(r => r.name).Distinct().Take(4).ToArray());
            if (!_mMats.Contains(_selMat)) _selMat = _mMats.FirstOrDefault();
            _propsFor = null;
        }

        string UsedByCached(Material m) { string s; return m != null && _mUsedBy.TryGetValue(m, out s) ? s : ""; }

        void DrawMaterialsTab()
        {
            if (Event.current.type == EventType.Layout) EnsureMaterials();
            int sel = _tuner.View == null ? 0 : _mSlots.IndexOf(_tuner.View) + 1;
            int v = Ui.Segs("View", _mViewNames, sel, null);
            if (v == 0) _tuner.ClearView();
            else if (v > 0) _tuner.SetView(_mSlots[v - 1], _mRends);
            GUILayout.BeginHorizontal();
            bool gear = Ui.Check(_matGear, "Gear too", "Also list / view the materials of gear, not only body parts");
            if (gear != _matGear) { _matGear = gear; _matsDirty = true; }
            _matSameShader = Ui.Check(_matSameShader, "Edit all with this shader", "A change goes to every listed material using the same shader");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = _tuner.ChangedCount > 0;
            if (GUILayout.Button(new GUIContent($"Save tuning ({_tuner.ChangedCount})", "Writes the changed values (new and was) to a .txt for the converter"), Ui.Primary)) SaveTuning();
            if (GUILayout.Button(new GUIContent("Reset all materials", "Every material back to the bundle's values"), Ui.Button)) _tuner.ResetAll();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            _matScroll = GUILayout.BeginScrollView(_matScroll, GUILayout.ExpandHeight(true));
            foreach (var m in _mMats)
            {
                if (m == null) continue;
                bool open = m == _selMat;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent((open ? "▼  " : "►  ") + m.name.Replace(" (Instance)", ""), "Used by " + UsedByCached(m)), open ? Ui.SegOn : Ui.Seg, GUILayout.ExpandWidth(true)))
                { var pick = open ? null : m; Later(() => _selMat = pick); }
                if (_tuner.Changed(m)) Ui.Pill("CHANGED", Ui.Warn);
                GUILayout.EndHorizontal();
                if (open) DrawMaterialEditor(m);
            }
            GUILayout.EndScrollView();
        }

        void DrawMaterialEditor(Material m)
        {
            if (Event.current.type == EventType.Layout && _propsFor != m) { _propsFor = m; _props = MaterialTuner.Props(m); }
            GUILayout.BeginVertical(Ui.CardBox);
            GUILayout.Label($"shader <b>{m.shader?.name}</b>  ·  queue {m.renderQueue}  ·  used by {UsedByCached(m)}", Ui.Small);
            var targets = _matSameShader ? _mMats.Where(x => x != null && x.shader == m.shader).ToList() : new List<Material> { m };
            foreach (var p in _props)
            {
                if (!m.HasProperty(p.Name)) continue;
                switch (p.Type)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float cur = m.GetFloat(p.Name);
                        string key = m.GetInstanceID() + p.Name;
                        Vector2 rg;
                        if (!_ranges.TryGetValue(key, out rg))
                        {
                            var o = _tuner.Original(m, p.Name) as float? ?? cur;
                            rg = p.Type == ShaderPropertyType.Range ? new Vector2(p.Min, p.Max) : new Vector2(Mathf.Min(0f, o * 2f), Mathf.Max(1f, o * 2f));
                            _ranges[key] = rg;
                        }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(p.Name, _tuner.Changed(m, p.Name) ? Ui.Bold : Ui.Label, GUILayout.Width(140));
                        float nv = GUILayout.HorizontalSlider(cur, rg.x, rg.y, GUILayout.MinWidth(70));
                        GUILayout.Label(nv.ToString("0.###"), Ui.Value, GUILayout.Width(44));
                        PropReset(m, p.Name, targets);
                        GUILayout.EndHorizontal();
                        if (!Mathf.Approximately(nv, cur)) foreach (var t in targets) if (t.HasProperty(p.Name)) _tuner.SetFloat(t, p.Name, nv);
                        break;
                    }
                    case ShaderPropertyType.Color:
                    {
                        var c = m.GetColor(p.Name);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(p.Name, _tuner.Changed(m, p.Name) ? Ui.Bold : Ui.Label, GUILayout.Width(140));
                        var r = GUILayoutUtility.GetRect(22, 16, GUILayout.Width(22));
                        GUI.DrawTexture(r, Ui.Tex1(new Color(c.r, c.g, c.b, 1f)));
                        float max = Mathf.Max(1f, c.maxColorComponent);
                        var n = new Color(GUILayout.HorizontalSlider(c.r, 0f, max, GUILayout.MinWidth(30)), GUILayout.HorizontalSlider(c.g, 0f, max, GUILayout.MinWidth(30)),
                                          GUILayout.HorizontalSlider(c.b, 0f, max, GUILayout.MinWidth(30)), GUILayout.HorizontalSlider(c.a, 0f, 1f, GUILayout.MinWidth(24)));
                        PropReset(m, p.Name, targets);
                        GUILayout.EndHorizontal();
                        if (n != c) foreach (var t in targets) if (t.HasProperty(p.Name)) _tuner.SetColor(t, p.Name, n);
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        var vv = m.GetVector(p.Name);
                        GUILayout.Label($"{p.Name}   {vv.x:0.###}, {vv.y:0.###}, {vv.z:0.###}, {vv.w:0.###}", Ui.Small);
                        break;
                    }
                    case ShaderPropertyType.Texture:
                    {
                        var tex = m.GetTexture(p.Name);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"{p.Name}   " + (tex != null ? $"<b>{tex.name}</b>  {tex.width}x{tex.height}" : "<color=#949ca6>(empty)</color>"), Ui.Small);
                        GUILayout.FlexibleSpace();
                        if (tex != null && GUILayout.Button(new GUIContent("view", "Show this texture slot on the whole outfit"), Ui.Link, GUILayout.Width(36))) _tuner.SetView(p.Name, _mRends);
                        GUILayout.EndHorizontal();
                        break;
                    }
                }
            }
            GUI.enabled = _tuner.Changed(m);
            if (GUILayout.Button(new GUIContent("Reset this material", "Back to the bundle's values"), Ui.Button, GUILayout.ExpandWidth(false))) _tuner.Reset(m);
            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        void PropReset(Material m, string prop, List<Material> targets)
        {
            GUI.enabled = _tuner.Changed(m, prop);
            if (GUILayout.Button(new GUIContent("R", "Back to " + (_tuner.Original(m, prop) ?? "?")), Ui.Icon, GUILayout.Width(20)))
                foreach (var t in targets) _tuner.ResetProp(t, prop);
            GUI.enabled = true;
        }
    }
}
