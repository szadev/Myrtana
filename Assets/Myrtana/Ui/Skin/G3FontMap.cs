using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>Where the original GUI takes a text's font from (Docs/formats-gui.md §5, Docs/ui-skin.md "Fonts").</summary>
    public enum G3FontSource
    {
        /// <summary>The window's own font in the res file (<c>GUI.Window.Font.*</c>); its face is always "Times New Roman".</summary>
        Res,
        /// <summary>
        /// A control without a font of its own: the font of its theme element. Every theme element has "Times New Roman"
        /// −16 bold, the button plates −18 bold.
        /// </summary>
        Theme,
        /// <summary>
        /// A font Game.dll/Engine.dll creates in code with a positive (cell) height in the engine's default face
        /// (<c>ge3.ini GUI.DefaultFont</c>, "Gothic3" in the original, Comic Sans MS in the CP 1.75 ini).
        /// </summary>
        Code,
    }

    /// <summary>A font as the original creates it: a Win32 LOGFONT face, height (negative = em, positive = cell) and weight.</summary>
    public sealed class G3Font
    {
        public G3Font(string face, int height, int weight, bool italic = false)
        {
            Face = face;
            Height = height;
            Weight = weight;
            Italic = italic;
        }

        public string Face { get; }
        public int Height { get; }
        public int Weight { get; }
        public bool Italic { get; }
        public bool Bold => Weight >= 700;

        /// <summary>The size the port draws it at: the em of a res/theme font, the converted cell height of a code font.</summary>
        public float PortSize => Height < 0 ? -Height : G3FontMetrics.FromCellHeight(Height);

        public override string ToString() => $"{Face} {Height} w{Weight}{(Italic ? " italic" : "")}";
    }

    /// <summary>One kind of GUI text the player sees, the control that draws it in the original, and that control's font.</summary>
    public sealed class G3FontUse
    {
        public G3FontUse(string element, string control, G3FontSource source, G3Font original)
        {
            Element = element;
            Control = control;
            Source = source;
            Original = original;
        }

        public string Element { get; }
        /// <summary>The res control (<c>IDC_*</c>) or the code that draws it.</summary>
        public string Control { get; }
        public G3FontSource Source { get; }
        public G3Font Original { get; }
        /// <summary>The face the port draws this text with (<see cref="G3FontMap.PortFaceOf"/>).</summary>
        public string PortFace => G3FontMap.PortFaceOf(Source);
    }

    /// <summary>
    /// The single font mapping of the G3 skin: which font the original uses for each kind of GUI text, and which face the
    /// port draws it with. Found in the data and the binaries (CP 1.75 Engine.dll): every res and theme font is
    /// "Times New Roman" and reaches <c>D3DXCreateFontW</c> unchanged (eCThemeAdmin::CreateFont → eCGfxFont →
    /// eCDX9Font, no face substitution); only the code fonts use the engine's default face, which
    /// <c>eCGUIDatabase::Create</c> registers from Gothic3.ttf when <c>GUI.DefaultFont</c> is "Gothic3".
    /// <para>
    /// The port draws all of them with one face, Gothic3 plus the Cyrillic composed from its own Latin
    /// (<see cref="PortFontPath"/>), by the owner's decision: Res and Theme texts in Gothic3 at Times' cap height
    /// (<see cref="G3FontMetrics.ResFaceScale"/>) and with Times' line distance in paragraphs
    /// (<see cref="G3FontMetrics.ParagraphLineHeight"/>). <see cref="Uses"/> is the audited table (Docs/ui-skin.md "Fonts").
    /// </para>
    /// </summary>
    public static class G3FontMap
    {
        public const string TimesNewRoman = "Times New Roman";
        public const string Gothic3 = "Gothic3";

        /// <summary>The face of every GUI text in the port.</summary>
        public const string PortFace = Gothic3;

        /// <summary>The port's font file: Gothic3 + Cyrillic, quotes and … (Tools/Fonts/g3cyrillic.py), tracked in git.</summary>
        public const string PortFontPath = "Assets/G3/Fonts/Gothic3_Cyr.ttf";

        /// <summary>Theme font of every element but the button plates: static texts, lists, tabs, check boxes, tooltips.</summary>
        public static readonly G3Font ThemeText = new G3Font(TimesNewRoman, -16, 700);

        /// <summary>Theme font of the button plates (buttons without a res font).</summary>
        public static readonly G3Font ThemeButton = new G3Font(TimesNewRoman, -18, 700);

        /// <summary>Focus names (Game.dll 0x100796c0): cell 24, weight 800 (400 with GUI.DefaultFontBold=false).</summary>
        public static readonly G3Font FocusName = new G3Font(Gothic3, 24, 800);

        /// <summary>Game messages (gCHUDTextEffect, Game.dll 0x100a7640): cell 20, bold.</summary>
        public static readonly G3Font GameMessage = new G3Font(Gothic3, 20, 700);

        /// <summary>The port's face for a font source. One face for all (owner decision); kept per source so the table says why.</summary>
        public static string PortFaceOf(G3FontSource source) => PortFace;

        /// <summary>The original's font of a control: its res font, else the theme font of its kind.</summary>
        public static G3Font Resolve(G3Font resFont, bool button) => resFont ?? (button ? ThemeButton : ThemeText);

        static G3FontUse Res(string element, string control, int height, int weight, bool italic = false) =>
            new G3FontUse(element, control, G3FontSource.Res, new G3Font(TimesNewRoman, height, weight, italic));

        static G3FontUse Theme(string element, string control, bool button = false) =>
            new G3FontUse(element, control, G3FontSource.Theme, button ? ThemeButton : ThemeText);

        static G3FontUse Code(string element, string control, G3Font font) => new G3FontUse(element, control, G3FontSource.Code, font);

        /// <summary>The audited GUI texts (G3 res/theme data; code fonts from the binaries).</summary>
        public static readonly IReadOnlyList<G3FontUse> Uses = new[]
        {
            Res("tip box: title", "IDC_LABEL_TUTORIAL_2", -24, 400, italic: true),
            Theme("tip box: text", "IDC_LABEL_TUTORIAL_MESSAGE_2"),
            Theme("tip box: check box", "IDC_CHECKBOX_TUTORIAL_2"),
            Theme("tip box: OK", "IDC_BUTTON_TUTORIAL_OK_2", button: true),
            Res("main/options menu buttons", "IDC_BUTTON_MENU_NEW_GAME_2", -19, 700),
            Theme("menu Back (main)", "IDC_BUTTON_MENU_MAIN_BACK_2", button: true),
            Res("menu question", "IDC_LABEL_MENU_REQUEST_2", -18, 400),
            Res("option labels", "IDC_LABEL_AUDIO_VOLUME_MUSIC_2", -18, 400),
            Res("option values", "IDC_VALUE_CONTROLS_CAMLOOKAROUNDINVERSE_2", -18, 400),
            Res("option headings", "IDC_LABEL_CONTROLS_KEYBOARD_2", -24, 700),
            Res("difficulty check box", "IDC_CHECKBOX_MENU_DIFFICULTY_AIMODE_2", -18, 700),
            Res("key mapping title", "IDC_STATIC_CONTROLS_CUSTOM_KEYMAPPING_2", -24, 700, italic: true),
            Theme("key mapping list", "IDC_LISTCTRL_CONTROLS_CUSTOM_KEYMAPPING_2"),
            Res("version string", "IDC_STATIC_APP_VERSION2", -12, 700),
            Theme("dialog choices", "IDC_LISTCTRL_DIALOG_2"),
            Res("subtitles", "IDC_LABEL_TALK_2", -14, 700),
            Res("slideshow subtitle", "IDC_STATIC_SLIDESHOW_SUBTITLE_2", -19, 700),
            Res("inventory title", "IDC_LABEL_INVENTORY_2", -24, 700, italic: true),
            Theme("inventory/trade/loot lists", "IDC_LISTCTRL_INVENTORY_2"),
            Theme("page tabs", "IDC_TABCTRL_INVENTORY_2"),
            Res("item name", "IDC_LABEL_ITEM_2", -18, 700),
            Res("item stats", "IDC_TABLE_ITEM_STATS_2", -14, 700),
            Res("item description", "IDC_LABEL_ITEM_DESC_2", -13, 700, italic: true),
            Res("trade gold", "IDC_VALUE_TRADE_POOL_PLAYER_GOLD_2", -16, 700),
            Res("loot title", "IDC_LABEL_LOOT_2", -24, 400, italic: true),
            Theme("tooltips", "tooltipctrl_main"),
            Res("quest log title", "IDC_LABEL_QUESTS_2", -24, 400, italic: true),
            Theme("quest log list", "IDC_LISTCTRL_QUESTS_2"),
            Res("documents title", "IDC_LABEL_DOCUMENTS_2", -24, 400, italic: true),
            Res("book/letter header", "IDC_LABEL_BOOK_HEADER_2", -19, 700, italic: true),
            Res("book/letter text", "IDC_LABEL_BOOK_LEFT_2", -14, 700),
            Res("recipe text", "IDC_STATIC_RECIPE_DESC_2", -14, 700),
            Res("character: skill name", "IDC_LABEL_SKILL_2", -18, 700, italic: true),
            Res("character: skill description", "IDC_LABEL_SKILL_DESC_2", -14, 400),
            Code("HUD focus name", "gCHUDPageView focus (Game.dll 0x100796c0)", FocusName),
            Code("HUD game messages", "gCHUDTextEffect (Game.dll 0x100a7640)", GameMessage),
            Code("death: \"The End\" (GO_GameOver)", "Kill → PrintGameMessage", GameMessage),
        };
    }
}
