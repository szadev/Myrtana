using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    public enum TorchTakeResult { Taken, AlreadyHeld, NotATorch, NotCarried, HandsBusy, Swimming }

    /// <summary>What an interaction does to a torch in the hand (G3 _AI_HoldInventoryItems for the player).</summary>
    public enum TorchInteraction { KeepInHand, Stow, Release }

    /// <summary>The hero took a torch into the left hand or it left the hand (back into the inventory).</summary>
    public readonly struct TorchChanged : IGameEvent
    {
        public TorchChanged(string item, bool held)
        {
            Item = item;
            Held = held;
        }

        public string Item { get; }
        public bool Held { get; }
    }

    /// <summary>
    /// The hero's burning torch (G3 CP 1.75 scripts OnPlayerUseStack, OnPlayerSelectWeapon, PS_HUD_Equip/UnEquip,
    /// _AI_HoldInventoryItems, PS_Swim; read for the rules only, Docs/formats-effects.md §8):
    /// * Using a torch item (use type Torch: It_Torch_Burning, quick slot 2 of the start kit) takes it into the left hand;
    ///   using it again puts it away. Torches never burn down (no timer exists).
    /// * <b>A torch that leaves the hand goes back into the inventory</b> (HoldStacks(-1, …) / the left equip slot set to
    ///   None): it is never deleted, dropped or used up — put away, replaced by another torch, let go in deep water
    ///   (PS_Swim empties both hands) or for an interaction.
    /// * It cannot be taken while a two-handed weapon (2H, axe, halberd) is drawn, nor while a shield or a second 1H weapon
    ///   is in the left hand; it stays with a drawn 1H weapon (the Torch_1H motion set) and with fists [interp: no
    ///   Torch_Fist motions; the rule does not release it]. Drawing a bow/crossbow, staff, two-handed weapon or spell
    ///   releases it (OnPlayerSelectWeapon empties the left hand for 2H/staff/axe/halberd, ranged weapons and spells).
    ///   Holstering a melee weapon takes the torch back if it was in the left hand before the draw.
    /// * Interactions that empty the hands (PS_Interact_* of anvils, barbecues, camp fires, carried stones, pirate
    ///   treasure, benches, stools, thrones, beds, sleeping on the ground, orc boulders, water pipes) stow it when the
    ///   object's name contains Bed, Campfire, Alchem, GrindStone, Anvil or Cauldron (it comes back afterwards) and
    ///   release it into the inventory otherwise; other objects (chests, doors, levers, books) leave it in the hand.
    /// * NPCs never carry torches in G3 (no NPC inventory holds one, no script or routine takes one, only the Hero rig
    ///   has Torch motions).
    /// </summary>
    public sealed class HeroTorch
    {
        public const string TorchKind = "Torch";

        static readonly string[] EmptyHandUses =
        {
            "Anvil", "Barbecue", "Campfire", "SitCampfire", "TakeStone", "DropStone", "CarryFront", "CarryShoulder", "PirateTreasure",
            "Bench", "Stool", "Throne", "Bed", "SleepGround", "OrcBoulder", "Waterpipe",
        };

        static readonly string[] KeepingNames = { "Bed", "Campfire", "Alchem", "GrindStone", "Anvil", "Cauldron" };

        Inventory _inventory;
        Func<string, ItemDef> _items;
        IEventBus _events;

        /// <summary>The torch item in the left hand (also while stowed for an interaction); null = none.</summary>
        public string Item { get; private set; }
        /// <summary>Put aside for an interaction; comes back with <see cref="EndInteraction"/>.</summary>
        public bool Stowed { get; private set; }
        /// <summary>A torch is shown in the left hand.</summary>
        public bool InHand => Item != null && !Stowed;
        /// <summary>The torch released by drawing a melee weapon; taken back when that weapon is holstered.</summary>
        public string HeldBeforeDraw { get; private set; }

        /// <summary>Connects to a session (new game or load); <paramref name="restored"/> = the saved torch, if any.</summary>
        public void Bind(Inventory inventory, Func<string, ItemDef> items, IEventBus events, string restored)
        {
            if (_inventory != null) _inventory.Changed -= OnInventoryChanged;
            _inventory = inventory;
            _items = items;
            _events = events;
            Item = null;
            Stowed = false;
            HeldBeforeDraw = null;
            if (_inventory != null) _inventory.Changed += OnInventoryChanged;
            if (restored != null && IsTorch(restored) && _inventory != null && _inventory.Has(restored)) Item = restored;
        }

        public bool IsTorch(string item)
        {
            var def = item != null ? _items?.Invoke(item) : null;
            return def != null && string.Equals(def.Kind, TorchKind, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether the drawn weapon (motion field "1H", "2H", "Staff", "Fist", "Bow", "CrossBow", "Cast"; null = none) lets the left hand hold a torch.</summary>
        public static bool WeaponAllowsTorch(string weaponField) =>
            weaponField == null || weaponField == "1H" || weaponField == "Fist";

        /// <summary>Melee weapon fields: holstering one takes back the torch its draw released.</summary>
        public static bool IsMelee(string weaponField) =>
            weaponField == "1H" || weaponField == "2H" || weaponField == "Staff" || weaponField == "Fist";

        /// <summary>Why the torch cannot be taken now, or <see cref="TorchTakeResult.Taken"/>.</summary>
        public TorchTakeResult Check(string item, string drawnWeaponField, bool leftHandBusy, bool swimming)
        {
            if (!IsTorch(item)) return TorchTakeResult.NotATorch;
            if (_inventory == null || !_inventory.Has(item)) return TorchTakeResult.NotCarried;
            if (InHand) return TorchTakeResult.AlreadyHeld;
            if (swimming) return TorchTakeResult.Swimming;
            if (leftHandBusy || !WeaponAllowsTorch(drawnWeaponField)) return TorchTakeResult.HandsBusy;
            return TorchTakeResult.Taken;
        }

        public TorchTakeResult Take(string item, string drawnWeaponField = null, bool leftHandBusy = false, bool swimming = false)
        {
            var r = Check(item, drawnWeaponField, leftHandBusy, swimming);
            if (r != TorchTakeResult.Taken) return r;
            Item = item;
            Stowed = false;
            HeldBeforeDraw = null;
            _events?.Publish(new TorchChanged(item, true));
            return r;
        }

        /// <summary>The torch leaves the hand and goes back into the inventory (put away, another torch, water,
        /// interaction); a pending re-hold is forgotten. False when none was held.</summary>
        public bool Release()
        {
            HeldBeforeDraw = null;
            return ReleaseHand();
        }

        bool ReleaseHand()
        {
            if (Item == null) return false;
            string item = Item;
            Item = null;
            Stowed = false;
            _events?.Publish(new TorchChanged(item, false));
            return true;
        }

        /// <summary>A weapon is drawn: the torch goes back into the inventory unless the weapon goes with it
        /// (<paramref name="leftHandBusy"/>: a shield comes into the left hand). A melee draw remembers it for the
        /// holster. True when it went.</summary>
        public bool OnWeaponDrawn(string weaponField, bool leftHandBusy = false)
        {
            if (!InHand)
            {
                if (weaponField != null && !IsMelee(weaponField)) HeldBeforeDraw = null; // bow or spell next: no re-hold
                return false;
            }
            if (WeaponAllowsTorch(weaponField) && !leftHandBusy) return false;
            string item = Item;
            ReleaseHand();
            HeldBeforeDraw = IsMelee(weaponField) ? item : null;
            return true;
        }

        /// <summary>The weapon of <paramref name="weaponField"/> was holstered: a torch released by a melee draw is
        /// taken back (if still carried and the hero does not swim). True when it came back.</summary>
        public bool OnWeaponHolstered(string weaponField, bool swimming = false)
        {
            string item = HeldBeforeDraw;
            HeldBeforeDraw = null;
            if (item == null || !IsMelee(weaponField)) return false;
            return Take(item, null, false, swimming) == TorchTakeResult.Taken;
        }

        /// <summary>What using an object of <paramref name="useType"/> named <paramref name="objectName"/> does to a held torch.</summary>
        public static TorchInteraction ForInteraction(string useType, string objectName)
        {
            if (!Contains(EmptyHandUses, useType, exact: true)) return TorchInteraction.KeepInHand;
            return Contains(KeepingNames, objectName, exact: false) ? TorchInteraction.Stow : TorchInteraction.Release;
        }

        /// <summary>An interaction starts: the torch stays, is stowed, or goes back into the inventory. Returns what happened.</summary>
        public TorchInteraction BeginInteraction(string useType, string objectName)
        {
            if (!InHand) return TorchInteraction.KeepInHand;
            var r = ForInteraction(useType, objectName);
            if (r == TorchInteraction.Release) Release();
            else if (r == TorchInteraction.Stow)
            {
                Stowed = true;
                _events?.Publish(new TorchChanged(Item, false));
            }
            return r;
        }

        /// <summary>The interaction ended: a stowed torch is taken back (PS_Interact_End holds the items again).</summary>
        public void EndInteraction()
        {
            if (!Stowed || Item == null) return;
            Stowed = false;
            if (_inventory != null && !_inventory.Has(Item))
            {
                Item = null;
                return;
            }
            _events?.Publish(new TorchChanged(Item, true));
        }

        /// <summary>The hero no longer carries the held torch (sold, dropped): it leaves the hand.</summary>
        void OnInventoryChanged()
        {
            if (HeldBeforeDraw != null && !_inventory.Has(HeldBeforeDraw)) HeldBeforeDraw = null;
            if (Item == null || _inventory.Has(Item)) return;
            string item = Item;
            Item = null;
            Stowed = false;
            _events?.Publish(new TorchChanged(item, false));
        }

        static bool Contains(string[] list, string value, bool exact)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var s in list)
                if (exact ? string.Equals(s, value, StringComparison.OrdinalIgnoreCase)
                          : value.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    /// <summary>What the torch view does with a torch released by a melee draw once the weapon leaves the stance (<see cref="TorchHolsterRules.Decide"/>).</summary>
    public enum TorchHolsterStep
    {
        /// <summary>The put-away (or what cut it after the weapon left the hand: a stumble) still has the body: wait.</summary>
        Wait,
        /// <summary>The hands are empty and the body is free: the torch is taken back now (its HoldLeft pair).</summary>
        Take,
        /// <summary>The weapon is in the stance again (the put-away was cut short, or a new draw began): no take; the torch stays remembered.</summary>
        Cancel,
    }

    /// <summary>
    /// fix-252 (owner: "torch in hand, then a weapon with a shield; after a strike key 1 sometimes leaves it in the hands"):
    /// the torch a melee draw released comes back <b>after</b> the put-away, not with its first frame. G3 runs the two as one
    /// command, each motion to its end (_AI_HoldInventoryItems: the right hand's pair, then the left hand's). Ours took the
    /// torch the moment the weapon left the combat stance (WeaponHand.Drawn turns false as the sheathe starts); its take
    /// overlay replaced the sheathe's overlay variant (used whenever the body still moves, as it does after a strike), the
    /// combat animator counted the sheathe as cut short, and the weapon stayed in the hand while the torch went back into
    /// the inventory once more.
    /// </summary>
    public static class TorchHolsterRules
    {
        /// <param name="drawn">The combat stance (drawing, or holding a weapon not being put away).</param>
        /// <param name="weaponInHand">A weapon is in the hand.</param>
        /// <param name="handSettled">No draw or sheathe runs.</param>
        /// <param name="bodyBusy">A combat one-shot or held pose has the body (a stumble after the hand change, a fall).</param>
        public static TorchHolsterStep Decide(bool drawn, bool weaponInHand, bool handSettled, bool bodyBusy)
        {
            if (drawn) return TorchHolsterStep.Cancel;
            if (!handSettled || weaponInHand || bodyBusy) return TorchHolsterStep.Wait;
            return TorchHolsterStep.Take;
        }

        /// <summary>A torch key must wait (not start its overlay): a draw or sheathe of the combat hand runs and would be cut.</summary>
        public static bool TorchKeyWaits(bool handSettled) => !handSettled;
    }

    /// <summary>Save section "rules.torch": the torch in the hero's left hand (a host section, read after the inventory).</summary>
    public sealed class TorchSaveSection : ISaveParticipant
    {
        readonly Func<HeroTorch> _torch;
        string _restored;

        public TorchSaveSection(Func<HeroTorch> torch) => _torch = torch;

        public string Section => "rules.torch";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteString(_torch?.Invoke()?.Item);

        public void Read(ISaveReader r, int version) => _restored = r.ReadString();

        /// <summary>The torch read by the last load (once); null for a new game.</summary>
        public string TakeRestored()
        {
            string s = _restored;
            _restored = null;
            return s;
        }
    }
}
