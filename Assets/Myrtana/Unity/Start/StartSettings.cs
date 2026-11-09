using System;
using G3.Unity;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The developer quick start (Docs/game-start.md): Play goes straight into a new game where the scene's hero stands
    /// (the generated master scene: Faring lower town), without logos, title, intro or the Ardea fight. On by default in
    /// the editor (menu Myrtana/Start/Dev quick start at Faring), off in players; a development build turns it on with
    /// the command-line switch <c>-quickstart</c>.
    /// </summary>
    public static class DevStart
    {
        public const string PrefKey = "Myrtana.DevQuickStart";
        public const string CommandLineSwitch = "-quickstart";

        public static bool QuickStart
        {
            get
            {
#if UNITY_EDITOR
                return UnityEditor.EditorPrefs.GetBool(PrefKey, true);
#else
                return Debug.isDebugBuild && Array.IndexOf(Environment.GetCommandLineArgs(), CommandLineSwitch) >= 0;
#endif
            }
        }
    }

    /// <summary>The menus' rule choices (<see cref="NewGameOptions"/>) as player preferences, stored at every change.</summary>
    public static class NewGameOptionsPreferences
    {
        const string AbKey = "Myrtana.AlternativeBalancing", AiKey = "Myrtana.AlternativeAI", DifficultyKey = "Myrtana.Difficulty";

        public static NewGameOptions Load()
        {
            var options = new NewGameOptions
            {
                AlternativeBalancing = PlayerPrefs.GetInt(AbKey, 1) != 0,
                AlternativeAI = PlayerPrefs.GetInt(AiKey, 1) != 0,
                Difficulty = (Difficulty)Mathf.Clamp(PlayerPrefs.GetInt(DifficultyKey, (int)Difficulty.Medium), 0, 2),
            };
            options.Changed += () =>
            {
                PlayerPrefs.SetInt(AbKey, options.AlternativeBalancing ? 1 : 0);
                PlayerPrefs.SetInt(AiKey, options.AlternativeAI ? 1 : 0);
                PlayerPrefs.SetInt(DifficultyKey, (int)options.Difficulty);
                PlayerPrefs.Save();
            };
            return options;
        }
    }

    /// <summary>
    /// The title screen's music (G3: situation "title" of location "myrtana"), set through the scene's
    /// <see cref="G3MusicSituationOverride"/> (the music director asks it first) while <see cref="Active"/>.
    /// </summary>
    public sealed class TitleMusic
    {
        public const string Situation = "title";
        bool _active;

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                var source = UnityEngine.Object.FindAnyObjectByType<G3MusicSituationOverride>();
                if (source == null) return;
                if (value) source.situation = Situation;
                else if (source.situation == Situation) source.situation = "";
                var context = UnityEngine.Object.FindAnyObjectByType<G3AudioContext>();
                if (context != null && context.isActiveAndEnabled) context.Refresh();
            }
        }

        static readonly object VideoHold = new object();

        /// <summary>
        /// G3 plays no situation music under its videos (logos, intro, credits — the end credits after the epilogue too):
        /// the director stays silent and does not re-target until the videos are over (<see cref="G3MusicDirector.Silence"/>).
        /// </summary>
        public static void Mute(bool mute) => G3MusicDirector.Silence(VideoHold, mute);
    }
}
