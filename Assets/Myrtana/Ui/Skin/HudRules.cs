namespace Myrtana.Ui.Skin
{
    public enum StatusBarKind { Health, Stamina, Mana, Focus }

    /// <summary>Colours of the HUD status bars (Docs/formats-gui.md §6.3): a white gradient tinted per bar.</summary>
    public static class StatusBarColors
    {
        /// <param name="poisoned">HP (and the focus bar) turn teal while poisoned.</param>
        /// <param name="exhausted">SP turns grey while NPC status flag 8 (diseased) is set.</param>
        public static SkinColor Fill(StatusBarKind kind, bool poisoned = false, bool exhausted = false)
        {
            switch (kind)
            {
                case StatusBarKind.Stamina: return exhausted ? G3Palette.ExhaustedGrey : G3Palette.Gold;
                case StatusBarKind.Mana: return G3Palette.SteelBlue;
                default: return poisoned ? G3Palette.Teal : G3Palette.BloodRed;
            }
        }

        /// <summary>Fill fraction 0…1 (a maximum of 0 shows an empty bar).</summary>
        public static float Fraction(int current, int maximum) =>
            maximum <= 0 ? 0f : current <= 0 ? 0f : current >= maximum ? 1f : (float)current / maximum;

        /// <summary>Seconds a value change takes to animate on the bar.</summary>
        public const float AnimationSeconds = 0.5f;
    }

    /// <summary>Row kinds of the item panel stats table, in display order (Docs/formats-gui.md §6.5).</summary>
    public enum ItemStatKind
    {
        RequirementMet,
        RequirementUnmet,
        Damage,
        Quality,
        Protection,
        Modifier,
        ManaCost,
        Gold,
    }

    /// <summary>
    /// Row colours of the item stats table (gCHUDTableStackStats row styles): requirements blue/red, modifiers and
    /// skill bonuses green, gold value gold, everything else (damage, qualities, protections, mana cost: styles 2, 3, 5)
    /// parchment.
    /// </summary>
    public static class ItemStatColors
    {
        public static SkinColor Of(ItemStatKind kind)
        {
            switch (kind)
            {
                case ItemStatKind.RequirementMet: return G3Palette.SteelBlue;
                case ItemStatKind.RequirementUnmet: return G3Palette.BloodRed;
                case ItemStatKind.Modifier: return G3Palette.Teal;
                case ItemStatKind.Gold: return G3Palette.Gold;
                default: return G3Palette.Tan;
            }
        }

        /// <summary>Gap in pixels the table leaves after each section (between rows of different kinds).</summary>
        public const int SectionGap = 4;

        /// <summary>Sections: met and unmet requirements form one section.</summary>
        public static int Section(ItemStatKind kind) => kind == ItemStatKind.RequirementUnmet ? (int)ItemStatKind.RequirementMet : (int)kind;
    }

    /// <summary>Quest log speaker-line colours (§6.9).</summary>
    public static class QuestLogColors
    {
        public static SkinColor Speaker(bool isHero) => isHero ? G3Palette.SteelBlue : G3Palette.BloodRed;
    }

    /// <summary>Focus-name colour by attitude (§6.3).</summary>
    public enum FocusAttitude { Friendly, Party, Neutral, Dead, Angry, Hostile, Panic, Item }

    public static class FocusNameColors
    {
        public static SkinColor Of(FocusAttitude a)
        {
            switch (a)
            {
                case FocusAttitude.Friendly: return G3Palette.Green;
                case FocusAttitude.Party: return G3Palette.SteelBlue;
                case FocusAttitude.Angry: return G3Palette.Orange;
                case FocusAttitude.Hostile: return G3Palette.BloodRed;
                case FocusAttitude.Panic: return G3Palette.BrightOrange;
                case FocusAttitude.Item: return G3Palette.Cream;
                default: return G3Palette.Tan;
            }
        }
    }
}
