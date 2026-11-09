using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-113: Use on a sleeper. G3 (CP 1.75 PS_Normal_Loop, AIMode 7): sneaking + Perk_MasterThief + humanoid robs it
    /// without a crime, otherwise AssessTalk. [owner]: an NPC held by the sleep spell is never talked to, its loot page
    /// opens anyway and each stack taken is a theft; it perceives nothing and holds no grudge when it wakes.
    /// </summary>
    public sealed class SleepLootTests
    {
        static readonly ActorId Gnar = SessionFixture.Npc;

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Ring", 50))
            .Add(RulesSchema.NpcInventoryFolder,
                 new NpcInventoryDef(Gnar, new[] { new TreasureStack("It_Gold", 40, 0), new TreasureStack("It_Ring", 1, 0) }));

        [Test]
        public void UseOnHelpless_G3MasterThiefRobs_OwnerSpellSleepRobsAsTheft_ElseTalks()
        {
            // helpless, spellbound, sneaking, master thief, humanoid
            Assert.That(PickpocketRules.UseOnHelpless(true, false, true, true, true), Is.EqualTo(HelplessUse.Rob), "G3: bed sleeper, master thief");
            Assert.That(PickpocketRules.UseOnHelpless(true, true, true, true, true), Is.EqualTo(HelplessUse.Rob), "G3 path wins: no crime");
            Assert.That(PickpocketRules.UseOnHelpless(true, false, false, true, true), Is.EqualTo(HelplessUse.Talk), "G3: bed sleeper, not sneaking");
            Assert.That(PickpocketRules.UseOnHelpless(true, false, true, false, true), Is.EqualTo(HelplessUse.Talk), "G3: bed sleeper, no perk");
            Assert.That(PickpocketRules.UseOnHelpless(true, true, false, false, true), Is.EqualTo(HelplessUse.RobAsTheft), "[owner] spell sleep");
            Assert.That(PickpocketRules.UseOnHelpless(true, true, false, false, false), Is.EqualTo(HelplessUse.Nothing), "[owner] sleeping beast");
            Assert.That(PickpocketRules.UseOnHelpless(false, false, true, true, true), Is.EqualTo(HelplessUse.Talk), "awake");
        }

        [Test]
        public void SpellSleeper_IsNotTalkable_LootMoves_EachStackIsATheft_NoGrudgeOnWaking()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
            var takes = new List<string>();
            s.Events.Subscribe<PerceptionBroadcast>(b => { if (b.Kind == BroadcastKind.TakeItem) takes.Add(b.Item); });
            var theft = s.SleeperTheft;
            Assert.That(theft.UseOn(Gnar, heroSneaking: false), Is.EqualTo(HelplessUse.Talk), "awake: talk");
            Assert.That(theft.TalkBlocked(Gnar), Is.False);

            Assert.That(s.SpellEffects.World.Sleep(Gnar), Is.True);
            Assert.That(theft.IsSpellbound(Gnar), Is.True);
            Assert.That(theft.TalkBlocked(Gnar), Is.True);
            Assert.That(s.Dialog.CanTalkTo(Gnar), Is.False);
            Assert.That(s.Dialog.HasImportantInfo(Gnar), Is.False);
            Assert.That(theft.UseOn(Gnar, heroSneaking: false), Is.EqualTo(HelplessUse.RobAsTheft));

            Assert.That(theft.Rob(Gnar, heroSneaking: false), Is.True, "something to take");
            Assert.That(theft.IsTheftVictim(Gnar), Is.True);
            Assert.That(s.Items.NpcLoot.IsGenerated(Gnar), Is.True, "plunder rolled (CleanUpPlunderInv)");
            s.Items.NpcLoot.TakeAll(Gnar);
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.EqualTo(40));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Ring"), Is.EqualTo(1));
            Assert.That(s.Items.NpcLoot.HasLoot(Gnar), Is.False);
            Assert.That(takes, Is.EquivalentTo(new[] { "It_Gold", "It_Ring" }), "one theft per stack (as taking an owned item)");

            s.SpellEffects.Tick(SpellEffectRules.NpcSleepSeconds + 1);
            Assert.That(s.SpellEffects.World.IsAsleep(Gnar), Is.False, "wakes after 30 s");
            Assert.That(theft.IsTheftVictim(Gnar), Is.False);
            Assert.That(theft.TalkBlocked(Gnar), Is.False);
            Assert.That(s.Crime.Ledger.PersonalOf(Gnar), Is.EqualTo(CrimeType.None), "he slept: saw nothing, no grudge");
        }

        [Test]
        public void SpellSleeper_SneakingMasterThief_RobsWithoutTheft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
            int takes = 0;
            s.Events.Subscribe<PerceptionBroadcast>(b => { if (b.Kind == BroadcastKind.TakeItem) takes++; });
            s.Progression.LearnSkill(PickpocketRules.MasterThiefPerk);
            Assert.That(s.SpellEffects.World.Sleep(Gnar), Is.True);
            Assert.That(s.SleeperTheft.UseOn(Gnar, heroSneaking: true), Is.EqualTo(HelplessUse.Rob));
            Assert.That(s.SleeperTheft.Rob(Gnar, heroSneaking: true), Is.True);
            Assert.That(s.SleeperTheft.IsTheftVictim(Gnar), Is.False);
            s.Items.NpcLoot.TakeAll(Gnar);
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.EqualTo(40));
            Assert.That(takes, Is.Zero, "G3: the master thief's robbery is no crime");
        }

        [Test]
        public void KnockedOutSleeper_IsABody_NotATheft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Crime.ReportPlayer(new WorldPose(0, 0, 0, 0));
            int takes = 0;
            s.Events.Subscribe<PerceptionBroadcast>(b => { if (b.Kind == BroadcastKind.TakeItem) takes++; });
            s.SpellEffects.World.Sleep(Gnar);
            Assert.That(s.SleeperTheft.Rob(Gnar, heroSneaking: false), Is.True);
            s.Combat.Vitals.KnockOut(Gnar, byPlayerParty: true);
            Assert.That(s.SleeperTheft.IsHelpless(Gnar), Is.False, "down: a body");
            s.Items.NpcLoot.TakeAll(Gnar);
            Assert.That(takes, Is.Zero, "looting a body is no crime");
        }
    }
}
