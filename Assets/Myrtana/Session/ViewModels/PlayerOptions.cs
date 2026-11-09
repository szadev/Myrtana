using System;
using System.Globalization;

namespace Myrtana.Session
{
    /// <summary>The volume sliders of G3's audio options (<c>IDD_MENU_OPTIONS_AUDIO_2</c>).</summary>
    public enum VolumeChannel { Master, Voice, Music, Effects, Ambient }

    /// <summary>
    /// Player options of the menu that are not languages (those live in <see cref="Core.LanguageSettings"/>) or controls
    /// (G3.Player.ControlOptions): the five volumes in percent, the camera mode, the GUI size, the Video page
    /// (<see cref="VideoOptions"/>) and the Performance page (<see cref="PerformanceOptions"/>). Kept outside save games; the host stores them on
    /// <see cref="Changed"/> and applies them to the audio, the camera and the GUI panels.
    /// </summary>
    public sealed class PlayerOptions
    {
        public const int VolumeStep = 5;
        /// <summary>GUI size in percent of the original's pixel size (100 = G3's look at any resolution).</summary>
        /// <summary>GUI size: 100 % = the 1024×768 GUI fitted to the screen height (Docs/ui-skin.md); 140 % is the most
        /// the HUD takes at 16:9 before the quick bar meets the bars.</summary>
        public const int UiScaleDefault = 100, UiScaleMin = 50, UiScaleMax = 140, UiScaleStep = 10;
        public static readonly VolumeChannel[] Channels = (VolumeChannel[])Enum.GetValues(typeof(VolumeChannel));

        readonly int[] _volumes = { 100, 100, 100, 100, 100 };
        bool _firstPerson, _subtitles = true;
        int _uiScale = UiScaleDefault, _speakerMode;

        public event Action Changed;

        public PlayerOptions()
        {
            Video.Changed += () => Changed?.Invoke();
            Performance.Changed += () => Changed?.Invoke();
            Port.Changed += () => Changed?.Invoke();
        }

        /// <summary>The Video page (resolution, V-Sync, brightness, contrast, gamma).</summary>
        public VideoOptions Video { get; } = new VideoOptions();

        /// <summary>The Performance ("Details") page: preset and custom sliders.</summary>
        public PerformanceOptions Performance { get; } = new PerformanceOptions();

        /// <summary>The port's own rows (FOV, upscaling, music, companions); defaults = the original.</summary>
        public PortOptions Port { get; } = new PortOptions();

        /// <summary>0…100.</summary>
        public int Volume(VolumeChannel channel) => _volumes[(int)channel];

        public void SetVolume(VolumeChannel channel, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            if (_volumes[(int)channel] == percent) return;
            _volumes[(int)channel] = percent;
            Changed?.Invoke();
        }

        /// <summary>Volume as a factor 0…1, the master volume not included.</summary>
        public float Gain(VolumeChannel channel) => Volume(channel) / 100f;

        public bool FirstPerson
        {
            get => _firstPerson;
            set
            {
                if (_firstPerson == value) return;
                _firstPerson = value;
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// G3 "Subtitles" (ge3.ini [Options.Audio] Subtitle, gCHUDOptionAudioSubtitle): spoken lines show in the talk box
        /// (IDD_VIEW_TALK_2) only while on (gCGUIManager::StartSay checks eSSetupEngine+0x1bc).
        /// </summary>
        public bool Subtitles
        {
            get => _subtitles;
            set
            {
                if (_subtitles == value) return;
                _subtitles = value;
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// G3 "Speaker setup" (gCHUDOptionAudioSpeaker): eEAudioSpeakerMode 1 Mono … 7 Dolby ProLogic (the FMOD order,
        /// which Unity's AudioSpeakerMode shares); 0 = not chosen, the system's mode. G3 restarts to apply it
        /// (HUD_Menu_ResetWarning); the port applies it on the next start.
        /// </summary>
        public int SpeakerMode
        {
            get => _speakerMode;
            set
            {
                value = value < SpeakerModeMin || value > SpeakerModeMax ? 0 : value;
                if (_speakerMode == value) return;
                _speakerMode = value;
                Changed?.Invoke();
            }
        }

        public const int SpeakerModeMin = 1, SpeakerModeMax = 7, SpeakerModeStereo = 2;

        /// <summary>The slider texts of G3 (Game.dll table at 0x103f2d24, indexed by the mode; not localised).</summary>
        public static readonly string[] SpeakerModeNames = { "Raw", "Mono", "Stereo", "Quadro", "Surround", "5.1", "7.1", "Dolby ProLogic" };

        /// <summary>Russian slider texts: G3's string tables have none for the speaker modes (r31: 'Stereo' in the Russian menu).</summary>
        static readonly string[] SpeakerModeNamesRussian = { "Без обработки", "Моно", "Стерео", "Квадро", "Объемный", "5.1", "7.1", "Dolby ProLogic" };

        /// <summary>The speaker-mode slider text in the text language (G3's own English names for every other language).</summary>
        public static string SpeakerModeName(int mode, string language)
        {
            var names = language == "Russian" ? SpeakerModeNamesRussian : SpeakerModeNames;
            return mode >= 0 && mode < names.Length ? names[mode] : SpeakerModeNames[SpeakerModeStereo];
        }

        /// <summary>The slider position of a stored mode; an unset mode shows the running one (G3: 8 → GetSpeakerMode).</summary>
        public static int SpeakerSliderPosition(int stored, int running) =>
            stored >= SpeakerModeMin && stored <= SpeakerModeMax ? stored
            : running >= SpeakerModeMin && running <= SpeakerModeMax ? running : SpeakerModeStereo;

        /// <summary>GUI size in percent (<see cref="UiScaleMin"/>…<see cref="UiScaleMax"/>).</summary>
        public int UiScale
        {
            get => _uiScale;
            set
            {
                value = Math.Max(UiScaleMin, Math.Min(UiScaleMax, value));
                if (_uiScale == value) return;
                _uiScale = value;
                Changed?.Invoke();
            }
        }

        /// <summary>Slider positions of the GUI size (0…UiScaleSteps) and the conversion from and to percent.</summary>
        public static int UiScaleSteps => (UiScaleMax - UiScaleMin) / UiScaleStep;

        public static int UiScaleToStep(int percent) => (int)Math.Round((percent - UiScaleMin) / (double)UiScaleStep);

        public static int UiScaleFromStep(int step) => Math.Max(UiScaleMin, Math.Min(UiScaleMax, UiScaleMin + step * UiScaleStep));

        /// <summary>Slider value text as G3 shows it ("85%").</summary>
        public static string Percent(int percent) => percent.ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>Slider positions of a volume (0…Steps) and the conversion from and to percent.</summary>
        public static int Steps => 100 / VolumeStep;

        public static int ToStep(int percent) => (int)Math.Round(percent / (double)VolumeStep);

        public static int FromStep(int step) => Math.Max(0, Math.Min(100, step * VolumeStep));
    }
}
