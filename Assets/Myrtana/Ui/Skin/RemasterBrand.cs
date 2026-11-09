namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// rebrand (2026-10-21): how the demo presents itself to the player: "Gothic 3 Remaster". The main menu's version line
    /// (<c>IDC_STATIC_APP_VERSION2</c>, replaces CP's "pb/cpt/sb v1.75.14" of <see cref="AppVersionText"/>), the window
    /// title and the caption over the credits videos. Engine-free (Tools/G3Tests). The persistent data path keeps the
    /// project's product name (LocalLow/G3World/G3World), so saves and settings stay where they are.
    /// </summary>
    public static class RemasterBrand
    {
        public const string Title = "Gothic 3 Remaster";

        /// <summary>Team line for the credits (no person named: the owner's project).</summary>
        public const string Team = "Gothic 3 Remaster team";

        static bool Russian(string language) => language == "Russian";

        /// <summary>
        /// "Gothic 3 Remaster — демо 2026-10-21, сборка ff8b7e6" / "Gothic 3 Remaster — demo 2026-10-21, build ff8b7e6";
        /// <paramref name="build"/> "date[ commit]" from the demo build's stamp (null/empty: "Gothic 3 Remaster — demo").
        /// </summary>
        public static string VersionLine(string language, string build)
        {
            bool ru = Russian(language);
            string s = Title + (ru ? " — демо" : " — demo");
            if (string.IsNullOrWhiteSpace(build)) return s;
            var parts = build.Trim().Split(' ');
            s += " " + parts[0];
            if (parts.Length > 1 && parts[1].Length > 0) s += (ru ? ", сборка " : ", build ") + parts[1];
            return s;
        }

        /// <summary>The caption over the first credits video: the remaster, then the base it stands on.</summary>
        public static string CreditsCaption(string language) => Russian(language)
            ? Title + "\n" + "Ремейк Gothic 3 на Unity 6 с полным ремастером мира" + "\n" + "Команда Gothic 3 Remaster" + "\n\n" +
              "Основано на Gothic 3 Community Patch 1.75 (Community Patch Team)" + "\n" +
              "Gothic 3 © Piranha Bytes / THQ Nordic. Оригинальные титры — далее."
            : Title + "\n" + "A Unity 6 remake of Gothic 3 with a full world remaster" + "\n" + Team + "\n\n" +
              "Based on Gothic 3 Community Patch 1.75 (Community Patch Team)" + "\n" +
              "Gothic 3 © Piranha Bytes / THQ Nordic. Original credits follow.";

        /// <summary>Seconds the credits caption stays over the video.</summary>
        public const float CreditsCaptionSeconds = 12f;
    }
}
