using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// One 46×46 item cell (inventory, trade, loot, quick slots, skills; Docs/formats-gui.md §6.5): theme
    /// <c>listctrl_item_tiles</c> (stone square tinted #784632, hover #BE966E, selected #F0C8A0, bronze frame in front),
    /// the 64² icon inset 6 px left/right and 4 px top/bottom, count bottom-right gold, one quality icon bottom-left,
    /// quick-slot digit top-left tan. Equipped stacks are tinted blue (#12496B, hover/selected #0C7097) — no extra icon.
    /// An empty tile (model null) is still drawn: the grid always looks like a slot grid.
    /// </summary>
    public class G3ItemTile : G3Part
    {
        public const string TilePart = "listctrl_item_tiles";
        public const int Size = 46;
        public const string EquippedClass = "g3-tile--equipped";

        readonly G3Image _icon, _quality;
        readonly Label _count, _digit;

        public ItemTileModel Model { get; private set; }
        public int Index { get; set; }

        public G3ItemTile() : base(TilePart)
        {
            AddToClassList("g3-tile");
            _icon = new G3Image();
            _icon.AddToClassList("g3-tile__icon");
            _quality = new G3Image();
            _quality.AddToClassList("g3-tile__quality");
            _count = new Label { pickingMode = PickingMode.Ignore };
            _count.AddToClassList("g3-tile__count");
            _digit = new Label { pickingMode = PickingMode.Ignore };
            _digit.AddToClassList("g3-tile__digit");
            // icon below the frame, overlays above it
            Insert(0, _icon);
            Add(_quality);
            Add(_count);
            Add(_digit);
            G3Text.Shadow(_count);
            G3Text.Shadow(_digit);
            Bind(null);
        }

        /// <param name="thousand">Localized <c>HUD_Thousand</c> suffix.</param>
        /// <param name="million">Localized <c>HUD_Million</c> suffix.</param>
        public void Bind(ItemTileModel model, string thousand = "k", string million = "m")
        {
            Model = model;
            var skin = G3Skin.Load();
            if (model == null)
            {
                _icon.SetSprite(null);
                _quality.SetSprite(null);
                _count.text = _digit.text = string.Empty;
                EnableInClassList(EquippedClass, false);
                return;
            }
            _icon.SetSprite(skin?.ItemIcon(model.Texture, model.Category, model.Learned));
            string count = StackCountText.Format(model.Amount, model.Category, thousand, million);
            _count.text = count ?? string.Empty;
            string quality = model.CountOnly ? null : ItemIconResolver.QualityIcon(model.Quality);
            _quality.Image = quality;
            _digit.text = model.CountOnly ? string.Empty : StackCountText.QuickSlotDigit(model.QuickSlot) ?? string.Empty;
            EnableInClassList(EquippedClass, model.Equipped);
        }
    }
}
