using System.Collections.Generic;
using System.Globalization;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The panel of the G3-look pages (inventory, trade, character, quest log) and the mapping of the session's view
    /// models to skin models (<see cref="ItemTileModel"/>, <see cref="ItemPanelModel"/>). One document above the HUD
    /// and below the dialog; pages are built from the original layouts and centred like the 4:3 original.
    /// </summary>
    public static class G3Pages
    {
        /// <summary>Above the HUD (100), below the dialog (110).</summary>
        public const int SortingOrder = GuiLayers.Pages;

        static readonly TextId ThousandText = new TextId("HUD_Thousand"), MillionText = new TextId("HUD_Million");

        /// <summary>The document root for the pages, or null when the G3 GUI is not imported (plain screens are used then).</summary>
        public static VisualElement TryCreateRoot(Transform parent)
        {
            var skin = G3Skin.Load();
            return skin == null ? null : G3PanelSettings.CreateDocument("G3 pages", GuiPanelKind.Page, parent, SortingOrder).rootVisualElement;
        }

        /// <summary>A page layout (IDD_PAGE_*) stretched over the panel; its views keep their centred placement.</summary>
        public static VisualElement Build(string layout)
        {
            var page = G3LayoutBuilder.Build(G3Skin.Load(), layout) ?? new VisualElement();
            page.AddToClassList("g3-fill-parent");
            page.style.width = StyleKeyword.Null;
            page.style.height = StyleKeyword.Null;
            page.pickingMode = PickingMode.Ignore;
            return page;
        }

        /// <summary>Builds a view layout (IDD_VIEW_*) into a placeholder that has no layout of the same name.</summary>
        public static VisualElement BuildInto(VisualElement slot, string viewLayout)
        {
            var view = G3LayoutBuilder.Build(G3Skin.Load(), viewLayout);
            if (slot == null || view == null) return view;
            view.AddToClassList("g3-fill-parent");
            view.style.width = StyleKeyword.Null;
            view.style.height = StyleKeyword.Null;
            slot.Add(view);
            return view;
        }

        /// <summary>
        /// A tile grid filling a list slot, sized by the slot's layout rect; it has the G3 scroll bar when the list's
        /// style has WS_VSCROLL (or as <paramref name="scrollable"/> says).
        /// </summary>
        public static G3ItemGrid Grid(VisualElement page, string slotName, bool? scrollable = null)
        {
            var slot = page.Q(slotName);
            if (!(slot?.userData is WindowData w)) return null;
            var grid = new G3ItemGrid(w.Width, w.Height, scrollable ?? TileGrid.HasVerticalScrollBar(w.Style));
            slot.pickingMode = PickingMode.Position;
            slot.Add(grid);
            return grid;
        }

        /// <summary>An item panel (IDD_VIEW_ITEM_2) in a placeholder.</summary>
        public static G3ItemPanel ItemPanel(VisualElement page, string slotName)
        {
            var panel = new G3ItemPanel(G3Skin.Load());
            panel.style.position = Position.Absolute;
            panel.style.left = panel.style.top = 0;
            page.Q(slotName)?.Add(panel);
            panel.Bind(null);
            return panel;
        }

        public static void SetText(VisualElement page, string name, string text)
        {
            if (page.Q(name) is Label label) label.text = text ?? string.Empty;
        }

        /// <summary>G3 writes amounts with thousands separators ("1,000,000").</summary>
        public static string Amount(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        public static void Localize(G3ItemGrid grid, GameSession session)
        {
            grid.ThousandSuffix = session.Localizer.Text(ThousandText);
            grid.MillionSuffix = session.Localizer.Text(MillionText);
        }

        public static ItemIconCategory Category(ItemCategory c)
        {
            switch (c)
            {
                case ItemCategory.Weapon: return ItemIconCategory.Weapon;
                case ItemCategory.Armor: return ItemIconCategory.Armor;
                case ItemCategory.Artefact: return ItemIconCategory.Artefact;
                case ItemCategory.Alchemy: return ItemIconCategory.Alchemy;
                case ItemCategory.Written: return ItemIconCategory.Written;
                case ItemCategory.Spellbook: return ItemIconCategory.Spellbook;
                case ItemCategory.Skill: return ItemIconCategory.Skill;
                default: return ItemIconCategory.Misc;
            }
        }

        /// <summary>A stack as a tile: equipped stacks tinted blue; trade tiles show only the count.</summary>
        public static ItemTileModel Tile(ItemRow row, bool countOnly = false) => new ItemTileModel
        {
            Texture = row.Icon,
            Category = Category(row.Category),
            Amount = row.Amount,
            Quality = (ItemQualityFlags)row.Quality,
            Equipped = row.Marked,
            CountOnly = countOnly,
        };

        public static List<ItemTileModel> Tiles(IReadOnlyList<ItemRow> rows, bool countOnly = false)
        {
            var tiles = new List<ItemTileModel>(rows.Count);
            foreach (var row in rows) tiles.Add(Tile(row, countOnly));
            return tiles;
        }

        public static ItemPanelModel Panel(ItemDetail detail)
        {
            var model = new ItemPanelModel
            {
                Name = detail.Name,
                Description = detail.Description,
                Texture = detail.Icon,
                Category = Category(detail.Category),
            };
            foreach (var line in detail.Lines)
                model.Rows.Add(new ItemStatRow(Kind(line.Kind), line.Label, line.Value,
                                               line.Quality != 0 ? ItemIconResolver.QualityRowIcon((ItemQualityFlags)line.Quality) : null));
            return model;
        }

        static ItemStatKind Kind(ItemLineKind kind)
        {
            switch (kind)
            {
                case ItemLineKind.RequirementMet: return ItemStatKind.RequirementMet;
                case ItemLineKind.RequirementUnmet: return ItemStatKind.RequirementUnmet;
                case ItemLineKind.Damage: return ItemStatKind.Damage;
                case ItemLineKind.Protection: return ItemStatKind.Protection;
                case ItemLineKind.Gold: return ItemStatKind.Gold;
                case ItemLineKind.Quality: return ItemStatKind.Quality;
                case ItemLineKind.ManaCost: return ItemStatKind.ManaCost;
                default: return ItemStatKind.Modifier;
            }
        }
    }
}
