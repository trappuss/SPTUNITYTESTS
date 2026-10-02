// Photo page (0.12.0): photo mode around your own character (raid / hideout), in four short sub-pages instead of one
// long scroll: Camera, Character (pose, play mode, time), Scene (lights, background), Shots (captures, presets).
// Photo mode itself is switched on / off from the bottom bar (or here when it is off).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace COD2EFTInspector
{
    public partial class InspectorPlugin
    {
        int _photoSub;
        Vector2 _photoScroll;
        static readonly string[] PhotoSubs = { "Camera", "Character", "Scene", "Shots" };

        void DrawPhotoPage()
        {
            var ph = _photo;
            if (!ph.Active)
            {
                GUILayout.BeginVertical(Ui.CardBox);
                GUILayout.Label("Photo mode", Ui.Bold);
                GUILayout.Label("An orbit camera and studio lights around your own character, for screenshots and turntables. " +
                                "The character takes no input while it is on (double-click outside the panel for play mode).", Ui.Label);
                Ui.Help(_inMenu ? "Works in the hideout or a raid. In the main menu, use the game's own character preview and the Screenshot button."
                                : "Same light every time: hideout, studio lights on, background isolated (Scene).");
                GUI.enabled = !_inMenu;
                if (GUILayout.Button(new GUIContent("Start photo mode", "Orbit camera around your character"), Ui.Primary, GUILayout.Height(30))) Later(TogglePhoto);
                GUI.enabled = true;
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                return;
            }
            Ui.Help("Mouse outside the panel:  right drag = orbit  ·  left drag = turn / aim  ·  wheel = zoom" +
                    (_playOnDoubleClick.Value ? "  ·  double-click = play mode" : ""));
            int hit = Ui.Segs(null, PhotoSubs, _photoSub);
            if (hit >= 0) Later(() => _photoSub = hit);
            _photoScroll = GUILayout.BeginScrollView(_photoScroll, GUILayout.ExpandHeight(true));
            switch (_photoSub)
            {
                case 1: DrawPhotoCharacter(ph); break;
                case 2: DrawPhotoScene(ph); break;
                case 3: DrawPhotoShots(); break;
                default: DrawPhotoCamera(ph); break;
            }
            GUILayout.EndScrollView();
        }

        void DrawPhotoCamera(PhotoMode ph)
        {
            if (Ui.Header("Camera", "Camera back to the default angle, distance and lens")) ph.ResetCamera();
            int sel = Array.FindIndex(PhotoMode.Angles, a => Mathf.Abs(Mathf.DeltaAngle(ph.Yaw, ph.YawFor(a.Yaw))) < 0.5f);
            int a2 = Ui.Segs("Angle", PhotoMode.Angles.Select(x => x.Name).ToArray(), sel);
            if (a2 >= 0) ph.Yaw = ph.YawFor(PhotoMode.Angles[a2].Yaw);
            int fsel = Array.FindIndex(PhotoMode.Framings, f => Mathf.Abs(ph.Height - f.Height) < 0.01f && Mathf.Abs(ph.Distance - f.Distance) < 0.01f);
            int f2 = Ui.Segs("Framing", PhotoMode.Framings.Select(x => x.Name).ToArray(), fsel);
            if (f2 >= 0) { ph.Height = PhotoMode.Framings[f2].Height; ph.Distance = PhotoMode.Framings[f2].Distance; }
            ph.Yaw = Ui.Slider("Orbit", ph.Yaw, 0f, 360f, ph.YawFor(0f), "0", "Camera angle around the character (0 = front)");
            ph.Pitch = Ui.Slider("Tilt", ph.Pitch, -60f, 80f, PhotoMode.DefPitch, "0", "Camera height angle");
            ph.Distance = Ui.Slider("Distance", ph.Distance, 0.3f, 12f, PhotoMode.DefDistance, "0.00", "Metres from the character (mouse wheel)");
            ph.Height = Ui.Slider("Aim height", ph.Height, 0f, 2.2f, PhotoMode.DefHeight, "0.00", "The point the camera looks at, metres above the feet");
            ph.Fov = Ui.Slider("Field of view", ph.Fov, 10f, 90f, PhotoMode.DefFov, "0", "Lower = more telephoto, less distortion");
            ph.Ortho = Ui.Check(ph.Ortho, "Orthographic", "No perspective (for reference sheets); the view size follows distance and field of view");
        }

        void DrawPhotoCharacter(PhotoMode ph)
        {
            if (Ui.Header("Pose", "Standing, facing the camera, aim level")) { ph.ResetCharacter(); if (_pose != "Stand") SetPose("Stand"); }
            int p = Ui.Segs(null, Poses.Names, Array.IndexOf(Poses.Names, _pose));
            if (p >= 0 && _pose != Poses.Names[p]) SetPose(Poses.Names[p]);
            ph.CharYaw = Ui.Slider("Turn", ph.CharYaw, -180f, 180f, 0f, "0", "Turns the character (or left drag sideways)");
            ph.CharPitch = Ui.Slider("Aim up / down", ph.CharPitch, -60f, 60f, 0f, "0", "Where the character looks / aims (or left drag up / down)");

            Ui.Header("Play mode");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Start play mode", "Full control of the character (walk, shoot, reload, inspect) with this camera; Esc to leave"), Ui.Button, GUILayout.ExpandWidth(false))) SetPlay(true);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            _playCamTurns.Value = Ui.Check(_playCamTurns.Value, "Camera turns with the character", "Off: the camera keeps its world angle while you walk / turn");
            _playOnDoubleClick.Value = Ui.Check(_playOnDoubleClick.Value, "Double-click outside the panel starts it");

            if (Ui.Header("Time", "Normal speed")) ResetTime();
            _speed = Ui.Slider("Speed", _speed, 0.05f, 1f, 1f, "0.00", "Game speed while photo mode is on");
            _frozen = Ui.Check(_frozen, $"Freeze ({_freezeKey.Value})", "Stops the game; the camera still moves");
            Ui.Help($"Also in play mode: {_freezeKey.Value} = freeze,  {_slowKey.Value} = 1x / 0.5x / 0.25x / 0.1x.");
        }

        void DrawPhotoScene(PhotoMode ph)
        {
            if (Ui.Header("Studio lights", "Lights on, following the camera, strength 1")) { ph.ResetLights(); _lightStrength.Value = 1f; }
            GUILayout.BeginHorizontal();
            bool li = Ui.Check(ph.Lights, "On", "Key, fill and rim spot lights");
            if (li != ph.Lights) ph.SetLights(li);
            ph.LightsFollowCamera = Ui.Check(ph.LightsFollowCamera, "Follow the camera", "Off: the lights stay fixed to the character's front");
            GUILayout.EndHorizontal();
            _lightStrength.Value = Ui.Slider("Strength", _lightStrength.Value, 0f, 4f, 1f, "0.00");

            if (Ui.Header("Background", "The world back, default colour")) Later(() => { ph.ResetBackground(); _transparent = false; _bgColor.Value = "#00B140"; });
            bool iso = Ui.Check(ph.Isolate, "Isolate character", "Hide the world: only you and what you wear / hold, on a solid colour");
            if (iso != ph.Isolate) Later(() => ph.Isolate = iso);
            if (!ph.Isolate) { Ui.Help("Isolate puts the character on a solid colour (and allows transparent PNGs)."); return; }
            Color bc;
            if (!_colourLoaded && ColorUtility.TryParseHtmlString(_bgColor.Value, out bc)) { ph.BgColor = bc; _colourLoaded = true; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour", Ui.Small, GUILayout.Width(78));
            var r = GUILayoutUtility.GetRect(30, 20, GUILayout.Width(30));
            GUI.DrawTexture(r, Ui.Tex1(new Color(ph.BgColor.r, ph.BgColor.g, ph.BgColor.b, 1f)));
            foreach (var c in new[] { ("Green", PhotoMode.DefaultBg), ("Blue", new Color(0f, 0.28f, 0.73f)), ("White", Color.white), ("Grey", new Color(0.5f, 0.5f, 0.5f)), ("Black", Color.black) })
                if (GUILayout.Button(c.Item1, ph.BgColor == c.Item2 ? Ui.SegOn : Ui.Seg)) ph.BgColor = c.Item2;
            GUILayout.EndHorizontal();
            float cr = Ui.Slider("Red", ph.BgColor.r, 0f, 1f, null, "0.00"), cg = Ui.Slider("Green", ph.BgColor.g, 0f, 1f, null, "0.00"), cb = Ui.Slider("Blue", ph.BgColor.b, 0f, 1f, null, "0.00");
            ph.BgColor = new Color(cr, cg, cb);
            string hex = "#" + ColorUtility.ToHtmlStringRGB(ph.BgColor);
            if (hex != _bgColor.Value) _bgColor.Value = hex;
            ph.NoFog = Ui.Check(ph.NoFog, "No fog / sky haze", "Switches off camera effects that tint the background");
            ph.NoPost = Ui.Check(ph.NoPost, "No post effects", "Exact colours; the character looks less 'in game'");
            ph.WorldLightsOff = Ui.Check(ph.WorldLightsOff, "Studio lights only", "Switches the world's lights off");
            _transparent = Ui.Check(_transparent, "Transparent PNG", "Screenshots get an alpha channel (2 captures: black + grey; max 2x supersize)");
        }

        void DrawPhotoShots()
        {
            Ui.Header("Captures");
            int hit = Ui.Segs("Supersize", new[] { "1x", "2x", "3x", "4x" }, _supersize.Value - 1, new[] { "Screen size", "2x the screen", "3x", "4x (big files)" });
            if (hit >= 0) _supersize.Value = hit + 1;
            bool idle = !_capturing && !Wearer.Busy;
            GUI.enabled = idle;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Turntable", "4 screenshots: front, left, back, right"), Ui.Button)) StartCoroutine(Turntable());
            if (GUILayout.Button(new GUIContent("Pose turntables", "4 angles in each of the 5 poses (clipping check)"), Ui.Button)) StartCoroutine(PoseTurntables());
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            Ui.Header("A/B comparison");
            var newest = _sets.FirstOrDefault();
            if (newest == null) Ui.Help("Needs a mod outfit (Outfits page).");
            else
            {
                var names = _sets.Where(x => x.Source == newest.Source).Select(x => x.Name).ToList();
                GUILayout.Label(string.Join("  <color=#949ca6>vs</color>  ", names.Select(n => "<b>" + n + "</b>")) + "  <color=#949ca6>vs</color>  your own outfit", Ui.Label);
                Ui.Help($"From {newest.Source}: a turntable of each, same camera and lights, then one comparison sheet PNG.");
                GUI.enabled = idle;
                if (GUILayout.Button(new GUIContent("Run A/B turntables", "A turntable of every outfit of the newest mod, then of your own outfit"), Ui.Primary)) StartCoroutine(CompareBatch());
                GUI.enabled = true;
            }
            Ui.Help("Files: " + _outDir);
            DrawPresets();
        }

        void DrawPresets()
        {
            Ui.Header("Presets");
            Ui.Help("The whole setup: camera, character, pose, lights, background, speed.");
            GUILayout.BeginHorizontal();
            _presetName = GUILayout.TextField(_presetName ?? "", Ui.Field, GUILayout.ExpandWidth(true));
            GUI.enabled = !string.IsNullOrEmpty(_presetName?.Trim());
            if (GUILayout.Button(new GUIContent("Save", "Save the current setup under this name (same name = overwrite)"), Ui.Primary, GUILayout.Width(56)))
                Later(() =>
                {
                    string n = _presetName.Trim().Replace("|", "/");
                    Presets().RemoveAll(kv => kv.Key == n);
                    _presets.Add(new KeyValuePair<string, string>(n, PresetString()));
                    SavePresets();
                    _status = "Preset saved: " + n;
                });
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            foreach (var kv in Presets().ToList())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(kv.Key, Ui.Row, GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("Load", "Apply this setup"), Ui.Button, GUILayout.Width(52))) { ApplyPreset(kv.Value); _presetName = kv.Key; _status = "Preset loaded: " + kv.Key; }
                if (GUILayout.Button(new GUIContent("×", "Delete this preset"), Ui.Icon, GUILayout.Width(22))) { var gone = kv; Later(() => { _presets.Remove(gone); SavePresets(); }); }
                GUILayout.EndHorizontal();
            }
            if (Presets().Count == 0) Ui.Help("No presets yet: set up a shot, type a name, Save.");
        }
    }
}
