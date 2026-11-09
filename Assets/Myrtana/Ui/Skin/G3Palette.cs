namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// Fixed colours of the original GUI (Docs/formats-gui.md §3, §5, §5.1). Theme colours are also in the generated
    /// skin data; the ones here are those Game.dll sets in code, plus the theme text colours that components need
    /// outside a themed element.
    /// </summary>
    public static class G3Palette
    {
        // ---- theme text colours (§3 / §5) ----
        /// <summary>Default text, warm parchment #E9D9C7 at α 210.</summary>
        public static readonly SkinColor Text = new SkinColor(0xE9, 0xD9, 0xC7, 210);
        /// <summary>Hovered / focused text #FFEBDC.</summary>
        public static readonly SkinColor TextHighlight = new SkinColor(0xFF, 0xEB, 0xDC);
        /// <summary>Selected (pressed) text: parchment at full alpha.</summary>
        public static readonly SkinColor TextSelected = new SkinColor(0xE9, 0xD9, 0xC7);
        public static readonly SkinColor TextDisabled = new SkinColor(0xE9, 0xD9, 0xC7, 128);
        /// <summary>Labels (theme static_text) gold #E0C495.</summary>
        public static readonly SkinColor Label = new SkinColor(0xE0, 0xC4, 0x95);

        // ---- Game.dll palette (§5.1) ----
        /// <summary>#9F1813 blood red: HP bar, focus bar, unmet requirements, hostile focus names, NPC speaker lines, errors.</summary>
        public static readonly SkinColor BloodRed = new SkinColor(0x9F, 0x18, 0x13);
        /// <summary>#0C9770 teal green: HP bar while poisoned, stat bonus rows, result messages.</summary>
        public static readonly SkinColor Teal = new SkinColor(0x0C, 0x97, 0x70);
        /// <summary>#0C7097 steel blue: MP bar, met requirements, party focus names, hero speaker lines, equipped tile (hover/selected).</summary>
        public static readonly SkinColor SteelBlue = new SkinColor(0x0C, 0x70, 0x97);
        /// <summary>#12496B dark blue: equipped tile (normal).</summary>
        public static readonly SkinColor DarkBlue = new SkinColor(0x12, 0x49, 0x6B);
        /// <summary>#C7A648 gold: SP bar, stack counts, gold values, most game messages.</summary>
        public static readonly SkinColor Gold = new SkinColor(0xC7, 0xA6, 0x48);
        /// <summary>#515151 grey: SP bar while NPC status flag 8 is set.</summary>
        public static readonly SkinColor ExhaustedGrey = new SkinColor(0x51, 0x51, 0x51);
        /// <summary>#E0C495 tan: quick-slot digits, damage/protection/quality rows, neutral and dead focus names.</summary>
        public static readonly SkinColor Tan = new SkinColor(0xE0, 0xC4, 0x95);
        /// <summary>#D17210 orange: angry focus names, quest-change messages.</summary>
        public static readonly SkinColor Orange = new SkinColor(0xD1, 0x72, 0x10);
        /// <summary>#FF6A22 bright orange: panicking NPCs, chests not yet looted.</summary>
        public static readonly SkinColor BrightOrange = new SkinColor(0xFF, 0x6A, 0x22);
        /// <summary>#FFE6E6 (1, 0.9, 0.9): every row of the skill box requirement table (Game.dll row adder 0x100a5030).</summary>
        public static readonly SkinColor SkillStats = new SkinColor(0xFF, 0xE6, 0xE6);
        /// <summary>#00A660 green: friendly focus names.</summary>
        public static readonly SkinColor Green = new SkinColor(0x00, 0xA6, 0x60);
        /// <summary>#FFFAC8 cream: item/object focus names.</summary>
        public static readonly SkinColor Cream = new SkinColor(0xFF, 0xFA, 0xC8);
        /// <summary>#7F7F7F grey: version string in the main menu.</summary>
        public static readonly SkinColor VersionGrey = new SkinColor(0x7F, 0x7F, 0x7F);
        /// <summary>Text shadow / outline colour of tiles, tables and focus names.</summary>
        public static readonly SkinColor Shadow = SkinColor.Black;
    }
}
