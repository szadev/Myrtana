using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// The version line at the top of the main menu (<c>IDC_STATIC_APP_VERSION2</c>, gCHUDMenuMain2::OnInitDialog): the
    /// application's version resource string ("1, 75, 20108, 14") split on commas, its first, second and last fields
    /// trimmed and joined with dots, after "pb/cpt/sb v" — "pb/cpt/sb v1.75.14" for Community Patch 1.75.14, the rules
    /// this game follows.
    /// </summary>
    public static class AppVersionText
    {
        public const string Prefix = "pb/cpt/sb v";

        /// <summary>The version resource of Game.dll/GUI.dll in the CP 1.75.14 install.</summary>
        public const string CommunityPatch = "1, 75, 20108, 14";

        public static string Format(string versionResource)
        {
            var parts = (versionResource ?? string.Empty).Split(',');
            if (parts.Length < 3) return Prefix + (versionResource ?? string.Empty).Trim();
            return Prefix + parts[0].Trim() + "." + parts[1].Trim() + "." + parts[parts.Length - 1].Trim();
        }
    }
}
