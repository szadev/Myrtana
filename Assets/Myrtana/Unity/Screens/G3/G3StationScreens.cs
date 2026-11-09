using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Unity.Ui.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>Messages and lines shared by the station pages and the hero's station use (G3 strings and SVMs).</summary>
    public static class StationTexts
    {
        public static readonly TextId MissingSkill = new TextId("HUD_MissingSkill"), MissingItem = new TextId("HUD_MissingItem_Interact"),
                                      Production = new TextId("HUD_Production"), NoIngredients = new TextId("HUD_NoIngredients"),
                                      AlreadySharp = new TextId("HUD_AlreadySharp"), AlreadyMagic = new TextId("HUD_AlreadyMagic"),
                                      BladeWeapon = new TextId("HUD_BladeWeapon"), Produce = new TextId("HL_Produce"),
                                      Transfer = new TextId("HL_Transfer");

        public const string MissingIngredientsLine = HeroSpokenLines.MissingIngredients, NotEnoughLpLine = HeroSpokenLines.NotEnoughLP;
        public const string SharpenEffect = "eff_inv_sharpen_01";

        /// <summary>The documents tab title of a craft's recipes (also the "missing" text of a station without recipes).</summary>
        public static TextId RecipesTitle(CraftKind craft) =>
            new TextId(craft == CraftKind.Smithing ? "HL_Recipes_SMT" : craft == CraftKind.Cooking ? "HL_Recipes_Cooking" : "HL_Recipes_ALC");

        public static string MissingSkillText(GameSession session, string skill) =>
            session.Localizer.Text(MissingSkill).Replace("$(name)", RecipeSheet.SkillName(session, skill));

        public static string Missing(GameSession session, string what) => session.Localizer.Text(MissingItem) + what;
    }

    /// <summary>
    /// The craft page of the anvil, cauldron and alchemy bench (<c>IDD_PAGE_SYNTH_2</c>, Docs/interactive-objects.md §6):
    /// the carried recipes of the station's craft in the list on the right (icon and name), the chosen recipe in the
    /// recipe view on the left, the close button in the side bar. Double click or Enter makes it: with at least one
    /// possible the amount dialog asks how many (starting at the most), otherwise G3's refusal (missing ingredients /
    /// missing skill) shows. ↑/↓ choose a recipe. [owner] fix-125: recipes the hero cannot make now are dimmed, the
    /// others show how many he can make (<see cref="RecipeRow"/>; G3 draws all rows alike).
    /// </summary>
    public sealed class G3SynthScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_SYNTH_2";
        const int RowHeight = 46;

        readonly GameScreens _screens;
        readonly CraftingHost _crafting;
        readonly HudMessages _messages;
        readonly HeroLines _lines;
        readonly G3ListView _list;
        readonly G3RecipeView _recipe;
        readonly G3AmountView _amount;
        StationRule _station;
        IReadOnlyList<RecipeDef> _recipes = new List<RecipeDef>();
        IReadOnlyList<RecipeRow> _rows = new List<RecipeRow>();

        public G3SynthScreen(VisualElement pagesRoot, GameScreens screens, CraftingHost crafting, HudMessages messages, HeroLines lines)
            : base(pagesRoot, G3Pages.Build(Layout))
        {
            _screens = screens;
            _crafting = crafting;
            _messages = messages;
            _lines = lines;
            _recipe = new G3RecipeView(G3Pages.BuildInto(Root.Q("IDC_VIEW_SYNTH_RECIPE_INNER_2"), "IDD_VIEW_RECIPE_2"));
            _list = StationPages.List(Root, "IDC_LISTCTRL_SYNTH_INVENTORY_2", RowHeight);
            _list.SelectionChanged += _ => ShowRecipe();
            _list.Activated += Produce;
            StationPages.CloseButton(Root, "IDC_VIEW_SYNTH_PAGES_2", () => _screens.CloseCurrent());
            _amount = new G3AmountView(Root.Q("IDC_VIEW_SYNTH_AMOUNT_2"));
        }

        /// <summary>The station whose recipes the page lists (set before opening).</summary>
        public void With(StationRule station) => _station = station;

        /// <summary>G3 pages 8–10: no HUD; the station's own bar (IDD_VIEW_BAR_SYNTH_PAGES_2) replaces the sidebar.</summary>
        public override Myrtana.Ui.Skin.HudPage HudPage => Myrtana.Ui.Skin.HudPage.Exclusive;

        /// <summary>The amount dialog's OK (also for the editor checks).</summary>
        public void ConfirmAmount() => _amount.Confirm();

        public int RecipeCount => _recipes.Count;

        /// <summary>The listed rows (craftable state and count), for the editor checks.</summary>
        public IReadOnlyList<RecipeRow> RecipeRows => _rows;

        /// <summary>The list control (row looks, scroll position), for the editor checks.</summary>
        public G3ListView List => _list;

        protected override void OnOpen()
        {
            _amount.Cancel();
            _list.Select(0, notify: false);
        }

        protected override void OnClose() => _amount.Cancel();

        /// <summary>G3 runs the amount dialog as a modal loop: Escape (IDCANCEL) closes only the dialog.</summary>
        public override bool HandleEscape()
        {
            if (!_amount.IsOpen) return false;
            _amount.Cancel();
            return true;
        }

        public override void Render()
        {
            var services = _crafting.Services;
            if (_station == null || services == null) return;
            G3Pages.SetText(Root, "IDC_LABEL_SYNTH_2", _station.Title.IsEmpty ? string.Empty : Session.Localizer.Text(_station.Title));
            _rows = RecipeRow.Of(Session, services.Crafting, _station.Craft);
            _recipes = _rows.Select(r => r.Recipe).ToList();
            int keep = _list.SelectedIndex;
            _list.SetItems(_rows.Select(r => r.Name).ToList());
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _list.Rows[i];
                row.SetIcon(_rows[i].Icon);
                row.SetCount(_rows[i].CountText);
                row.Unavailable = !_rows[i].Craftable;
            }
            _list.Select(keep >= 0 && keep < _recipes.Count ? keep : _recipes.Count > 0 ? 0 : -1, notify: false);
            ShowRecipe();
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Station;

        public override void Tick()
        {
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (d-pad, A = Enter)
            if (_amount.IsOpen)
            {
                if (keys.Left) _amount.Step(-1);
                if (keys.Right) _amount.Step(1);
                if (keys.Enter) _amount.Confirm();
                return;
            }
            int s = _list.SelectedIndex;
            if (keys.Up) _list.Select(Mathf.Max(0, s - 1));
            if (keys.Down) _list.Select(Mathf.Min(_recipes.Count - 1, s + 1));
            if (keys.Enter) Produce(_list.SelectedIndex);
        }

        void ShowRecipe()
        {
            int i = _list.SelectedIndex;
            var services = _crafting.Services;
            _recipe.Bind(services != null && i >= 0 && i < _recipes.Count ? RecipeSheet.Of(Session, services.Crafting, _recipes[i]) : null);
        }

        /// <summary>G3 <c>gCHUDPageSynth2</c> → <c>OnPlayerSynthRecipe</c>.</summary>
        public void Produce(int index)
        {
            var services = _crafting.Services;
            if (services == null || index < 0 || index >= _recipes.Count || _amount.IsOpen) return;
            var recipe = _recipes[index];
            int max = services.Crafting.MaxCount(recipe);
            if (max >= 1) _amount.Ask(Session, Session.Localizer.Text(StationTexts.Produce), max, n => Make(recipe, n));
            else Make(recipe, max);
        }

        void Make(RecipeDef recipe, int count)
        {
            var session = Session;
            var services = _crafting.Services;
            if (session == null || services == null) return;
            int made = 0;
            var verdict = count < 0 ? CraftVerdict.MissingSkill : services.Crafting.Craft(recipe.Id, count, out made);
            switch (verdict)
            {
                case CraftVerdict.Done:
                    _messages.Show(session.Localizer.Text(StationTexts.Production)
                                          .Replace("$(name)", RecipeSheet.ItemName(session, recipe.Result))
                                          .Replace("$(value)", made.ToString()), NoticeKind.Result);
                    return;
                case CraftVerdict.MissingSkill:
                    _messages.Show(StationTexts.MissingSkillText(session, recipe.RequiredSkill), NoticeKind.Error);
                    _lines?.Say(session, StationTexts.NotEnoughLpLine);
                    return;
                default:
                    _messages.Show(session.Localizer.Text(StationTexts.NoIngredients), NoticeKind.Error);
                    _lines?.Say(session, StationTexts.MissingIngredientsLine);
                    return;
            }
        }
    }

    /// <summary>
    /// The transform page (<c>IDD_PAGE_TRANSFORM_2</c>): the grindstone lists the hero's blade weapons and sharpens a whole
    /// stack, the campfire lists raw meat and fries the chosen amount (amount dialog when the stack has more than one).
    /// Item panel in the middle, close button in the side bar; double click or Enter transforms.
    /// </summary>
    public sealed partial class G3TransformScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_TRANSFORM_2";
        const int RowHeight = 46;

        readonly GameScreens _screens;
        readonly CraftingHost _crafting;
        readonly HudMessages _messages;
        readonly G3ListView _list;
        readonly G3ItemPanel _panel;
        readonly G3AmountView _amount;
        StationRule _station;
        string _treatWith; // blessing or poisoning from the inventory: the scroll, spell or potion (magic-3)
        IReadOnlyList<ItemStack> _stacks = new List<ItemStack>();

        /// <summary>The page the inventory opens for blessing and poisoning (G3 GUI2::OpenBless / OpenPoison, pages 11/12).</summary>
        public static G3TransformScreen Current { get; private set; }

        public G3TransformScreen(VisualElement pagesRoot, GameScreens screens, CraftingHost crafting, HudMessages messages)
            : base(pagesRoot, G3Pages.Build(Layout))
        {
            _screens = screens;
            _crafting = crafting;
            _messages = messages;
            _panel = G3Pages.ItemPanel(Root, "IDC_VIEW_TRANSFORM_ITEM_2");
            _list = StationPages.List(Root, "IDC_LISTCTRL_TRANSFORM_2", RowHeight);
            _list.SelectionChanged += _ => ShowPanel();
            _list.Activated += Transform;
            StationPages.CloseButton(Root, "IDC_VIEW_TRANSFORM_PAGES_2", () => _screens.CloseCurrent());
            _amount = new G3AmountView(Root.Q("IDC_VIEW_TRANSFORM_AMOUNT_2"));
            Current = this;
        }

        /// <summary>G3 pages 11–13: no HUD; page 14 (frying pan) keeps the play HUD. Neither shows the page sidebar.</summary>
        public override Myrtana.Ui.Skin.HudPage HudPage =>
            _station != null && _station.Page == StationPage.Fry ? Myrtana.Ui.Skin.HudPage.Fry : Myrtana.Ui.Skin.HudPage.Exclusive;

        public void With(StationRule station)
        {
            _station = station;
            _treatWith = null;
        }

        /// <summary>
        /// G3 OnPlayerUseStack, UseType Modify (Docs/combat.md §12.5): a bless scroll or the bless spell opens the bless page,
        /// a poison potion the poison page (needs Perk_PoisonBlade: HUD_MissingSkill). False when the item opens nothing.
        /// The page is closed at that point, so its own <see cref="GameScreen.Session"/> is not bound yet: the caller passes
        /// the running session.
        /// </summary>
        public bool OpenTreatment(GameSession session, string item)
        {
            var weapons = session?.SpellEffects?.Weapons;
            if (weapons == null || WeaponTreatments.KindOf(item) == TreatmentKind.None) return false;
            var verdict = weapons.CanOpen(item);
            if (verdict == TreatmentVerdict.MissingSkill)
            {
                _messages.Show(StationTexts.MissingSkillText(session, SpellEffectRules.PoisonPerk), NoticeKind.Error);
                return true;
            }
            if (verdict != TreatmentVerdict.Done) return false;
            _station = null;
            _treatWith = item;
            _screens.Open(this);
            return true;
        }

        /// <summary>The amount dialog's OK (also for the editor checks).</summary>
        public void ConfirmAmount() => _amount.Confirm();

        public int StackCount => _stacks.Count;

        protected override void OnOpen()
        {
            _amount.Cancel();
            _list.Select(0, notify: false);
        }

        protected override void OnClose() => _amount.Cancel();

        /// <summary>G3 runs the amount dialog as a modal loop: Escape (IDCANCEL) closes only the dialog.</summary>
        public override bool HandleEscape()
        {
            if (!_amount.IsOpen) return false;
            _amount.Cancel();
            return true;
        }

        public override void Render()
        {
            var services = _crafting.Services;
            if (_treatWith != null)
            {
                G3Pages.SetText(Root, "IDC_LABEL_TRANSFORM_2", RecipeSheet.ItemName(Session, _treatWith));
                _stacks = Session.SpellEffects.Weapons.Candidates(WeaponTreatments.KindOf(_treatWith));
            }
            else if (_station == null || services == null) return;
            else
            {
                G3Pages.SetText(Root, "IDC_LABEL_TRANSFORM_2", _station.Title.IsEmpty ? string.Empty : Session.Localizer.Text(_station.Title));
                _stacks = _station.Page == StationPage.Fry ? services.Crafting.FryCandidates() : services.Crafting.SharpenCandidates();
            }
            int keep = _list.SelectedIndex;
            _list.SetItems(_stacks.Select(s => s.Amount > 1 ? $"{RecipeSheet.ItemName(Session, s.Item)} ({s.Amount})" : RecipeSheet.ItemName(Session, s.Item)).ToList());
            for (int i = 0; i < _stacks.Count; i++) _list.Rows[i].SetIcon(StationPages.Icon(Session, _stacks[i].Item));
            _list.Select(keep >= 0 && keep < _stacks.Count ? keep : _stacks.Count > 0 ? 0 : -1, notify: false);
            ShowPanel();
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Station;

        public override void Tick()
        {
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (d-pad, A = Enter)
            if (_amount.IsOpen)
            {
                if (keys.Left) _amount.Step(-1);
                if (keys.Right) _amount.Step(1);
                if (keys.Enter) _amount.Confirm();
                return;
            }
            int s = _list.SelectedIndex;
            if (keys.Up) _list.Select(Mathf.Max(0, s - 1));
            if (keys.Down) _list.Select(Mathf.Min(_stacks.Count - 1, s + 1));
            if (keys.Enter) Transform(_list.SelectedIndex);
        }

        void ShowPanel()
        {
            int i = _list.SelectedIndex;
            _panel.Bind(i >= 0 && i < _stacks.Count ? G3Pages.Panel(ItemLists.Detail(Session, _stacks[i].Item)) : null);
        }

        /// <summary>G3 <c>OnPlayerTransformStack</c> / <c>OnPlayerFryStack</c>.</summary>
        public void Transform(int index)
        {
            if (_treatWith != null)
            {
                if (index >= 0 && index < _stacks.Count) Treat(_stacks[index]);
                return;
            }
            var services = _crafting.Services;
            if (services == null || index < 0 || index >= _stacks.Count || _amount.IsOpen) return;
            var stack = _stacks[index];
            if (_station.Page == StationPage.Fry)
            {
                if (stack.Amount > 1)
                    _amount.Ask(Session, Session.Localizer.Text(StationTexts.Transfer), stack.Amount, n => services.Crafting.Fry(stack.Item, n, out _));
                else services.Crafting.Fry(stack.Item, 1, out _);
                return;
            }
            var verdict = services.Crafting.Sharpen(stack);
            var text = Session.Localizer;
            switch (verdict)
            {
                case TransformVerdict.Done:
                    var camera = Camera.main;
                    G3.Unity.G3EffectSounds.Play(StationTexts.SharpenEffect, camera != null ? camera.transform.position : Vector3.zero);
                    break;
                case TransformVerdict.AlreadySharp: _messages.Show(text.Text(StationTexts.AlreadySharp), NoticeKind.Error); break;
                case TransformVerdict.AlreadyMagic: _messages.Show(text.Text(StationTexts.AlreadyMagic), NoticeKind.Error); break;
                case TransformVerdict.MissingSkill:
                    _messages.Show(StationTexts.MissingSkillText(Session, _station.RequiredSkill), NoticeKind.Error);
                    break;
            }
        }
    }

    public sealed partial class G3TransformScreen
    {
        /// <summary>
        /// The bless/poison worker (G3 FUN_10068040 with bit 0x40 / 4): one weapon (arrows: a batch) gets the quality; the
        /// refusals are HUD_AlreadyMagic, HUD_AlreadyBlessed, HUD_AlreadyPoisoned and, for the spell, HUD_MissingAttrib (MP);
        /// the page closes when the last scroll or potion is used up.
        /// </summary>
        void Treat(ItemStack stack)
        {
            var weapons = Session.SpellEffects.Weapons;
            var verdict = weapons.Apply(_treatWith, stack, out _);
            var text = Session.Localizer;
            switch (verdict)
            {
                case TreatmentVerdict.Done:
                    var effect = WeaponTreatments.KindOf(_treatWith) == TreatmentKind.Bless ? SpellEffectRules.BlessEffect : SpellEffectRules.PoisonEffect;
                    var camera = Camera.main;
                    G3.Unity.G3EffectSounds.Play(effect, camera != null ? camera.transform.position : Vector3.zero);
                    if (!Session.Items.Inventories.Of(ActorId.Player).Has(_treatWith)) _screens.CloseCurrent();
                    break;
                case TreatmentVerdict.AlreadyMagic: _messages.Show(text.Text(StationTexts.AlreadyMagic), NoticeKind.Error); break;
                case TreatmentVerdict.AlreadyBlessed: _messages.Show(text.Text(new TextId("HUD_AlreadyBlessed")), NoticeKind.Error); break;
                case TreatmentVerdict.AlreadyPoisoned: _messages.Show(text.Text(new TextId("HUD_AlreadyPoisoned")), NoticeKind.Error); break;
                case TreatmentVerdict.MissingMana:
                    int lacking = SpellEffectRules.BlessMana - Session.Combat.Vitals.Mana(ActorId.Player);
                    string mp = Session.Content.Database.TryGet("MP", out AttributeDef a) ? text.Text(a.Name) : "MP";
                    _messages.Show(text.Text(new TextId("HUD_MissingAttrib")).Replace("$(value)", lacking.ToString()).Replace("$(name)", mp), NoticeKind.Error);
                    break;
            }
            if (verdict == TreatmentVerdict.AlreadyMagic || verdict == TreatmentVerdict.AlreadyBlessed || verdict == TreatmentVerdict.AlreadyPoisoned)
                Session.Events.Publish(new CombatShout(ActorId.Player, "DoesntWork")); // StartSayDoesntWork
            Render();
        }
    }

    /// <summary>Layout helpers of the station pages.</summary>
    static class StationPages
    {
        /// <summary>G3 <c>GUI.ListCtrl.LargeIcon.Spacing</c> 48 − item height 46.</summary>
        public const int RowGap = 2;

        public static G3ListView List(VisualElement root, string slotName, int rowHeight)
        {
            // G3 LargeIcon view: 238×46 items at a 48 px pitch; the 531 px list shows 11 whole rows (fix-125: no half row)
            var list = new G3ListView
            {
                ActivateOnSingleClick = false, RowHeight = rowHeight, RowSpacing = RowGap, SnapToRows = true, AlwaysShowScrollBar = true,
            };
            list.AddToClassList("g3-fill-parent");
            var slot = root.Q(slotName);
            if (slot != null)
            {
                slot.pickingMode = PickingMode.Position;
                slot.Add(list);
            }
            return list;
        }

        /// <summary>The side bar with its close button (<c>IDD_VIEW_BAR_SYNTH_PAGES_2</c>).</summary>
        public static void CloseButton(VisualElement root, string slotName, System.Action close)
        {
            var slot = root.Q(slotName);
            var bar = slot?.Q("IDC_BUTTON_SYNTH_CLOSE_2") != null ? slot : G3Pages.BuildInto(slot, "IDD_VIEW_BAR_SYNTH_PAGES_2");
            if (slot != null) slot.pickingMode = PickingMode.Position;
            if (bar?.Q<G3Button>("IDC_BUTTON_SYNTH_CLOSE_2") is G3Button button) button.clicked += close;
        }

        public static string Icon(GameSession session, string item) =>
            session.Content.Database.TryGet(item, out ItemDef def) && !string.IsNullOrEmpty(def.Icon) ? def.Icon : null;
    }
}
