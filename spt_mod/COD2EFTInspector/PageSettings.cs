// Settings page (0.12.0): every option of the plugin in one place (the same values as F12 > COD2EFT Inspector), the
// hotkeys, and the diagnostics that used to sit between the everyday buttons.
using System;
using System.Linq;
using UnityEngine;

namespace COD2EFTInspector
{
    public partial class InspectorPlugin
    {
        Vector2 _setScroll;

        void DrawSettingsPage()
        {
            _setScroll = GUILayout.BeginScrollView(_setScroll, GUILayout.ExpandHeight(true));

            Ui.Header("Panel");
            float sc = Ui.Slider("Size", _scale.Value, 0.75f, 2.5f, 1f, "0.00", "Text and panel size");
            if (!Mathf.Approximately(sc, _scale.Value)) _scale.Value = Mathf.Round(sc * 20f) / 20f;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Dock right", "Put the panel back at the right edge of the screen"), Ui.Button)) DockRight();
            if (GUILayout.Button(new GUIContent("Default size", "470 x 660"), Ui.Button))
            { _win.width = 470f; _win.height = 660f; _winW.Value = 470f; _winH.Value = 660f; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            _blockInput.Value = Ui.Check(_blockInput.Value, "Character takes no input while the panel is open", "No look, aim, fire or walk; given back on close (raid / hideout)");
            _unlockCursor.Value = Ui.Check(_unlockCursor.Value, "Free the mouse while the panel is open");
            _escSteps.Value = Ui.Check(_escSteps.Value, "Esc steps back", "Play mode -> photo mode with the panel -> panel closed -> photo mode off");

            Ui.Header("Outfits");
            _topsBringHands.Value = Ui.Check(_topsBringHands.Value, "Tops bring their first-person hands", "The suite's pairing; off: hands only change when you wear hands");
            _autoWear.Value = Ui.Check(_autoWear.Value, "Auto-wear the newest mod outfit in the hideout", "Try-on, not saved");
            bool others = Ui.Check(_others.Value, "List other players / bots too", "Raid: inspect other characters (character picker)");
            if (others != _others.Value) Later(() => { _others.Value = others; Refresh(true); });
            var cat = Cat;
            Ui.Help("Server folder: " + (cat?.ServerDir ?? "not found") + (string.IsNullOrWhiteSpace(_serverDir.Value) ? "  (found automatically; set it in F12 if wrong)" : "  (from F12)"));

            Ui.Header("Photo mode and screenshots");
            _invertAim.Value = Ui.Check(_invertAim.Value, "Invert aim drag (left drag up / down)");
            int ss = Ui.Segs("Supersize", new[] { "1x", "2x", "3x", "4x" }, _supersize.Value - 1, new[] { "Screen size", "2x the screen", "3x", "4x (big files)" });
            if (ss >= 0) _supersize.Value = ss + 1;
            _hideHud.Value = Ui.Check(_hideHud.Value, "Hide the game's HUD in screenshots", "Raid / hideout only; in the menu the character preview is itself UI");
            Ui.Help("Screenshots: " + _outDir);

            Ui.Header("Hotkeys");
            Ui.Help($"Panel {_panelKey.Value}   ·   Screenshot {_shotKey.Value}   ·   Freeze {_freezeKey.Value}   ·   Slow motion {_slowKey.Value}   ·   Esc steps back");
            Ui.Help("Change them in F12 (Configuration Manager) > COD2EFT Inspector > 1. Hotkeys.");

            Ui.Header("Diagnostics");
            Ui.Help("For reports to Claude: written to the screenshot folder / the BepInEx log, which SEND_RESULTS_TO_CLAUDE.bat sends.");
            GUILayout.BeginHorizontal();
            GUI.enabled = _scan != null;
            if (GUILayout.Button(new GUIContent("Material report", "renderer -> material -> shader / textures to a .txt, with checks"), Ui.Button)) MaterialReport();
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Log all bodies", "Every character body (owner, what it shows) to the BepInEx log"), Ui.Button)) { BodyScan.LogBodies(null); _status = "Every PlayerBody written to the BepInEx log"; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Write catalog to file", "A .txt of every outfit entry with notes"), Ui.Button)) WriteCatalog(GetCatalog());
            if (GUILayout.Button(new GUIContent("Reload catalog", "Read the server's outfit files again (after installing a mod)"), Ui.Button))
                Later(() => { GetCatalog(true); _status = $"Catalog reloaded: {Cat.Items.Count} entries, {Cat.ModSets().Count} mod outfit(s)"; });
            GUILayout.EndHorizontal();
            if (cat != null) foreach (var n in cat.Notes.Where(n => n.Contains("not found") || n.Contains("failed") || n.Contains("not readable")).Take(3)) Ui.Help(n);

            GUILayout.EndScrollView();
        }
    }
}
