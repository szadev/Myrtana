using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Components
{
    /// <summary>
    /// The item panel (<c>IDD_VIEW_ITEM_2</c>, 287×350, <c>G3_HUD_View_Item_Back</c>), built from the original layout
    /// and bound to an <see cref="ItemPanelModel"/>: name (18 px bold, centred), 52² icon, description (13 px bold
    /// italic, wrapped), stats table (14 px bold; label left, value right; colour per row kind; a 4 px gap after each
    /// section; quality rows with a 16² icon and the label 18 px further right). It follows the selection — G3 has no
    /// hover tooltip for items. A description longer than the four lines of its box grows the box and moves the table
    /// down (<see cref="ItemPanelLayout"/>), so the whole text is shown.
    /// </summary>
    public class G3ItemPanel : VisualElement
    {
        public const string Layout = "IDD_VIEW_ITEM_2";
        const int FontSize = 14, IconSize = 16, IconIndent = 18, LabelGap = 6;

        readonly Label _name, _description;
        readonly G3Image _icon;
        readonly VisualElement _table, _tableSlot;
        readonly WindowData _descriptionRect, _tableRect;

        public G3ItemPanel(G3Skin skin)
        {
            AddToClassList("g3-item-panel");
            pickingMode = PickingMode.Ignore;
            var root = G3LayoutBuilder.Build(skin, Layout);
            if (root == null) return;
            style.width = root.style.width;
            style.height = root.style.height;
            root.style.position = Position.Absolute;
            Add(root);
            _name = root.Q<Label>("IDC_LABEL_ITEM_2");
            _description = root.Q<Label>("IDC_LABEL_ITEM_DESC_2");
            _icon = root.Q<G3Image>("IDC_STATIC_ITEM_ICON_2");
            _tableSlot = root.Q("IDC_TABLE_ITEM_STATS_2");
            _tableRect = _tableSlot?.userData as WindowData;
            _descriptionRect = _description?.userData as WindowData;
            _table = new VisualElement { name = "rows", pickingMode = PickingMode.Ignore };
            _table.AddToClassList("g3-fill-parent");
            _tableSlot?.Add(_table);
            if (_name != null) _name.style.color = G3Palette.TextSelected.ToColor();
            if (_description != null)
            {
                _description.style.color = G3Palette.Text.ToColor();
                _description.style.overflow = Overflow.Hidden;
                _description.enableRichText = true;
            }
        }

        public void Bind(ItemPanelModel model)
        {
            style.visibility = model == null ? Visibility.Hidden : Visibility.Visible;
            if (model == null || _table == null) return;
            var skin = G3Skin.Load();
            if (_name != null) _name.text = model.Name;
            // Four wrapped lines fit the 66 px box (the font's own 2.16 em line height would fit two and overflow).
            if (_description != null)
            {
                _description.text = $"<line-height={ItemPanelLayout.DescriptionLineHeight}px>{model.Description}";
                PlaceTable(0);
                _description.schedule.Execute(FitDescription);
            }
            _icon?.SetSprite(skin?.ItemIcon(model.Texture, model.Category, model.Learned));
            _table.Clear();
            int lastSection = -1;
            foreach (var r in model.Rows)
            {
                int section = ItemStatColors.Section(r.Kind);
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("g3-stat-row");
                row.style.height = G3Text.LineHeight(FontSize);
                if (lastSection >= 0 && section != lastSection) row.style.marginTop = ItemStatColors.SectionGap;
                lastSection = section;
                if (!string.IsNullOrEmpty(r.Icon))
                {
                    var icon = new G3Image(r.Icon);
                    icon.style.width = icon.style.height = IconSize;
                    icon.style.position = Position.Absolute;
                    row.Add(icon);
                }
                var label = new Label(r.Label) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("g3-stat-row__label");
                if (!string.IsNullOrEmpty(r.Icon)) label.style.marginLeft = IconIndent;
                var value = new Label(r.Value) { pickingMode = PickingMode.Ignore };
                value.AddToClassList("g3-stat-row__value");
                Color c = ItemStatColors.Of(r.Kind).ToColor();
                label.style.color = c;
                value.style.color = c;
                G3Text.Shadow(label);
                G3Text.Shadow(value);
                row.Add(label);
                row.Add(value);
                _table.Add(row);
                bool indented = !string.IsNullOrEmpty(r.Icon);
                label.schedule.Execute(() =>
                    G3Text.FitWidth(label, row.layout.width - (indented ? IconIndent : 0) - G3Text.TextWidth(value) - LabelGap, FontSize));
            }
        }

        /// <summary>Measures the wrapped description in its box width and moves the table down by what does not fit.</summary>
        void FitDescription()
        {
            if (_description == null || _descriptionRect == null) return;
            float height = _description.MeasureTextSize(_description.text, _descriptionRect.Width, MeasureMode.Exactly, 0, MeasureMode.Undefined).y;
            PlaceTable(ItemPanelLayout.TableShift(height, _tableRect?.Height ?? 0));
        }

        /// <summary>The description box <paramref name="shift"/> px taller and the table that much lower and shorter.</summary>
        void PlaceTable(float shift)
        {
            if (_descriptionRect != null) _description.style.height = _descriptionRect.Height + shift;
            if (_tableSlot == null || _tableRect == null) return;
            _tableSlot.style.top = _tableRect.Top + shift;
            _tableSlot.style.height = _tableRect.Height - shift;
        }
    }
}
