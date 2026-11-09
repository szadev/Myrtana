using System;
using System.Collections.Generic;
using G3.Player;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The menu's options as player preferences (the port's options file, next to the languages of
    /// <see cref="LanguagePreferences"/>): <see cref="PlayerOptions"/> (volumes, camera mode, GUI size, Video and Performance
    /// pages) and the Controls page's <see cref="ControlOptions"/> (mouse rows, Standard/Custom and the key bindings, in
    /// G3's [Options.Controls] names and the Key1/Key2 Type:Offset form). Every change is stored immediately.
    /// </summary>
    public static class OptionsPreferences
    {
        const string VolumeKey = "Myrtana.Volume.", FirstPersonKey = "Myrtana.FirstPerson", UiScaleKey = "Myrtana.GuiSize";
        const string SubtitlesKey = "Myrtana.Audio.Subtitle", SpeakerKey = "Myrtana.Audio.SpeakerMode"; // was Myrtana.UiScale (100 % = original pixels), not carried over
        const string VideoKey = "Myrtana.Video.", PerfKey = "Myrtana.Details.", ControlsKey = "Myrtana.Controls.";

        /// <summary>
        /// The stored Video and Performance pages and the port's upscaling in one line (fix-172 perf report; the menu stores
        /// every change at once, so this is what is applied).
        /// </summary>
        public static string DescribeDisplay()
        {
            var sb = new System.Text.StringBuilder();
            int w = PlayerPrefs.GetInt(VideoKey + "Width", 0), h = PlayerPrefs.GetInt(VideoKey + "Height", 0);
            sb.Append("resolution ").Append(w > 0 && h > 0 ? w + "x" + h : "native")
              .Append(", vsync ").Append(PlayerPrefs.GetInt(VideoKey + "VSync", VideoOptions.VSyncDefault ? 1 : 0) != 0 ? "on" : "off")
              .Append(", details ").Append(PlayerPrefs.GetString(PerfKey + "Preset", nameof(DetailPreset.High)));
            foreach (var d in PerformanceOptions.Settings)
                if (PlayerPrefs.HasKey(PerfKey + d)) sb.Append(' ').Append(d).Append('=').Append(PlayerPrefs.GetInt(PerfKey + d));
            sb.Append(", upscaling ").Append(PlayerPrefs.GetString("Myrtana.Port.Upscaling", nameof(UpscalingMode.Off)))
              .Append(", fov ").Append(PlayerPrefs.GetInt("Myrtana.Port.FovOffset", 0).ToString("+0;-0;0"));
            return sb.ToString();
        }

        public static PlayerOptions Load()
        {
            var options = new PlayerOptions();
            foreach (var channel in PlayerOptions.Channels)
                options.SetVolume(channel, PlayerPrefs.GetInt(VolumeKey + channel, 100));
            options.FirstPerson = PlayerPrefs.GetInt(FirstPersonKey, 0) != 0;
            options.UiScale = PlayerPrefs.GetInt(UiScaleKey, PlayerOptions.UiScaleDefault);
            options.Subtitles = PlayerPrefs.GetInt(SubtitlesKey, 1) != 0;
            options.SpeakerMode = PlayerPrefs.GetInt(SpeakerKey, 0);

            var video = options.Video;
            video.SetResolution(PlayerPrefs.GetInt(VideoKey + "Width", 0), PlayerPrefs.GetInt(VideoKey + "Height", 0),
                                PlayerPrefs.GetInt(VideoKey + "RefreshRate", 0));
            video.VSync = PlayerPrefs.GetInt(VideoKey + "VSync", VideoOptions.VSyncDefault ? 1 : 0) != 0;
            foreach (var color in VideoOptions.Colors) video.SetSlider(color, PlayerPrefs.GetInt(VideoKey + color, 0));

            if (Enum.TryParse(PlayerPrefs.GetString(PerfKey + "Preset", nameof(DetailPreset.High)), out DetailPreset preset) &&
                Enum.IsDefined(typeof(DetailPreset), preset))
            {
                var values = new Dictionary<DetailSetting, int>();
                foreach (var s in PerformanceOptions.Settings)
                    if (PlayerPrefs.HasKey(PerfKey + s)) values[s] = PlayerPrefs.GetInt(PerfKey + s);
                options.Performance.Load(preset, values);
            }
            PortOptionsPreferences.Load(options.Port);
            options.Changed += () => Save(options);
            return options;
        }

        static void Save(PlayerOptions options)
        {
            foreach (var channel in PlayerOptions.Channels) PlayerPrefs.SetInt(VolumeKey + channel, options.Volume(channel));
            PlayerPrefs.SetInt(FirstPersonKey, options.FirstPerson ? 1 : 0);
            PlayerPrefs.SetInt(UiScaleKey, options.UiScale);
            PlayerPrefs.SetInt(SubtitlesKey, options.Subtitles ? 1 : 0);
            PlayerPrefs.SetInt(SpeakerKey, options.SpeakerMode);
            var video = options.Video;
            PlayerPrefs.SetInt(VideoKey + "Width", video.Width);
            PlayerPrefs.SetInt(VideoKey + "Height", video.Height);
            PlayerPrefs.SetInt(VideoKey + "RefreshRate", video.RefreshRate);
            PlayerPrefs.SetInt(VideoKey + "VSync", video.VSync ? 1 : 0);
            foreach (var color in VideoOptions.Colors) PlayerPrefs.SetInt(VideoKey + color, video.Slider(color));
            PlayerPrefs.SetString(PerfKey + "Preset", options.Performance.Preset.ToString());
            foreach (var s in PerformanceOptions.Settings) PlayerPrefs.SetInt(PerfKey + s, options.Performance.Value(s));
            PortOptionsPreferences.Save(options.Port);
            PlayerPrefs.Save();
        }

        /// <summary>The Controls page's options (stored on every change, also a rebinding).</summary>
        public static ControlOptions LoadControls()
        {
            var c = new ControlOptions
            {
                SensitivityX = PlayerPrefs.GetFloat(ControlsKey + "MouseSensitivityX", 0.5f),
                SensitivityY = PlayerPrefs.GetFloat(ControlsKey + "MouseSensitivityY", 0.5f),
                SmoothingX = PlayerPrefs.GetFloat(ControlsKey + "MouseSmoothingX", 0.5f),
                SmoothingY = PlayerPrefs.GetFloat(ControlsKey + "MouseSmoothingY", 0.5f),
                InvertX = PlayerPrefs.GetInt(ControlsKey + "MouseInvertX", 0) != 0,
                InvertY = PlayerPrefs.GetInt(ControlsKey + "MouseInvertY", 0) != 0,
                LookaroundInverse = PlayerPrefs.GetInt(ControlsKey + "CamLookaroundInverse", 0) != 0,
                Layout = PlayerPrefs.GetString(ControlsKey + "Controls", ControlOptions.Standard),
            };
            // Custom keys only: "Standard" always means the default table (a build with new default keys gets them).
            if (c.Layout == ControlOptions.Custom) c.Keys.CopyFrom(KeyMap.Parse(PlayerPrefs.GetString(ControlsKey + "Keys", "")));
            LoadPad(c.Pad);
            c.Changed += () => SaveControls(c);
            return c;
        }

        static void SaveControls(ControlOptions c)
        {
            PlayerPrefs.SetFloat(ControlsKey + "MouseSensitivityX", c.SensitivityX);
            PlayerPrefs.SetFloat(ControlsKey + "MouseSensitivityY", c.SensitivityY);
            PlayerPrefs.SetFloat(ControlsKey + "MouseSmoothingX", c.SmoothingX);
            PlayerPrefs.SetFloat(ControlsKey + "MouseSmoothingY", c.SmoothingY);
            PlayerPrefs.SetInt(ControlsKey + "MouseInvertX", c.InvertX ? 1 : 0);
            PlayerPrefs.SetInt(ControlsKey + "MouseInvertY", c.InvertY ? 1 : 0);
            PlayerPrefs.SetInt(ControlsKey + "CamLookaroundInverse", c.LookaroundInverse ? 1 : 0);
            PlayerPrefs.SetString(ControlsKey + "Controls", c.Layout);
            PlayerPrefs.SetString(ControlsKey + "Keys", c.Keys.Serialize());
            SavePad(c.Pad);
            PlayerPrefs.Save();
        }

        // gamepad-1 (Docs/gamepad.md §5): the gamepad options, "Myrtana.Controls.Pad*"; the buttons only when Custom
        static void LoadPad(PadOptions p)
        {
            p.SensitivityX = PlayerPrefs.GetFloat(ControlsKey + "PadSensitivityX", 0.5f);
            p.SensitivityY = PlayerPrefs.GetFloat(ControlsKey + "PadSensitivityY", 0.5f);
            p.InvertX = PlayerPrefs.GetInt(ControlsKey + "PadInvertX", 0) != 0;
            p.InvertY = PlayerPrefs.GetInt(ControlsKey + "PadInvertY", 0) != 0;
            p.DeadZone = PlayerPrefs.GetFloat(ControlsKey + "PadDeadZone", PadOptions.DefaultDeadZone);
            p.TriggerThreshold = PlayerPrefs.GetFloat(ControlsKey + "PadTrigger", PadOptions.DefaultTrigger);
            p.Vibration = PlayerPrefs.GetInt(ControlsKey + "PadVibration", 1) != 0;
            p.Layout = PlayerPrefs.GetString(ControlsKey + "PadLayout", ControlOptions.Standard);
            if (p.Layout == ControlOptions.Custom) p.Buttons.CopyFrom(PadMap.Parse(PlayerPrefs.GetString(ControlsKey + "PadKeys", "")));
        }

        static void SavePad(PadOptions p)
        {
            PlayerPrefs.SetFloat(ControlsKey + "PadSensitivityX", p.SensitivityX);
            PlayerPrefs.SetFloat(ControlsKey + "PadSensitivityY", p.SensitivityY);
            PlayerPrefs.SetInt(ControlsKey + "PadInvertX", p.InvertX ? 1 : 0);
            PlayerPrefs.SetInt(ControlsKey + "PadInvertY", p.InvertY ? 1 : 0);
            PlayerPrefs.SetFloat(ControlsKey + "PadDeadZone", p.DeadZone);
            PlayerPrefs.SetFloat(ControlsKey + "PadTrigger", p.TriggerThreshold);
            PlayerPrefs.SetInt(ControlsKey + "PadVibration", p.Vibration ? 1 : 0);
            PlayerPrefs.SetString(ControlsKey + "PadLayout", p.Layout);
            PlayerPrefs.SetString(ControlsKey + "PadKeys", p.Buttons.Serialize());
        }
    }
}
