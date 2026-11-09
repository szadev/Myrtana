using G3.Player;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Myrtana.Unity
{
    /// <summary>The quick-slot keys: the session keys QuickUse1…9 → slots 0…8, QuickUse0 → slot 9 (G3 default: the number row).</summary>
    public static class QuickSlotKeys
    {
        /// <summary>The slot whose key went down this frame, or −1 (the keyboard argument is kept for the callers).</summary>
        public static int Pressed(Keyboard kb)
        {
            for (int digit = 0; digit < QuickSlots.Count; digit++)
                if (G3Input.WasPressed(KeyMap.QuickUse(digit))) return QuickSlots.SlotOfDigit(digit);
            return -1;
        }
    }

    /// <summary>
    /// Uses the quick slots from the number keys while the hero moves freely (no page, menu or conversation); on the
    /// inventory page the keys assign slots instead (<see cref="G3InventoryScreen"/>). Also the CP potion keys
    /// (<see cref="PotionQuickUse"/>: Num 7 / 8 / 9 drink a healing, mana or endurance potion).
    /// </summary>
    public sealed class QuickSlotInput : MonoBehaviour, ISessionView
    {
        GameSession _session;
        HeroControlLock _controls;
        HudMessages _messages;

        public void Init(HeroControlLock controls, HudMessages messages)
        {
            _controls = controls;
            _messages = messages;
        }

        public void Bind(GameSession session) => _session = session;

        /// <summary>Readies weapons and spells in combat (the hero's <see cref="PlayerCombat"/>); null = slots only put items on.</summary>
        public IQuickSlotReadiness Combat { get; set; }

        /// <summary>
        /// gamepad-1: the pad's cycling quick bar (Docs/gamepad.md §3): LB/RB move this highlight over the filled slots,
        /// d-pad up uses the highlighted one. The HUD draws it while the pad is the device in use.
        /// </summary>
        public static readonly QuickSlotCursor PadCursor = new QuickSlotCursor(QuickSlots.Count);

        void Update()
        {
            int slot = QuickSlotKeys.Pressed(global::G3.Unity.G3Input.GameKeyboard);
            if (slot >= 0) Use(slot);
            var pad = G3Pad.Resolver;
            if (_session != null && !_session.Dialog.IsActive)
            {
                var slots = _session.Items.QuickSlots;
                if (pad.WasPressed(PadAction.QuickSlotNext)) PadCursor.Move(1, s => slots.ItemAt(s) != null);
                if (pad.WasPressed(PadAction.QuickSlotPrev)) PadCursor.Move(-1, s => slots.ItemAt(s) != null);
            }
            if (pad.WasPressed(PadAction.QuickSlotUse)) Use(PadCursor.Slot);
            if (G3Input.WasPressed(SessionKey.UsePotionHP)) DrinkPotion(QuickPotion.Health);
            if (G3Input.WasPressed(SessionKey.UsePotionMP)) DrinkPotion(QuickPotion.Mana);
            if (G3Input.WasPressed(SessionKey.UsePotionSP)) DrinkPotion(QuickPotion.Stamina);
        }

        /// <summary>What the slot's key does (also the entry point for tests that cannot press keys).</summary>
        public void Use(int slot)
        {
            if (_session == null || _session.Dialog.IsActive || _controls.IsLocked) return;
            string item = _session.Items.QuickSlots.ItemAt(slot);
            if (Combat != null && Combat.TryReady(item)) return;
            HeroItemUse.UseQuickSlot(_session, _messages, item);
        }

        /// <summary>A CP potion key (also the entry point for tests): drinks the small potion of that kind if carried.</summary>
        public void DrinkPotion(QuickPotion potion)
        {
            if (_session == null || _session.Dialog.IsActive || _controls.IsLocked) return;
            bool transformed = _session.SpellEffects.Hero.Kind != HeroFormKind.Hero;
            var inventory = _session.Items.Inventories.Of(ActorId.Player);
            string item = PotionQuickUse.ItemToDrink(potion, transformed, inventory.Count);
            if (item != null) HeroItemUse.UseQuickSlot(_session, _messages, item);
        }
    }
}
