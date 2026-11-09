using System;

namespace Myrtana.Ui.Skin
{
    /// <summary>Item category as far as the GUI cares (values of G3 gEItemCategory).</summary>
    public enum ItemIconCategory { None = 0, Weapon = 1, Armor = 2, Artefact = 3, Alchemy = 4, Misc = 6, Written = 7, Spellbook = 9, Skill = 10 }

    /// <summary>Quality flags of an item stack (G3 values) as far as icons are concerned.</summary>
    [Flags]
    public enum ItemQualityFlags
    {
        None = 0,
        Poison = 0x04,
        Sharpened = 0x20,
        Blessed = 0x40,
        Forged = 0x80,
        Worn = 0x100,
    }

    /// <summary>
    /// Which bitmap an item stack shows (Docs/formats-gui.md §4): the item's
    /// <c>gCItem_PS.Texture</c>; when it is empty — or, in this port, not packed — the per-category default
    /// <c>G3_HUD_Icon_Category_{Weapon,Armor,Artefact,Food,Misc}.tga</c>. Spell-book and skill (perk) items show their
    /// <c>&lt;name&gt;_Grey</c> variant while not learned. Icons are pre-rendered 64×64 tiles; there is no 3D preview.
    /// </summary>
    public sealed class ItemIconResolver
    {
        readonly IImageCatalog _images;

        public ItemIconResolver(IImageCatalog images) { _images = images ?? throw new ArgumentNullException(nameof(images)); }

        /// <summary>Default icon of a category (table at Game.dll init: None and 5…10 → Misc, Alchemy → Food).</summary>
        public static string CategoryIcon(ItemIconCategory category)
        {
            switch (category)
            {
                case ItemIconCategory.Weapon: return "G3_HUD_Icon_Category_Weapon.tga";
                case ItemIconCategory.Armor: return "G3_HUD_Icon_Category_Armor.tga";
                case ItemIconCategory.Artefact: return "G3_HUD_Icon_Category_Artefact.tga";
                case ItemIconCategory.Alchemy: return "G3_HUD_Icon_Category_Food.tga";
                default: return "G3_HUD_Icon_Category_Misc.tga";
            }
        }

        /// <summary>"Spell_Fireball.tga" → "Spell_Fireball_Grey.tga" (the extension is kept).</summary>
        public static string GreyVariant(string texture)
        {
            if (string.IsNullOrEmpty(texture)) return texture;
            int dot = texture.LastIndexOf('.');
            int slash = Math.Max(texture.LastIndexOf('\\'), texture.LastIndexOf('/'));
            return dot > slash ? texture.Substring(0, dot) + "_Grey" + texture.Substring(dot) : texture + "_Grey";
        }

        /// <summary>
        /// Image name for an item: its texture if packed, else the category default. <paramref name="learned"/> only
        /// matters for spell books and skills: false selects the grey variant when it exists.
        /// </summary>
        public string Resolve(string texture, ItemIconCategory category, bool learned = true)
        {
            if (!string.IsNullOrEmpty(texture))
            {
                bool greyable = category == ItemIconCategory.Spellbook || category == ItemIconCategory.Skill;
                if (greyable && !learned)
                {
                    string grey = GreyVariant(texture);
                    if (_images.Contains(grey)) return grey;
                }
                if (_images.Contains(texture)) return texture;
            }
            return CategoryIcon(category);
        }

        /// <summary>
        /// The one quality icon a grid tile shows (bottom-left, 16×16): Blessed &gt; Poison &gt; Sharpened &gt; Forged; Worn is
        /// never shown on the grid. Null = none.
        /// </summary>
        public static string QualityIcon(ItemQualityFlags q)
        {
            if ((q & ItemQualityFlags.Blessed) != 0) return "G3_HUD_Icon_Quality_Blessed.tga";
            if ((q & ItemQualityFlags.Poison) != 0) return "G3_HUD_Icon_Quality_Poison.tga";
            if ((q & ItemQualityFlags.Sharpened) != 0) return "G3_HUD_Icon_Quality_Sharpened.tga";
            if ((q & ItemQualityFlags.Forged) != 0) return "G3_HUD_Icon_Quality_Forged.tga";
            return null;
        }

        /// <summary>Icon of a quality row in the item panel (all five, including Worn).</summary>
        public static string QualityRowIcon(ItemQualityFlags single)
        {
            switch (single)
            {
                case ItemQualityFlags.Worn: return "G3_HUD_Icon_Quality_Worn.tga";
                default: return QualityIcon(single);
            }
        }
    }

    /// <summary>Stack-count text of a tile (bottom-right, gold, black shadow).</summary>
    public static class StackCountText
    {
        /// <summary>
        /// Below 2 (or for spell books / skills) nothing; below 1000 the number; up to 999,999 <c>n/1000</c> + the
        /// localized "thousand" suffix (<c>HUD_Thousand</c>); from a million <c>n/1000000</c> + <c>HUD_Million</c>.
        /// </summary>
        public static string Format(long amount, ItemIconCategory category, string thousandSuffix, string millionSuffix)
        {
            if (amount < 2 || category == ItemIconCategory.Spellbook || category == ItemIconCategory.Skill) return null;
            if (amount < 1000) return amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (amount < 1000000) return (amount / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + thousandSuffix;
            return (amount / 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture) + millionSuffix;
        }

        /// <summary>Quick-slot digit (top-left, tan): slot index 0…9 is shown as 1…9, 0 (the 10th key is 0); none = null.</summary>
        public static string QuickSlotDigit(int slotIndex) => slotIndex < 0 || slotIndex > 9 ? null : ((slotIndex + 1) % 10).ToString();
    }
}
