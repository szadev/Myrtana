using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>The cast an actor performs (the magic subset of G3 gEAction); picks the cast motion.</summary>
    public enum CastAction { Cast, PowerCast, QuickCast, Summon, FlameSword }

    /// <summary>
    /// Where a cast is (G3 _AI_StartCastPhase): Hold (the spell is in the hand, nothing paid), Load (the minimum mana is
    /// paid), Power (the rest up to the maximum is paid), Cast (released), Aborted (what was paid came back).
    /// </summary>
    public enum CastPhase { Hold, Load, Power, Cast, Aborted }

    /// <summary>Why the player's cast does not start (G3 fizzle pre-check; NPC casters skip it).</summary>
    public enum CastRefusal
    {
        None,
        /// <summary>Less mana than the spell's minimum: the spell fizzles.</summary>
        NotEnoughMana,
        /// <summary>A focus spell without a focus target ("DoesntWork").</summary>
        NoFocus,
        /// <summary>The spell's own condition failed (Dusk to Dawn by day …; "DoesntWork").</summary>
        DoesntWork,
    }

    /// <summary>
    /// One cast in progress (G3 PS_Magic_Cast/PowerCast/QuickCast/Summon and the NPC's ZS_Attack_Cast): which spell from
    /// which item, the cast action, the mana phase, and the mana it used so far. Plain data; <see cref="MagicService"/>
    /// moves it through its phases.
    /// </summary>
    public sealed class SpellCast
    {
        internal SpellCast(ActorId caster, SpellDef spell, string item, bool usesUpItem, bool freeMana, CastAction action, double startedAt)
        {
            Caster = caster;
            Spell = spell;
            Item = item;
            UsesUpItem = usesUpItem;
            FreeMana = freeMana;
            Action = action;
            StartedAt = startedAt;
        }

        public ActorId Caster { get; }
        public SpellDef Spell { get; }
        /// <summary>The item cast from (It_Spell_…, It_Scroll_…).</summary>
        public string Item { get; }
        /// <summary>The item is not permanent (a scroll): casting uses it up.</summary>
        public bool UsesUpItem { get; }
        /// <summary>The caster pays no mana (NPCs, whose mana is constant; Alchemy items).</summary>
        public bool FreeMana { get; }
        public CastAction Action { get; }
        /// <summary>Real seconds when the button was pressed (the power-cast hold time counts from here).</summary>
        public double StartedAt { get; }
        public CastPhase Phase { get; internal set; } = CastPhase.Hold;
        /// <summary>Mana spent on the spell: the minimum after loading, the maximum after powering (G3 ManaUsed).</summary>
        public int ManaUsed { get; internal set; }
        /// <summary>Mana actually taken from the caster (0 when free); refunded on abort.</summary>
        public int ManaPaid { get; internal set; }

        /// <summary>The spell was powered up (the maximum mana is used; a power cast with a target flies as G3 path style "Missile": double damage).</summary>
        public bool Powered { get; internal set; }
        public bool Released => Phase == CastPhase.Cast;

        public override string ToString() => $"{Caster} {Action} {Spell.Id} [{Phase}, mana {ManaUsed}]";
    }

    /// <summary>
    /// The pure casting rules (Docs/gameplay-logic.md §4.1, Docs/combat.md §12): the cast action of a spell, the
    /// player's fizzle pre-check, the cast motion speed and the mana phases.
    /// </summary>
    public static class SpellCasting
    {
        /// <summary>
        /// G3 GetCastAction: the Flame Sword spell has its own action; Immediate spells are quick casts, Regular spells
        /// summons; Power spells power-cast when the caster has mana for the maximum (ice golems always do; with AB goblins
        /// never), otherwise they are cast normally.
        /// </summary>
        public static CastAction ActionFor(SpellDef spell, string casterSpecies, int mana, bool godMode, MagicRules rules, RuleOptions options)
        {
            if (spell == null) throw new ArgumentNullException(nameof(spell));
            if (rules.FlameSwordSpell != null && string.Equals(spell.Id, rules.FlameSwordSpell, StringComparison.Ordinal)) return CastAction.FlameSword;
            switch (spell.CastType)
            {
                case SpellCastType.Immediate: return CastAction.QuickCast;
                case SpellCastType.Regular: return CastAction.Summon;
            }
            if (Contains(rules.AlwaysPowerCastSpecies, casterSpecies)) return CastAction.PowerCast;
            if (mana < spell.MaxMana && !godMode) return CastAction.Cast;
            if (options.AlternativeBalancing && Contains(rules.BalancedNeverPowerCastSpecies, casterSpecies)) return CastAction.Cast;
            return CastAction.PowerCast;
        }

        /// <summary>CP's evil-only spell (pre-check: the focus must be IsEvil).</summary>
        public const string DestroyEvil = "Spl_DestroyEvil";

        /// <summary>
        /// The player's pre-check in G3 order: mana below the minimum fizzles; a focus spell needs a focus unless it is
        /// power-cast; Dusk to Dawn works only from 20:00 to 06:00. NPC casters skip every check.
        /// </summary>
        public static CastRefusal PreCheck(SpellDef spell, bool isPlayer, int mana, bool godMode, bool hasFocus, double worldHour,
                                           string focusSpecies = null, bool focusEvil = false)
        {
            if (!isPlayer) return CastRefusal.None;
            if (mana < spell.MinMana && !godMode) return CastRefusal.NotEnoughMana;
            if (spell.NeedsFocus && spell.CastType != SpellCastType.Power && !hasFocus) return CastRefusal.NoFocus;
            // Animal Form: the focus target's species must be in the list (CP adds Meatbug); "DoesntWork" without a fizzle.
            if (spell.Effect == "MagicAnimalForm" && hasFocus && focusSpecies != null && !TransformRules.AnimalFormTargetAllowed(focusSpecies))
                return CastRefusal.DoesntWork;
            if (string.Equals(spell.Id, "Spl_DuskToDawn", StringComparison.Ordinal) && worldHour > 6.0 && worldHour < 20.0) return CastRefusal.DoesntWork;
            // CP 1.75 (FUN_100798d0): Destroy Evil needs a focus that is IsEvil (undead, golems, demons, gargoyles, the
            // scorpion king), else StartSayDoesntWork + fizzle.
            if (string.Equals(spell.Id, DestroyEvil, StringComparison.Ordinal) && !(hasFocus && focusEvil)) return CastRefusal.DoesntWork;
            return CastRefusal.None;
        }

        /// <summary>
        /// Playback speed of a cast motion: summons ×1.5, the flame sword ×0.7, with AB the fast caster (Xardas) casts at
        /// ×1.6; everything else as authored.
        /// </summary>
        public static float MotionSpeed(CastAction action, string casterName, MagicRules rules, RuleOptions options)
        {
            var s = rules.CastSpeeds;
            switch (action)
            {
                case CastAction.Summon: return s.Summon;
                case CastAction.FlameSword: return s.FlameSword;
                case CastAction.Cast:
                case CastAction.PowerCast:
                    return options.AlternativeBalancing && s.FastCaster != null && string.Equals(casterName, s.FastCaster, StringComparison.Ordinal)
                        ? s.FastCasterSpeed : 1f;
                default: return 1f;
            }
        }

        /// <summary>
        /// Duration bonus of a timed spell: the player with the deity's mage perk gets +50 % (Perk_Firemage = Innos,
        /// Perk_Watermage = Adanos, Perk_Darkmage = Beliar).
        /// </summary>
        public static float DurationWithBonus(float baseSeconds, SpellDeity deity, bool isPlayer, Func<string, bool> knows)
        {
            string perk = DeityPerk(deity);
            return isPlayer && perk != null && knows != null && knows(perk) ? baseSeconds * 1.5f : baseSeconds;
        }

        public static string DeityPerk(SpellDeity deity) =>
            deity == SpellDeity.Innos ? "Perk_Firemage" : deity == SpellDeity.Adanos ? "Perk_Watermage" : deity == SpellDeity.Beliar ? "Perk_Darkmage" : null;

        static bool Contains(System.Collections.Generic.IReadOnlyList<string> list, string value)
        {
            if (value == null) return false;
            foreach (var s in list)
                if (string.Equals(s, value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
