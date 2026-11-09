using System;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The recipe view (<c>IDD_VIEW_RECIPE_2</c>, Docs/interactive-objects.md §6.4), shared by the documents page and the
    /// station page: result name, icon, description, the result's numbers, ingredients (red = not enough for one craft)
    /// and the required knowledge (red = not known).
    /// </summary>
    public sealed class G3RecipeView
    {
        const int FontSize = 14, IconSize = 64, DescriptionLineHeight = 15;

        readonly VisualElement _root;
        readonly G3Image _icon;
        readonly VisualElement _stats, _ingredients, _knowledge;

        public G3RecipeView(VisualElement root)
        {
            _root = root;
            if (root == null) return;
            _icon = new G3Image { name = "recipe-icon" };
            _icon.style.width = _icon.style.height = IconSize;
            root.Q("IDC_STATIC_RECIPE_ICON_2")?.Add(_icon);
            _stats = Table(root, "IDC_STATIC_RECIPE_ITEM_STATS_2");
            _ingredients = Table(root, "IDC_STATIC_INGREDIENTS_2");
            _knowledge = Table(root, "IDC_STATIC_RECIPE_REQ_SKILLS_2");
            if (root.Q("IDC_STATIC_RECIPE_DESC_2") is Label description)
            {
                description.enableRichText = true;
                description.style.whiteSpace = WhiteSpace.Normal;
                description.style.overflow = Overflow.Hidden;
                description.style.color = G3Palette.Text.ToColor();
            }
        }

        public VisualElement Root => _root;

        public void Show(bool visible)
        {
            if (_root != null) _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void Bind(RecipeSheet sheet)
        {
            if (_root == null) return;
            Show(sheet != null);
            if (sheet == null) return;
            G3Pages.SetText(_root, "IDC_LABEL_RECIPE_2", sheet.Title);
            G3Pages.SetText(_root, "IDC_STATIC_RECIPE_DESC_2", $"<line-height={DescriptionLineHeight}px>{sheet.Description}");
            G3Pages.SetText(_root, "IDC_LABEL_INGREDIENTS_2", sheet.IngredientsLabel);
            G3Pages.SetText(_root, "IDC_LABEL_RECIPE_REQ_SKILLS_2", sheet.KnowledgeLabel);
            _icon.SetSprite(G3Skin.Load()?.ItemIcon(sheet.Icon, G3Pages.Category(sheet.Category), true));

            var panel = G3Pages.Panel(new ItemDetail(sheet.Title, sheet.Description, sheet.Icon, sheet.Category, sheet.Stats));
            Clear(_stats);
            foreach (var row in panel.Rows) Row(_stats, row.Label, row.Value, ItemStatColors.Of(row.Kind).ToColor());
            Clear(_ingredients);
            foreach (var line in sheet.Ingredients) Row(_ingredients, line.Text, null, (line.Carried ? G3Palette.Tan : G3Palette.BloodRed).ToColor());
            Clear(_knowledge);
            if (!string.IsNullOrEmpty(sheet.Knowledge))
                Row(_knowledge, sheet.Knowledge, null, (sheet.KnowsSkill ? G3Palette.SteelBlue : G3Palette.BloodRed).ToColor());
        }

        static VisualElement Table(VisualElement root, string slot)
        {
            var parent = root.Q(slot);
            if (parent == null) return null;
            if (parent is Label label) label.text = string.Empty;
            var table = new VisualElement { name = "rows", pickingMode = PickingMode.Ignore };
            table.AddToClassList("g3-fill-parent");
            parent.Add(table);
            return table;
        }

        static void Clear(VisualElement table) => table?.Clear();

        static void Row(VisualElement table, string text, string value, Color color)
        {
            if (table == null) return;
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("g3-stat-row");
            row.style.height = G3Text.LineHeight(FontSize);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("g3-stat-row__label");
            label.style.color = color;
            G3Text.Shadow(label);
            row.Add(label);
            if (value != null)
            {
                var v = new Label(value) { pickingMode = PickingMode.Ignore };
                v.AddToClassList("g3-stat-row__value");
                v.style.color = color;
                G3Text.Shadow(v);
                row.Add(v);
            }
            table.Add(row);
        }
    }

    /// <summary>Letter (<c>IDD_VIEW_LETTER_2</c>) and book (<c>IDD_VIEW_BOOK_2</c>) views of the documents page.</summary>
    public static class G3TextViews
    {
        public static void PrepareText(VisualElement root, params string[] labels)
        {
            if (root == null) return;
            foreach (var name in labels)
                if (root.Q(name) is Label label)
                {
                    label.style.whiteSpace = WhiteSpace.Normal;
                    label.style.overflow = Overflow.Hidden;
                    label.enableRichText = false;
                }
        }

        public static void BindLetter(VisualElement root, DocumentSheet sheet)
        {
            if (root == null) return;
            G3Pages.SetText(root, "IDC_LABEL_LETTER_HEADER_2", sheet.Header);
            G3Pages.SetText(root, "IDC_LABEL_LETTER_TEXT_2", sheet.Text);
        }

        public static void BindBook(VisualElement root, DocumentSheet sheet)
        {
            if (root == null) return;
            G3Pages.SetText(root, "IDC_LABEL_BOOK_HEADER_2", sheet.Header);
            G3Pages.SetText(root, "IDC_LABEL_BOOK_LEFT_2", sheet.Text);
            G3Pages.SetText(root, "IDC_LABEL_BOOK_RIGHT_2", sheet.RightText);
        }
    }

    /// <summary>
    /// The amount dialog (<c>IDD_VIEW_AMOUNT_2</c>) of the station pages: a slider 1..max starting at max, the value, OK and
    /// Cancel (G3 opens it only when more than one can be made).
    /// </summary>
    public sealed class G3AmountView
    {
        static readonly TextId OkText = new TextId("HUD_Ok"), CancelText = new TextId("HUD_Cancel");

        readonly VisualElement _root;
        readonly SliderInt _slider;
        Action<int> _done;

        public G3AmountView(VisualElement slot)
        {
            Slot = slot;
            _root = G3Pages.BuildInto(slot, "IDD_VIEW_AMOUNT_2");
            if (slot != null) slot.pickingMode = PickingMode.Position;
            if (_root == null) return;
            _root.pickingMode = PickingMode.Position;
            var sliderSlot = _root.Q("IDC_SLIDER_AMOUNT_2");
            if (sliderSlot != null)
            {
                _slider = new SliderInt(1, 1) { pageSize = 1, focusable = true };
                _slider.AddToClassList("unity-slider");
                _slider.style.position = Position.Absolute;
                _slider.style.left = _slider.style.top = _slider.style.right = _slider.style.bottom = 0;
                _slider.style.marginLeft = _slider.style.marginRight = _slider.style.marginTop = _slider.style.marginBottom = 0;
                _slider.RegisterValueChangedCallback(e => G3Pages.SetText(_root, "IDC_VALUE_AMOUNT_2", G3Pages.Amount(e.newValue)));
                sliderSlot.pickingMode = PickingMode.Position;
                sliderSlot.Add(_slider);
            }
            if (_root.Q<G3Button>("IDC_BUTTON_AMOUNT_OK_2") is G3Button ok) ok.clicked += Confirm;
            if (_root.Q<G3Button>("IDC_BUTTON_AMOUNT_CANCEL_2") is G3Button cancel) cancel.clicked += Cancel;
            Hide();
        }

        VisualElement Slot { get; }

        public bool IsOpen => Slot != null && Slot.style.display == DisplayStyle.Flex;

        /// <summary>Asks for 1..<paramref name="max"/>; <paramref name="done"/> gets the amount (not called on Cancel).</summary>
        public void Ask(GameSession session, string title, int max, Action<int> done)
        {
            if (_root == null || _slider == null)
            {
                done(max);
                return;
            }
            _done = done;
            G3Pages.SetText(_root, "IDC_LABEL_AMOUNT_2", title);
            if (_root.Q<G3Button>("IDC_BUTTON_AMOUNT_OK_2") is G3Button ok) ok.text = Text(session, OkText, "OK");
            if (_root.Q<G3Button>("IDC_BUTTON_AMOUNT_CANCEL_2") is G3Button cancel) cancel.text = Text(session, CancelText, "Cancel");
            _slider.lowValue = 1;
            _slider.highValue = Mathf.Max(1, max);
            _slider.SetValueWithoutNotify(Mathf.Max(1, max));
            G3Pages.SetText(_root, "IDC_VALUE_AMOUNT_2", G3Pages.Amount(_slider.value));
            Slot.style.display = DisplayStyle.Flex;
            _slider.Focus();
        }

        /// <summary>Keyboard: ←/→ change the amount, Enter confirms, Escape cancels (handled by the page).</summary>
        public void Step(int delta)
        {
            if (_slider != null && IsOpen) _slider.value = Mathf.Clamp(_slider.value + delta, _slider.lowValue, _slider.highValue);
        }

        /// <summary>Sets the amount within 1..max (dev runner).</summary>
        public void Set(int amount)
        {
            if (_slider != null && IsOpen) _slider.value = Mathf.Clamp(amount, _slider.lowValue, _slider.highValue);
        }

        /// <summary>The amount shown (1 when closed).</summary>
        public int Value => _slider != null && IsOpen ? _slider.value : 1;

        public void Confirm()
        {
            if (!IsOpen) return;
            var done = _done;
            int amount = _slider?.value ?? 1;
            Hide();
            done?.Invoke(amount);
        }

        public void Cancel() => Hide();

        void Hide()
        {
            _done = null;
            if (Slot != null) Slot.style.display = DisplayStyle.None;
        }

        static string Text(GameSession session, TextId id, string fallback) =>
            session.Localizer.TryText(id, out var s) && !string.IsNullOrEmpty(s) ? s : fallback;
    }
}
