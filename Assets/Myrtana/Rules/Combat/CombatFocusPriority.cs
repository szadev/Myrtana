namespace Myrtana.Rules
{
    /// <summary>What the focus priority looks at for one character, seen from the hero (fix-68).</summary>
    public struct FocusFacts
    {
        /// <summary>An NPC or creature (G3: the entity has an NPC property set); false for anything else.</summary>
        public bool IsNpc;
        public VitalState State;
        /// <summary>The NPC's attitude to the hero (CP GetAttitudeToPlayer: every member of his party is Friendly).</summary>
        public Attitude AttitudeToHero;
        /// <summary>The NPC fights (G3 AI mode Combat).</summary>
        public bool InCombat;
        /// <summary>Its current target is the hero.</summary>
        public bool TargetsHero;
        /// <summary>Its current target is the hero's current target (it fights his foe).</summary>
        public bool TargetsHerosTarget;
        /// <summary>It belongs to the hero's party, or leads the hero (the guide).</summary>
        public bool HerosParty;
        /// <summary>The hero holds the block (IsInParadeMode).</summary>
        public bool HeroInParade;
    }

    /// <summary>
    /// G3 CP 1.75 <c>GetFocusEntityPriority</c> (Script_Game 0x100aa4a0), the engine's focus callback: −1 = the
    /// character cannot be focused; otherwise gCFocus_PS prefers the higher priority before the angle (FocusCheck /
    /// HasLowerPriority, <see cref="CombatFocus.Pick"/>). Ported for the combat stances (PS_Melee, PS_Magic, PS_Ranged);
    /// PS_Normal keeps its own focus (PlayerInteraction). Docs/combat.md §9.2.
    /// <para>Not ported [interp]: the "won fight" clause (combat state 1 and LastFightAgainstPlayer 4 count as targeting
    /// the hero), the PS_Magic status-effect 0x40 range cut (700 cm) and the teleport-stone special case.</para>
    /// </summary>
    public static class CombatFocusPriority
    {
        /// <summary><see cref="Lowest"/>: GetFocusEntityPriority's 0, focusable but below every other priority (Spl_HealOther).</summary>
        public const int Unfocusable = -1, Lowest = 0, Low = 1, Medium = 2, High = 3;

        /// <summary>
        /// PS_Melee: dead → −1; downed → 3 only while the hero blocks, else −1. A friendly NPC → 3 only while it fights the
        /// hero, else −1. Otherwise not fighting → 1; fighting: the hero's foe as its target (an ally) → −1, the hero as its
        /// target → 3, the hero's party → −1, else by attitude: Neutral/Panic 1, Angry 2, Hostile 3, Predator −1.
        /// </summary>
        public static int Melee(in FocusFacts f)
        {
            if (!f.IsNpc || f.State == VitalState.Dead) return Unfocusable;
            if (f.State == VitalState.Down) return f.HeroInParade ? High : Unfocusable;
            if (f.AttitudeToHero == Attitude.Friendly) return f.TargetsHero && f.InCombat ? High : Unfocusable;
            if (!f.InCombat) return Low;
            if (f.TargetsHerosTarget && !f.TargetsHero) return Unfocusable;
            if (f.TargetsHero) return High;
            if (f.HerosParty) return Unfocusable;
            switch (f.AttitudeToHero)
            {
                case Attitude.Neutral:
                case Attitude.Panic: return Low;
                case Attitude.Angry: return Medium;
                case Attitude.Hostile: return High;
                default: return Unfocusable;
            }
        }

        /// <summary>
        /// PS_Magic / PS_Ranged: dead or downed → −1; not fighting → 1; fighting: the hero's foe as its target → −1, the
        /// hero's party → −1, the hero as its target → 3, else by attitude: Neutral 1, Angry/Hostile/Panic 2, Friendly and
        /// Predator −1.
        /// </summary>
        public static int Cast(in FocusFacts f)
        {
            if (!f.IsNpc || f.State != VitalState.Alive) return Unfocusable;
            if (!f.InCombat) return Low;
            if (f.TargetsHerosTarget && !f.TargetsHero) return Unfocusable;
            if (f.HerosParty) return Unfocusable;
            if (f.TargetsHero) return High;
            switch (f.AttitudeToHero)
            {
                case Attitude.Neutral: return Low;
                case Attitude.Angry:
                case Attitude.Hostile:
                case Attitude.Panic: return Medium;
                default: return Unfocusable;
            }
        }

        /// <summary>
        /// PS_Magic with Spl_HealOther readied (fix-207) [GetFocusEntityPriority 0x100aa4a0, the Spl_HealOther branch
        /// after the dead/downed test, before the combat clauses]: script GetAttitude(NPC, hero) Hostile, Predator or Panic
        /// → −1, else 0. So friends, companions (also while they fight), neutral and angry NPCs are focused by angle alone,
        /// enemies never; dead or downed ones neither.
        /// </summary>
        public static int HealOther(in FocusFacts f)
        {
            if (!f.IsNpc || f.State != VitalState.Alive) return Unfocusable;
            switch (f.AttitudeToHero)
            {
                case Attitude.Hostile:
                case Attitude.Predator:
                case Attitude.Panic: return Unfocusable;
                default: return Lowest;
            }
        }

        /// <summary>The priority of a character for the spell's focus rule (<see cref="SpellFocus.RuleOf"/>).</summary>
        public static int Spell(in FocusFacts f, SpellFocusRule rule) =>
            rule == SpellFocusRule.Friendly ? HealOther(f) : rule == SpellFocusRule.None ? Unfocusable : Cast(f);
    }

    /// <summary>Whom the readied spell focuses (fix-207).</summary>
    public enum SpellFocusRule
    {
        /// <summary>G3 PS_Magic's priority (<see cref="CombatFocusPriority.Cast"/>): foes first, idle NPCs low.</summary>
        Hostile,
        /// <summary>Spl_HealOther: everyone but enemies (<see cref="CombatFocusPriority.HealOther"/>).</summary>
        Friendly,
        /// <summary>A spell on the caster himself (the cures, Refresh, teleports, transformations …): no character focus.</summary>
        None,
    }

    /// <summary>
    /// fix-207 (owner: "with a heal spell readied the focus picks enemies"): the focus rule of a readied spell. Spl_HealOther
    /// (the one friendly focus spell of G3; effect MagicHealOther) uses its own priority; a caster-only effect spell
    /// (gESpellTarget Caster, gESpellType Target: Spl_Cure, CurePoison, CureDisease, Refresh, teleports, transformations,
    /// Flame Sword …) never uses the focus, so none is shown [interp: G3 still runs FindFocusEntity in PS_Magic]; every
    /// other spell (missiles, focus spells, area and summon spells) keeps G3's PS_Magic priority.
    /// </summary>
    public static class SpellFocus
    {
        public const string HealOtherSpell = "Spl_HealOther", HealOtherEffect = "MagicHealOther";

        public static SpellFocusRule RuleOf(SpellDef spell)
        {
            if (spell == null) return SpellFocusRule.Hostile;
            if (spell.Id == HealOtherSpell || spell.Effect == HealOtherEffect) return SpellFocusRule.Friendly;
            if (spell.Target == SpellTarget.Caster && spell.Kind == SpellKind.Target) return SpellFocusRule.None;
            return SpellFocusRule.Hostile;
        }
    }
}
