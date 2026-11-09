using Myrtana.Core;
using Myrtana.Session;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The inventory (key I or Tab): the hero's items by page, the selected item's description and numbers, gold.
    /// Enter, E or a double click puts an item on or takes it off, or uses food and potions.
    /// </summary>
    public sealed class InventoryScreen : GameScreen
    {
        static readonly TextId Title = new TextId("HUD_SessionKey_InventoryMode"), GoldText = new TextId("HUD_GoldAmount"),
                               EmptyText = new TextId("HUD_NoEntries");

        readonly Label _title, _hint, _gold, _name, _description;
        readonly VisualElement _numbers;
        readonly Image _icon;
        readonly ItemListView _list;
        readonly HudMessages _messages;

        public InventoryScreen(VisualElement hudRoot, HudMessages messages) : base(hudRoot, "InventoryScreen")
        {
            _messages = messages;
            _title = Root.Q<Label>("screen-title");
            _hint = Root.Q<Label>("screen-hint");
            _gold = Root.Q<Label>("gold");
            _name = Root.Q<Label>("item-name");
            _description = Root.Q<Label>("item-description");
            _numbers = Root.Q("item-numbers");
            _icon = Root.Q<Image>("item-icon");
            _list = new ItemListView(Root.Q<ScrollView>("item-list"));
            _list.Selected += ShowDetail;
            _list.Activated += Activate;
        }

        public override void Render()
        {
            var text = Session.Localizer;
            _title.text = text.Text(Title);
            _hint.text = "↑↓    Enter / E    I / Esc";
            _gold.text = $"{text.Text(GoldText)}: {ItemLists.Gold(Session, ActorId.Player)}";
            _list.Show(ItemLists.Of(Session, ActorId.Player), text.Text(EmptyText));
        }

        public override void Tick()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb == null) return;
            if (kb.downArrowKey.wasPressedThisFrame) _list.Move(1);
            if (kb.upArrowKey.wasPressedThisFrame) _list.Move(-1);
            if ((kb.enterKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame) && _list.Current != null) Activate(_list.Current);
        }

        void Activate(ItemRow row) => HeroItemUse.Activate(Session, _messages, row);

        void ShowDetail(ItemRow row)
        {
            _numbers.Clear();
            if (row == null)
            {
                _name.text = _description.text = string.Empty;
                _icon.sprite = null;
                return;
            }
            var (name, description, lines) = ItemLists.Describe(Session, row.Item);
            _name.text = name;
            _description.text = description;
            _icon.sprite = ItemListView.ItemIcon(row.Icon);
            AddSection(_numbers, string.Empty, lines);
        }
    }
}
