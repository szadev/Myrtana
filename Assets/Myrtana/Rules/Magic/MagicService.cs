using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>A spell was released (CP notifies the quest system on every cast: quest type SpellCast).</summary>
    public readonly struct SpellCasted : IGameEvent
    {
        public SpellCasted(ActorId caster, string spell, string item, bool powered)
        {
            Caster = caster;
            Spell = spell;
            Item = item;
            Powered = powered;
        }

        public ActorId Caster { get; }
        public string Spell { get; }
        public string Item { get; }
        public bool Powered { get; }
    }

    /// <summary>
    /// Magic for the session (Docs/combat.md §12): finds the spell of an item, moves casts through their mana phases (load
    /// pays the minimum, power the rest, abort refunds, release uses up a scroll and tells the quests), applies the instant
    /// effects (heal, refresh, cures) and builds the rule snapshot of a spell projectile's hit. The presentation decides
    /// when buttons are pressed and where missiles fly; the AI decides what NPCs cast.
    /// </summary>
    public sealed class MagicService : IDrunkSpells
    {
        readonly Dictionary<string, SpellDef> _byItem = new Dictionary<string, SpellDef>(StringComparer.Ordinal);
        readonly Dictionary<string, SpellDef> _byId = new Dictionary<string, SpellDef>(StringComparer.Ordinal);
        readonly RuleOptions _options;
        readonly ActorVitals _vitals;
        readonly Func<string, ItemDef> _items;
        readonly Func<ActorId, string, int, int> _consume;
        readonly IEventBus _events;

        /// <param name="items">Item definitions by id (whether an item is permanent and its category).</param>
        /// <param name="consume">Takes items from an actor's inventory; returns how many were taken.</param>
        public MagicService(MagicRules rules, IEnumerable<SpellDef> spells, RuleOptions options, ActorVitals vitals, StatusEffects status,
                            Func<string, ItemDef> items, Func<ActorId, string, int, int> consume, IEventBus events)
        {
            Rules = rules ?? MagicRules.Neutral();
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            Status = status ?? throw new ArgumentNullException(nameof(status));
            _items = items ?? (_ => null);
            _consume = consume ?? ((_, __, ___) => 0);
            _events = events;
            foreach (var spell in spells ?? Array.Empty<SpellDef>())
            {
                _byId[spell.Id] = spell;
                foreach (var item in spell.Items) _byItem[item] = spell;
                foreach (var item in spell.Scrolls) _byItem[item] = spell;
            }
        }

        public MagicRules Rules { get; }
        public StatusEffects Status { get; }
        public IEnumerable<SpellDef> Spells => _byId.Values;

        /// <summary>The spell an item casts (It_Spell_Fireball, It_Scroll_Fireball), or the spell of that id.</summary>
        public bool TryGetSpell(string itemOrSpell, out SpellDef spell)
        {
            spell = null;
            return itemOrSpell != null && (_byItem.TryGetValue(itemOrSpell, out spell) || _byId.TryGetValue(itemOrSpell, out spell));
        }

        public bool IsSpellItem(string item) => item != null && _byItem.ContainsKey(item);

        /// <summary>
        /// fix-193: whether readying <paramref name="item"/> (a quick-slot key) draws the spell hand and casts it: runes,
        /// scrolls, druid stones. A potion carrying a spell (transformation potions are listed as the spell's scrolls, the
        /// antidote) is drunk with the potion use motion instead (G3 OnPlayerUseQuickSlot: use type Potion → PS_QuickUse).
        /// </summary>
        public bool IsCastItem(string item) => IsSpellItem(item) && _items(item)?.Kind != "Potion";

        /// <summary>The player's pre-check for a cast from <paramref name="item"/>.</summary>
        public CastRefusal Check(ActorId caster, string item, bool hasFocus, double worldHour, bool godMode = false, string focusSpecies = null,
                                 bool focusEvil = false)
        {
            if (!TryGetSpell(item, out var spell)) return CastRefusal.DoesntWork;
            return SpellCasting.PreCheck(spell, caster.IsPlayer, _vitals.Mana(caster), godMode, hasFocus, worldHour, focusSpecies, focusEvil);
        }

        /// <summary>
        /// The button is pressed (or the NPC starts ZS_Attack_Cast): picks the cast action and loads the spell, paying the
        /// minimum mana. NPCs pay nothing (their mana is a constant 1000 in G3). Null when the item casts no spell.
        /// <paramref name="action"/>: a forced cast action instead of G3 GetCastAction (the player's power cast let go before
        /// it powered up restarts as a plain Cast, <see cref="CastTrigger.OnButtonUp"/>).
        /// </summary>
        public SpellCast Begin(ActorId caster, string item, string casterSpecies, double now, bool godMode = false, CastAction? action = null)
        {
            if (!TryGetSpell(item, out var spell)) return null;
            var def = _items(item);
            bool free = !caster.IsPlayer || godMode || def?.Category == ItemCategory.Alchemy;
            int mana = caster.IsPlayer ? _vitals.Mana(caster) : int.MaxValue;
            var chosen = action ?? SpellCasting.ActionFor(spell, casterSpecies, mana, godMode, Rules, _options);
            var cast = new SpellCast(caster, spell, item, usesUpItem: spell.UsesUp(item), free, chosen, now);
            Pay(cast, spell.MinMana);
            cast.ManaUsed = spell.MinMana;
            cast.Phase = CastPhase.Load;
            return cast;
        }

        /// <summary>
        /// While the button is held: a power-cast spell held past <see cref="MagicRules.PowerCastHoldSeconds"/> is powered
        /// up and pays the rest of its maximum mana. Returns true at the moment it powers up.
        /// </summary>
        public bool Update(SpellCast cast, double now)
        {
            if (cast == null || cast.Phase != CastPhase.Load || cast.Action != CastAction.PowerCast) return false;
            if (now - cast.StartedAt < Rules.PowerCastHoldSeconds) return false;
            PowerUp(cast);
            return true;
        }

        /// <summary>Powers a cast up at once (an NPC's chosen power cast, an ice golem).</summary>
        public void PowerUp(SpellCast cast)
        {
            if (cast == null || cast.Phase != CastPhase.Load || cast.Action != CastAction.PowerCast) return;
            Pay(cast, cast.Spell.MaxMana - cast.Spell.MinMana);
            cast.ManaUsed = cast.Spell.MaxMana;
            cast.Powered = true;
            cast.Phase = CastPhase.Power;
        }

        /// <summary>The cast is interrupted (a hit, the spell put away): the mana paid comes back.</summary>
        public void Abort(SpellCast cast)
        {
            if (cast == null || cast.Phase == CastPhase.Cast || cast.Phase == CastPhase.Aborted) return;
            if (cast.ManaPaid > 0) _vitals.AddMana(cast.Caster, cast.ManaPaid);
            cast.ManaPaid = 0;
            cast.ManaUsed = 0;
            cast.Phase = CastPhase.Aborted;
        }

        /// <summary>
        /// The button is released (or the NPC's cast motion reaches its hit): the spell leaves. A scroll is used up, the
        /// quests are told. Returns false when the cast was already over or its scroll is gone.
        /// </summary>
        public bool Release(SpellCast cast)
        {
            if (cast == null || (cast.Phase != CastPhase.Load && cast.Phase != CastPhase.Power)) return false;
            if (cast.UsesUpItem && _consume(cast.Caster, cast.Item, 1) == 0)
            {
                Abort(cast);
                return false;
            }
            cast.Phase = CastPhase.Cast;
            _events?.Publish(new SpellCasted(cast.Caster, cast.Spell.Id, cast.Item, cast.Powered));
            return true;
        }

        /// <summary>
        /// Applies a released spell's instant effect on <paramref name="target"/> (the caster for caster spells): heal and
        /// cure set HP to the maximum, Heal Other also clears burning, freezing, poison and disease, Refresh fills mana,
        /// the cure spells clear their condition. Returns false for effects that are not instant (projectiles, summons,
        /// area spells, transformations …).
        /// </summary>
        public bool ApplyInstant(SpellCast cast, ActorId target)
        {
            if (cast == null || !HasInstantEffect(cast.Spell.Effect)) return false;
            if (cast == null || !cast.Released || target.IsEmpty || _vitals.StateOf(target) == VitalState.Dead) return false;
            switch (cast.Spell.Effect)
            {
                case "MagicHeal":
                    FillHitPoints(target);
                    return true;
                case "MagicHealOther":
                    FillHitPoints(target);
                    Status.Clear(target, ActorStatus.Burning | ActorStatus.Frozen | ActorStatus.Poisoned | ActorStatus.Diseased);
                    return true;
                case "MagicRefresh":
                    _vitals.AddMana(target, _vitals.ManaMax(target) - _vitals.Mana(target));
                    return true;
                case "MagicPoison": // Spl_Poison: the target is poisoned (combat-6)
                    Status.Poison(target);
                    return true;
                case "MagicDisease": // Spl_Disease: diseased, stamina gone
                    Status.Infect(target);
                    return true;
                case "MagicCurePoison":
                    Status.Clear(target, ActorStatus.Poisoned);
                    return true;
                case "MagicCureDisease":
                    Status.Clear(target, ActorStatus.Diseased);
                    return true;
                case Teleports.Effect: // the move itself is the Teleports service's answer to SpellCasted
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// G3 PS_MagicPotion for a potion with an instant spell (the antidote, the potion against diseases; fix-127): a
        /// Potion-use item whose spell <see cref="ApplyInstant"/> carries out. Transformation potions have their own path.
        /// </summary>
        public bool CastsWhenDrunk(string item) =>
            _byItem.TryGetValue(item ?? "", out var spell) && HasInstantEffect(spell.Effect) && _items(item)?.Kind == "Potion";

        /// <summary>The drunk potion's spell leaves at once on the drinker (no mana, no motion of its own; quests are told).</summary>
        public void CastDrunk(ActorId drinker, string item)
        {
            if (!CastsWhenDrunk(item)) return;
            var cast = new SpellCast(drinker, _byItem[item], item, usesUpItem: false, freeMana: true, CastAction.Cast, 0) { Phase = CastPhase.Load };
            if (Release(cast)) ApplyInstant(cast, drinker);
        }

        /// <summary>The target effects <see cref="ApplyInstant"/> carries out (heal, refresh, poison, disease, cures, teleport).</summary>
        public static bool HasInstantEffect(string effect)
        {
            switch (effect)
            {
                case "MagicHeal": case "MagicHealOther": case "MagicRefresh": case "MagicPoison": case "MagicDisease":
                case "MagicCurePoison": case "MagicCureDisease": case Teleports.Effect:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Turns a melee-style snapshot into a spell projectile's hit: spell delivery, the missile's damage type and amount,
        /// the mana used (drives the player's INT bonus; a spell of no mana doubles the base instead), the deity (mage perks), and the power cast
        /// (path style "Missile" when it flew at a target: double damage).
        /// </summary>
        public static HitContext ToSpellHit(HitContext hit, SpellCast cast, bool flewAtTarget)
        {
            var missile = cast.Spell.Missile;
            hit.Delivery = HitDelivery.Spell;
            hit.DamageType = missile?.DamageType ?? DamageType.Fire;
            hit.DamageAmount = missile?.Damage ?? 0;
            hit.HitMultiplier = 1f;
            hit.Quality = WeaponQuality.None;
            hit.SpellProjectile = true;
            hit.PowerCast = cast.Powered && flewAtTarget;
            hit.ManaUsed = cast.ManaUsed;
            hit.SpellDeity = cast.Spell.DeityName;
            hit.FireOrExplosiveProjectile = RangedRules.IsFireOrExplosive(missile?.Template);
            if (hit.Attacker != null) hit.Attacker.Action = CombatAction.None;
            return hit;
        }

        /// <summary>
        /// Turns a snapshot of caster and victim into an area spell's hit (Docs/combat.md §12.1): spell delivery, the area's
        /// damage type and amount; the spell object is the source, so no attacker bonus applies (<see cref="HitContext.AreaSpell"/>).
        /// The victim is never the caster's target (friendly-fire gates of non-target spells apply).
        /// </summary>
        public static HitContext ToAreaHit(HitContext hit, SpellDef spell, in AreaHit area)
        {
            hit.Delivery = HitDelivery.Spell;
            hit.DamageType = area.Type;
            hit.DamageAmount = area.Damage;
            hit.HitMultiplier = 1f;
            hit.Quality = WeaponQuality.None;
            hit.SpellProjectile = false;
            hit.PowerCast = false;
            hit.AreaSpell = true;
            hit.ManaUsed = 0;
            hit.SpellDeity = spell?.DeityName;
            hit.FireOrExplosiveProjectile = false;
            hit.Headshot = false;
            hit.VictimIsTarget = false;
            hit.AttackerSeesVictim = true;
            if (hit.Attacker != null) hit.Attacker.Action = CombatAction.None;
            return hit;
        }

        void FillHitPoints(ActorId target) => _vitals.AddHitPoints(target, _vitals.HitPointsMax(target) - _vitals.HitPoints(target));

        void Pay(SpellCast cast, int amount)
        {
            if (cast.FreeMana || amount <= 0) return;
            _vitals.AddMana(cast.Caster, -amount);
            cast.ManaPaid += amount;
        }
    }
}
