using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>
    /// Strings the game uses that are missing from the CP 1.75 string table (CPT forgot them; the Russian Update Pack
    /// added some back). The localizer falls back to these after the selected and the fallback language, so the player
    /// sees a sentence instead of the id. Own wording, not game data. The owner's additions on the item pages
    /// carry no text of their own (fix-92: sort mark on the tab, drop by dragging out). Ids are case-insensitive, as G3's.
    /// </summary>
    public static class PatchStrings
    {
        static readonly Dictionary<string, Dictionary<string, string>> Texts =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["HUD_ChestLocked"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["English"] = "The chest is locked!",
                    ["Russian"] = "Сундук заперт!",
                    ["German"] = "Die Truhe ist verschlossen!",
                },
                // fix-88 [owner]: focus names of the fires the hero may cook at (G3 shows them without a focus, so its
                // string table has no FO_ entry for these meshes)
                ["FO_G3_Object_Fireplace_02"] = Texts3("Hearth", "Очаг", "Herd"),
                ["FO_G3_Object_Fireplace_03"] = Texts3("Hearth", "Очаг", "Herd"),
                ["FO_G3_Object_Fireplace_04"] = Texts3("Hearth", "Очаг", "Herd"),
                ["FO_G3_Object_Fireplace_Large_01"] = Texts3("Fireplace", "Камин", "Kamin"),
                ["FO_G3_Object_Fire_Cage_01"] = Texts3("Brazier", "Жаровня", "Feuerkorb"),
                ["FO_G3_Object_Varant_Firebasin_01"] = Texts3("Fire basin", "Огненная чаша", "Feuerschale"),
                // fix-126 [owner]: captions of the map pages' filter bar (Ui.Skin.MapFilterBarLayout.TextId; the port's own buttons)
                ["MYR_MapFilter_Chests"] = Texts3("Locked chests", "Сундуки", "Truhen"),
                ["MYR_MapFilter_Names"] = Texts3("Place names", "Названия", "Ortsnamen"),
                ["MYR_MapFilter_Companions"] = Texts3("Companions", "Спутники", "Gefährten"),
                ["MYR_MapFilter_Quests"] = Texts3("Quest people", "Задания", "Aufträge"),
                // fix-235: the hero's refusal and reading lines are recorded, but CP 1.75 dropped their texts in every language
                // (an empty subtitle box after a failed pickpocket). English and German as in G3 before the Community Patch;
                // the Russian is ours, after the Russian recording.
                ["SVM_Player_TooHard"] = Texts3("Too hard!", "Слишком сложно!", "Zu schwer!"), // fix-248: not "heavy" (locks, pickpocket, spells)
                ["SVM_Player_TooHeavy"] = Texts3("That is too heavy for me...", "Это для меня слишком тяжело...", "Das ist zu schwer für mich..."),
                ["SVM_Player_Impossible"] = Texts3("Impossible!", "Невозможно!", "Unmöglich!"),
                ["SVM_Player_PickBroke"] = Texts3("Broken.", "Сломалась.", "Abgebrochen."),
                ["SVM_Player_NeedKey"] = Texts3("I need the right key.", "Мне нужен подходящий ключ.", "Ich brauche den richtigen Schlüssel."),
                ["SVM_Player_NeedLockpick"] = Texts3("I need a lockpick.", "Мне нужна отмычка.", "Ich brauche einen Dietrich."),
                ["SVM_Player_NeedKeyOrLockpick"] = Texts3("I need a lockpick - or the right key.", "Мне нужна отмычка - или подходящий ключ.",
                                                          "Ich brauche einen Dietrich - oder den richtigen Schlüssel."),
                ["SVM_Player_NothingToGet_00"] = Texts3("Nothing to be had there!", "Тут нечего взять!", "Da ist nichts zu holen!"),
                ["SVM_Player_NothingToGet_01"] = Texts3("Nothing to be had!", "Нечего взять!", "Nichts zu holen!"),
                ["SVM_Player_NothingToGet_02"] = Texts3("Nothing more to be had there.", "Тут больше нечего взять.", "Da ist nichts mehr zu holen."),
                ["SVM_Player_NothingNew_00"] = Texts3("There's nothing new here...", "Здесь ничего нового...", "Hier steht nichts Neues..."),
                ["SVM_Player_NothingNew_01"] = Texts3("I already know that...", "Это я уже знаю...", "Das kenne ich schon..."),
                ["SVM_Player_NothingNew_02"] = Texts3("Uninteresting...", "Неинтересно...", "Uninteressant..."),
                ["SVM_Player_AlchemyBook"] = Texts3("Here it says something about alchemy...", "Здесь что-то написано об алхимии...",
                                                    "Hier steht etwas über Alchemie..."),
                ["SVM_Player_SmithBook"] = Texts3("Here is something about the art of forging...", "Здесь что-то об искусстве ковки...",
                                                  "Hier steht etwas über die Kunst des Schmiedens..."),
                // fix-248 [port]: the HUD notice of a failed pickpocket (G3 shows none, only the victim's shout and attack)
                ["MYR_PickpocketCaught"] = Texts3("You were caught stealing!", "Вас поймали на краже!", "Du wurdest beim Stehlen erwischt!"),
                ["SVM_Player_MissingIngredients"] = Texts3("I don't have the ingredients for that.", "У меня нет для этого ингредиентов.",
                                                           "Dazu fehlen mir die Zutaten."),
            };

        static Dictionary<string, string> Texts3(string english, string russian, string german) =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["English"] = english, ["Russian"] = russian, ["German"] = german };

        /// <summary>The text in <paramref name="language"/>, else in English; false for an id that is not patched.</summary>
        public static bool TryGet(string language, string id, out string text)
        {
            text = null;
            if (id == null || !Texts.TryGetValue(id, out var byLanguage)) return false;
            return (language != null && byLanguage.TryGetValue(language, out text)) || byLanguage.TryGetValue("English", out text);
        }
    }
}
