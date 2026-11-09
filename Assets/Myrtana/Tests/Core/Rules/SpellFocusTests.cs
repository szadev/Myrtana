using System.Collections.Generic;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-207: the readied spell decides whom the magic focus picks (Heal Other: friends; self spells: nobody).</summary>
    public class SpellFocusTests
    {
        static FocusFacts Npc(Attitude attitude, bool combat = false, bool targetsHero = false, bool targetsHerosTarget = false,
                              bool party = false, VitalState state = VitalState.Alive) =>
            new FocusFacts { IsNpc = true, State = state, AttitudeToHero = attitude, InCombat = combat, TargetsHero = targetsHero,
                             TargetsHerosTarget = targetsHerosTarget, HerosParty = party };

        static SpellDef Spell(string id, SpellTarget target, SpellKind kind, string effect = null) =>
            new SpellDef(id, new[] { "It_" + id }, null, SpellCastType.Regular, target, kind, SpellDeity.Innos, 10, 10,
                         SpellDelivery.None, effect, null, 0, null, null, null);

        [Test]
        public void RuleOf_HealOtherFriendly_SelfSpellsNone_RestHostile()
        {
            Assert.That(SpellFocus.RuleOf(Spell("Spl_HealOther", SpellTarget.Focus, SpellKind.Target, "MagicHealOther")), Is.EqualTo(SpellFocusRule.Friendly));
            Assert.That(SpellFocus.RuleOf(Spell("Spl_Custom", SpellTarget.Focus, SpellKind.Target, "MagicHealOther")), Is.EqualTo(SpellFocusRule.Friendly), "by effect");
            foreach (var self in new[] { "Spl_Cure", "Spl_CurePoison", "Spl_CureDisease", "Spl_Refresh", "Spl_Teleport_Faring", "Spl_Transform_Wolf", "Spl_Flamesword" })
                Assert.That(SpellFocus.RuleOf(Spell(self, SpellTarget.Caster, SpellKind.Target)), Is.EqualTo(SpellFocusRule.None), self);
            Assert.That(SpellFocus.RuleOf(Spell("Spl_Fireball", SpellTarget.Focus, SpellKind.Projectile)), Is.EqualTo(SpellFocusRule.Hostile));
            Assert.That(SpellFocus.RuleOf(Spell("Spl_Sleep", SpellTarget.Focus, SpellKind.Target)), Is.EqualTo(SpellFocusRule.Hostile));
            Assert.That(SpellFocus.RuleOf(Spell("Spl_Firerain", SpellTarget.Caster, SpellKind.Summon)), Is.EqualTo(SpellFocusRule.Hostile), "area spells keep G3's focus");
            Assert.That(SpellFocus.RuleOf(null), Is.EqualTo(SpellFocusRule.Hostile));
        }

        [Test]
        public void HealOther_FocusesFriendsCompanionsAndNeutrals_NeverEnemies()
        {
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Friendly, combat: true, targetsHerosTarget: true, party: true)),
                        Is.EqualTo(CombatFocusPriority.Lowest), "companion fighting the hero's foe");
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Friendly, party: true)), Is.EqualTo(0), "companion standing by");
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Neutral)), Is.EqualTo(0));
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Angry)), Is.EqualTo(0), "G3: only GetAttitude 4..6 is cut");
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Hostile, combat: true, targetsHero: true)), Is.EqualTo(-1), "the foe");
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Predator)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Panic)), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Friendly, state: VitalState.Down)), Is.EqualTo(-1), "downed");
            Assert.That(CombatFocusPriority.HealOther(Npc(Attitude.Friendly, state: VitalState.Dead)), Is.EqualTo(-1), "dead");
            Assert.That(CombatFocusPriority.HealOther(new FocusFacts { State = VitalState.Alive, AttitudeToHero = Attitude.Friendly }), Is.EqualTo(-1), "not an NPC");
        }

        [Test]
        public void Spell_DispatchesByRule()
        {
            var foe = Npc(Attitude.Hostile, combat: true, targetsHero: true);
            var companion = Npc(Attitude.Friendly, combat: true, targetsHerosTarget: true, party: true);
            Assert.That(CombatFocusPriority.Spell(foe, SpellFocusRule.Hostile), Is.EqualTo(CombatFocusPriority.High));
            Assert.That(CombatFocusPriority.Spell(companion, SpellFocusRule.Hostile), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Spell(foe, SpellFocusRule.Friendly), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Spell(companion, SpellFocusRule.Friendly), Is.EqualTo(0));
            Assert.That(CombatFocusPriority.Spell(foe, SpellFocusRule.None), Is.EqualTo(-1));
            Assert.That(CombatFocusPriority.Spell(companion, SpellFocusRule.None), Is.EqualTo(-1));
        }

        [Test]
        public void HealOther_InAFight_PicksTheCompanionNotTheFoeAhead()
        {
            // The foe straight ahead, the companion 20° aside: PS_Magic's Cast rule takes the foe, Heal Other the companion.
            var foe = Npc(Attitude.Hostile, combat: true, targetsHero: true);
            var companion = Npc(Attitude.Friendly, combat: true, targetsHerosTarget: true, party: true);
            FocusCandidate C(float azi, int prio) => new FocusCandidate { AzimuthDeg = azi, DistanceCm = 800, HalfExtentCm = 40, FreeLineOfSight = true, Priority = prio };
            var hostile = new List<FocusCandidate> { C(0, CombatFocusPriority.Spell(foe, SpellFocusRule.Hostile)), C(20, CombatFocusPriority.Spell(companion, SpellFocusRule.Hostile)) };
            var friendly = new List<FocusCandidate> { C(0, CombatFocusPriority.Spell(foe, SpellFocusRule.Friendly)), C(20, CombatFocusPriority.Spell(companion, SpellFocusRule.Friendly)) };
            Assert.That(CombatFocus.Pick(hostile, FocusScanMode.Magic, false, false, 150), Is.EqualTo(0));
            Assert.That(CombatFocus.Pick(friendly, FocusScanMode.Magic, false, false, 150), Is.EqualTo(1));
        }
    }
}
