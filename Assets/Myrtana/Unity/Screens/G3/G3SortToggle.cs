using System;
using Myrtana.Session;
using Myrtana.Unity.Ui.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The owner's sort modes of the item lists [owner], without any text of their own (fix-92): a click on the category tab
    /// that is already selected (or O on the inventory page) cycles <see cref="ItemSortMode"/>; the selected tab then
    /// carries a small G3 theme arrow (<see cref="ItemSorting.GlyphPart"/>), none in G3 order, so the default looks exactly
    /// like the original. The mode is kept in PlayerPrefs and read by the inventory, trade and loot lists (<see cref="Mode"/>);
    /// G3's group order always stays.
    /// </summary>
    public static class G3SortToggle
    {
        const string PrefsKey = "Myrtana.InventorySort";
        /// <summary>The silent hotkey of the inventory page (not bound by G3's key map nor by the pages).</summary>
        public const Key Hotkey = Key.O;
        /// <summary>Side of the mark in 1024×768 units: a corner of the 48×26 tab, beside the 16² category icon.</summary>
        const int GlyphSize = 11;

        static ItemSortMode? _mode;

        /// <summary>The sort mode of all item lists (PlayerPrefs; G3 order until the player picks another).</summary>
        public static ItemSortMode Mode
        {
            get
            {
                if (_mode == null)
                {
                    try { _mode = ItemSorting.Parse(PlayerPrefs.GetString(PrefsKey, ItemSortMode.G3.ToString())); }
                    catch (Exception) { _mode = ItemSortMode.G3; }
                }
                return _mode.Value;
            }
            set
            {
                _mode = value;
                try
                {
                    PlayerPrefs.SetString(PrefsKey, value.ToString());
                    PlayerPrefs.Save();
                }
                catch (Exception) { }
            }
        }

        /// <summary>Next mode, stored.</summary>
        public static ItemSortMode Cycle() => Mode = ItemSorting.Next(Mode);

        /// <summary>
        /// The sort mark for <paramref name="mode"/> (only the theme part's front layer: the framed triangle of the list
        /// header / scroll bar arrows), or null in G3 order. Absolutely placed on the right of a tab, mouse-transparent.
        /// </summary>
        public static VisualElement Glyph(ItemSortMode mode)
        {
            string part = ItemSorting.GlyphPart(mode);
            if (part == null) return null;
            var glyph = new G3Part(part) { name = "myr-sort-mark", pickingMode = PickingMode.Ignore };
            glyph.style.backgroundImage = new StyleBackground(StyleKeyword.None); // the part's back layer is a button face
            glyph.style.position = Position.Absolute;
            glyph.style.width = GlyphSize;
            glyph.style.height = GlyphSize;
            glyph.style.right = 3;
            glyph.style.top = Length.Percent(50);
            glyph.style.translate = new Translate(new Length(0), Length.Percent(-50));
            return glyph;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _mode = null; // domain reload is off in the editor
    }
}
