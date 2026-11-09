using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The spell book in the original look (<c>IDD_PAGE_SPELLS_2</c> → <c>IDD_VIEW_SPELLS_2</c>, key B, the third sidebar tab;
    /// Docs/formats-gui.md §6.8): three 6×3 tile grids — Innos, Adanos, Beliar (<see cref="SpellBook"/>) — with one selection
    /// across them, and the spell box <c>IDD_VIEW_SPELL_2</c> (name, description, requirements and mana). Unlearned spells are
    /// grey. As on the inventory page (gCHUDListCtrlInventory → OnPlayerAssignStackKey) a number key or a drag onto the quick
    /// bar puts the selected spell into a quick slot and Backspace takes it off; only learned spells go there. Enter or a
    /// double click readies a learned spell and closes the page (OnPlayerUseStack: close page, PS_Magic); an unlearned one
    /// is refused (eff_inv_deny_01). The spell in the hand is tinted blue (<see cref="EquippedMarker"/>).
    /// </summary>
    public sealed class G3SpellBookScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_SPELLS_2", ViewLayout = "IDD_VIEW_SPELLS_2";
        const int HeaderFont = 24;

        /// <summary>Grid and header controls in page order → deity and title string.</summary>
        static readonly (string control, SpellDeity deity, TextId title)[] Lists =
        {
            ("INNOS", SpellDeity.Innos, new TextId("HL_Innos")),
            ("ADANOS", SpellDeity.Adanos, new TextId("HL_Adanos")),
            ("BELIAR", SpellDeity.Beliar, new TextId("HL_Beliar")),
        };

        readonly GameScreens _screens;
        readonly VisualElement _view;
        readonly List<(G3ItemGrid grid, SpellDeity deity)> _grids = new List<(G3ItemGrid, SpellDeity)>();
        readonly VisualElement _spellBox;
        SpellBook _book;
        (int grid, int index) _selected = (-1, -1);

        public G3SpellBookScreen(VisualElement pagesRoot, GameScreens screens) : base(pagesRoot, G3Pages.Build(Layout))
        {
            _screens = screens;
            _view = Root.Q(ViewLayout) ?? G3Pages.BuildInto(Root.Q("IDC_STATIC_VIEW_SPELLS_2"), ViewLayout) ?? Root;
            foreach (var (control, deity, _) in Lists)
            {
                var grid = G3Pages.Grid(_view, "IDC_LISTCTRL_" + control + "_2", scrollable: false);
                if (grid == null) continue;
                int gridIndex = _grids.Count;
                grid.SelectionChanged += index => Select(gridIndex, index);
                grid.Activated += index => Activate();
                grid.DragEnabled = true;
                grid.DropRole = "spells";
                grid.Dropped += drop =>
                {
                    if (Session == null || drop.Target?.DropRole != G3InventoryScreen.QuickBarRole || drop.TargetIndex < 0) return;
                    var spell = SpellAt(gridIndex, drop.SourceIndex);
                    if (spell != null) Session.Items.QuickSlots.Assign(drop.TargetIndex, spell.Item);
                };
                _grids.Add((grid, deity));
            }
            _spellBox = _view.Q("IDC_VIEW_SPELL_2");
        }

        /// <summary>Readies the spell in the hand (the hero's <see cref="PlayerCombat"/>); null = the page only shows spells.</summary>
        public IQuickSlotReadiness Combat { get; set; }

        protected override void OnOpen() => _selected = (-1, -1);

        public override void Render()
        {
            var text = Session.Localizer;
            _book = SpellBook.Of(Session);
            foreach (var (control, _, title) in Lists) G3Pages.SetText(_view, "IDC_LABEL_" + control + "_2", text.Text(title));
            _view.schedule.Execute(() =>
            {
                foreach (var (control, _, _) in Lists)
                    if (_view.Q("IDC_LABEL_" + control + "_2") is Label header && header.userData is WindowData w)
                        G3Text.FitWidth(header, w.Width, HeaderFont);
            });
            var slots = Session.Items.QuickSlots;
            var hands = Session.Items.Hands;
            for (int i = 0; i < _grids.Count; i++)
            {
                var (grid, deity) = _grids[i];
                G3Pages.Localize(grid, Session);
                grid.SetItems(_book.Of(deity).Select(s => new ItemTileModel
                {
                    Texture = s.Icon, Category = ItemIconCategory.Spellbook, Learned = s.Coloured,
                    QuickSlot = slots.SlotOf(s.Item), Equipped = EquippedMarker.IsMarked(s.Item, (System.Func<string, bool>)null, hands),
                }).ToList());
                grid.Select(i == _selected.grid ? _selected.index : -1, notify: false);
            }
            ShowSpell();
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.SpellBook;

        public override void Tick()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (LT/RT or LB/RB = the grids, A = Enter, X/Y = quick slot)
            if (_grids.Count == 0) return;
            int g = _selected.grid < 0 ? 0 : _selected.grid;
            var grid = _grids[g].grid;
            int s = _selected.grid < 0 ? -1 : _selected.index;
            if (keys.GridMove(s, grid.Count, grid.Columns) is int moved) grid.Select(moved);
            if (keys.PageNext || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabNext)) _grids[(g + 1) % _grids.Count].grid.Select(0);
            if (keys.PagePrev || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabPrev)) _grids[(g + _grids.Count - 1) % _grids.Count].grid.Select(0);
            if (keys.Enter || keys.Key(Key.E))
            {
                Activate(); // may close the page
                return;
            }
            int slot = QuickSlotKeys.Pressed(kb);
            if (slot >= 0) AssignQuickSlot(slot);
            if (keys.Key(Key.Backspace) || keys.PadTertiary) ClearQuickSlot();
            if (keys.PadSecondary) AssignQuickSlot(QuickSlotInput.PadCursor.Slot);
        }

        /// <summary>The selected spell, or null.</summary>
        public SpellBook.Spell Selected => SpellAt(_selected.grid, _selected.index);

        /// <summary>A number key puts the selected spell into that quick slot (learned spells only).</summary>
        public bool AssignQuickSlot(int slot)
        {
            var spell = Selected;
            return spell != null && Session != null && Session.Items.QuickSlots.Assign(slot, spell.Item);
        }

        /// <summary>Backspace takes the selected spell off the quick bar.</summary>
        public void ClearQuickSlot()
        {
            var spell = Selected;
            if (spell == null || Session == null) return;
            int slot = Session.Items.QuickSlots.SlotOf(spell.Item);
            if (slot >= 0) Session.Items.QuickSlots.Clear(slot);
        }

        /// <summary>Selects a spell by its item or spell id (dev scenarios); false when the book has no such spell.</summary>
        public bool SelectSpell(string itemOrSpell)
        {
            if (_book == null) return false;
            for (int g = 0; g < _grids.Count; g++)
            {
                var list = _book.Of(_grids[g].deity);
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Item == itemOrSpell || list[i].Id == itemOrSpell)
                    {
                        _grids[g].grid.Select(i);
                        return true;
                    }
            }
            return false;
        }

        /// <summary>Enter / double click: a learned spell is readied and the page closes; an unlearned one is refused.</summary>
        public bool Activate()
        {
            var spell = Selected;
            if (spell == null || Session == null) return false;
            if (!spell.Learned || Combat == null || !Session.HeroSpells.CanReady(spell.Item))
            {
                var camera = Camera.main;
                G3.Unity.G3EffectSounds.Play(HeroItemUse.DenyEffect, camera != null ? camera.transform.position : Vector3.zero);
                return false;
            }
            var combat = Combat;
            string item = spell.Item;
            _screens?.CloseCurrent(); // G3 OnPlayerUseStack: ClosePage, then PS_Magic
            return combat.TryReady(item);
        }

        SpellBook.Spell SpellAt(int grid, int index)
        {
            if (_book == null || grid < 0 || grid >= _grids.Count || index < 0) return null;
            var list = _book.Of(_grids[grid].deity);
            return index < list.Count ? list[index] : null;
        }

        void Select(int grid, int index)
        {
            if (index < 0 && grid != _selected.grid) return;
            _selected = (grid, index);
            for (int i = 0; i < _grids.Count; i++)
                if (i != grid) _grids[i].grid.Select(-1, notify: false);
            ShowSpell();
        }

        void ShowSpell()
        {
            var spell = Selected;
            if (_spellBox == null) return;
            _spellBox.style.visibility = spell == null ? Visibility.Hidden : Visibility.Visible;
            if (spell == null) return;
            G3Pages.SetText(_spellBox, "IDC_LABEL_SPELL_2", spell.Name);
            G3Pages.SetText(_spellBox, "IDC_LABEL_SPELL_DESC_2", spell.Description);
            G3Pages.SetText(_spellBox, "IDC_LABEL_SPELL_REQ_2", string.Join("\n", spell.Requirements.Select(r => $"{r.Label}: {r.Value}")));
        }
    }
}
