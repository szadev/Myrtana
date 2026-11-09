using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// story-3 in a whole session: a gate guard stops the hero with its warnings (the NPC starts the dialog), the
    /// warnings survive save/load, SetClass changes how the NPC fights, pickpocketing, and the revolution fights of an
    /// enclave in Attack.
    /// </summary>
    public class GuardRevolutionSessionTests
    {
        static readonly ActorId GateGuard = new ActorId("Orc_Faring_GateGuard_01");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
            .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
            .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Faring", "Orc"));

        static NpcDef Npc(ActorId id, float x, string species = "Orc", string faction = "Orc", string enclave = "Faring", int levelMax = 10) =>
            new NpcDef(id, new TextId("FO_" + id.Name), "Orc_Warrior_Hard", species, "visual", new WorldPose(x, 0, 0, 0),
                       combat: new NpcCombatDef(5, levelMax, species, true, faction: faction, enclave: enclave, hasEnclave: enclave != null));

        static DialogEntryDef Warning(string id, bool second, string line) =>
            new DialogEntryDef(new Id<DialogEntryDef>(id), ActorId.Player, default, DialogEntryKind.Important, second ? 2 : 1, default,
                               true, false, false, null, new GateGuardMayWarn(ActorRef.Partner, second), null,
                               new DialogCommand[] { new Say(ActorRef.Partner, new TextId(line), null), new GateGuardWarned(ActorRef.Partner, second) });

        [Test]
        public void GateGuard_StopsTheHeroWithItsWarnings_ThenAttacks_AndRemembersAcrossSaveLoad()
        {
            using (var f = Fixture()
                .Add(SimulationSchema.NpcFolder, Npc(GateGuard, 2))
                .Add(SimulationSchema.GateGuardFolder, new GateGuardDef(GateGuard, new WorldPose(0, 0, 0, 0)))
                .Add(StorySchema.DialogFolder, Warning("FIRSTWARN", false, "Halt"), Warning("SECONDWARN", true, "ISaidDontGoFurther")))
            {
                var s = f.NewGame();
                s.Crime.ReportPlayer(new WorldPose(5, 0, 0, 0));
                var seen = new Percept(ActorId.Player, 300, Sense.Seen);
                var reaction = s.Crime.Crime.AssessPlayer(GateGuard, seen, new PlayerConduct { WeaponDrawn = true }, 0);
                Assert.That(reaction.Kind, Is.EqualTo(ReactionKind.Talk), "the guard comes to warn, not about the weapon");
                Assert.That(s.Dialog.IsActive, Is.True, "the NPC started the conversation");
                Assert.That(f.Presenter.Lines.Last().Text.Value, Is.EqualTo("Halt"));
                s.Dialog.CompleteLine();
                Assert.That(s.WorldState.GateGuards.StatusOf(GateGuard), Is.EqualTo(GateGuardStatus.FirstWarnGiven));
                Assert.That(s.Dialog.IsActive, Is.False);

                s.Crime.ReportPlayer(new WorldPose(4.5f, 0, 0, 0)); // 50 closer: nothing
                Assert.That(s.Crime.Crime.AssessPlayer(GateGuard, seen, default, 1).Kind, Is.EqualTo(ReactionKind.None));
                s.Crime.ReportPlayer(new WorldPose(4.1f, 0, 0, 0)); // 90 closer: second warning
                Assert.That(s.Crime.Crime.AssessPlayer(GateGuard, seen, default, 2).Kind, Is.EqualTo(ReactionKind.Talk));
                Assert.That(f.Presenter.Lines.Last().Text.Value, Is.EqualTo("ISaidDontGoFurther"));
                s.Dialog.CompleteLine();
                Assert.That(s.WorldState.GateGuards.StatusOf(GateGuard), Is.EqualTo(GateGuardStatus.SecondWarnGiven));

                var loaded = f.Reload(s);
                Assert.That(loaded.WorldState.GateGuards.StatusOf(GateGuard), Is.EqualTo(GateGuardStatus.SecondWarnGiven));
                loaded.Crime.ReportPlayer(new WorldPose(3.2f, 0, 0, 0));
                Assert.That(loaded.Crime.Crime.AssessPlayer(GateGuard, seen, default, 3).ToString(), Is.EqualTo("Attack player (GateGuard)"));
                Assert.That(loaded.NpcCombat.Get(GateGuard)?.IsFighting, Is.True);
            }
        }

        [Test]
        public void SetGuardStatus_LetsThePlayerPass()
        {
            using (var f = Fixture()
                .Add(SimulationSchema.NpcFolder, Npc(GateGuard, 2))
                .Add(SimulationSchema.GateGuardFolder, new GateGuardDef(GateGuard, new WorldPose(0, 0, 0, 0)))
                .Add(StorySchema.DialogFolder, Warning("FIRSTWARN", false, "Halt")))
            {
                var s = f.NewGame();
                s.WorldState.Director.SetGuardActive(GateGuard, false);
                s.Crime.ReportPlayer(new WorldPose(3, 0, 0, 0));
                Assert.That(s.Crime.Crime.AssessPlayer(GateGuard, new Percept(ActorId.Player, 100, Sense.Seen), default, 0).Kind,
                            Is.EqualTo(ReactionKind.None));
                Assert.That(s.Dialog.IsActive, Is.False);
                var loaded = f.Reload(s);
                Assert.That(loaded.WorldState.Director.IsGuardInactive(GateGuard), Is.True);
            }
        }

        [Test]
        public void SetClass_ChangesTheNpcsCombatClass_AcrossSaveLoad()
        {
            var monk = new ActorId("Monk");
            using (var f = Fixture().Add(SimulationSchema.NpcFolder, Npc(monk, 3, "Human", "Reb", null)))
            {
                var s = f.NewGame();
                s.Actors.TryGet(monk, out var state);
                Assert.That(CombatWiring.CombatDefOf(state).Class, Is.EqualTo(NpcClass.None));
                s.WorldState.Director.SetClass(monk, "Mage");
                Assert.That(CombatWiring.CombatDefOf(state).Class, Is.EqualTo(NpcClass.Mage));
                Assert.That(s.NpcCombat.BrainOf(monk).Definition.Class, Is.EqualTo(NpcClass.Mage), "a new brain fights as a mage");

                var loaded = f.Reload(s);
                loaded.Actors.TryGet(monk, out var again);
                Assert.That(CombatWiring.CombatDefOf(again).Class, Is.EqualTo(NpcClass.Mage));
            }
        }

        /// <summary>
        /// fix-94, CP 1.75 SetPAL Reb on Wenzel (Kap Dun massacre): he becomes a Friend NPC outside the enclave, Kap Dun's
        /// Raid flag goes on, and his story alignment (Reb) is his political one, also after a load.
        /// </summary>
        [Test]
        public void SetAlignmentRebOnWenzel_MakesHimAFriendOutsideKapDun_AndRaidsKapDun()
        {
            var wenzel = new ActorId("Wenzel");
            var orc = new ActorId("KapDun_Orc");
            using (var f = new SessionFixture()
                .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
                .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
                .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
                .Add(RulesSchema.EnclaveFolder, new EnclaveDef("KapDun", "Orc"))
                .Add(SimulationSchema.NpcFolder, Npc(wenzel, 2, "Human", null, "KapDun"), Npc(orc, 4, "Orc", "Orc", "KapDun"))
                .Add(StorySchema.DialogFolder, new DialogEntryDef(new Id<DialogEntryDef>("KAPDUNMETZEL"), wenzel, default, DialogEntryKind.News,
                                                                  0, default, false, false, false, null, null, null,
                                                                  new DialogCommand[] { new SetAlignment(ActorRef.Partner, "Reb") })))
            {
                var s = f.NewGame();
                s.Actors.TryGet(wenzel, out var state);
                Assert.That(CombatWiring.CombatDefOf(state).Enclave, Is.EqualTo("KapDun"));
                Assert.That(s.Crime.Attitudes.Of(orc, wenzel), Is.Not.EqualTo(Attitude.Hostile), "no alignment of his own yet");
                s.Dialog.Talk(wenzel);
                var def = CombatWiring.CombatDefOf(state);
                Assert.That(def.FriendType && def.Enclave == null && !def.HasEnclave, Is.True);
                Assert.That(s.Enclaves.Of("KapDun").Raid, Is.True);
                Assert.That(s.Crime.Attitudes.Of(orc, wenzel), Is.EqualTo(Attitude.Hostile), "a rebel now");

                var loaded = f.Reload(s);
                loaded.Actors.TryGet(wenzel, out var again);
                Assert.That(CombatWiring.CombatDefOf(again).FriendType, Is.True);
                Assert.That(loaded.Enclaves.Of("KapDun").Raid, Is.True);
            }
        }

        [Test]
        public void Pickpocket_GivesTheLoot_CountsTheTheft_AndRemembersTheVictim()
        {
            var victim = new ActorId("Tjalf");
            var set = new TreasureSetDef("TS_PickPocket_Tjalf", TreasureUse.Pickpocket, 1, 1, new[] { new TreasureStack("It_Key_Tjalf", 1, 0) });
            using (var f = Fixture()
                .Add(SimulationSchema.NpcFolder, Npc(victim, 1, "Human", "Reb", "Faring", levelMax: 0))
                .Add(RulesSchema.TreasureFolder, set)
                .Add(RulesSchema.PickpocketFolder, new PickpocketDef(victim, new[] { set.Id })))
            {
                var s = f.NewGame();
                Assert.That(s.Pickpockets.MayPickpocket(victim), Is.False, "needs Perk_PickPocket_1");
                s.Progression.LearnSkill(PickpocketRules.OfferPerk);
                Assert.That(s.Pickpockets.MayPickpocket(victim), Is.True);
                s.Hero.Stats.Set(PickpocketRules.TheftAttribute, new StatValue(40)); // 20 - 0 + 85 ≥ any roll
                Assert.That(s.Pickpockets.Pickpocket(victim), Is.EqualTo(PickpocketVerdict.Success));
                Assert.That(s.Items.Inventories.Of(ActorId.Player).Has("It_Key_Tjalf"), Is.True);
                Assert.That(s.Crime.Ledger.Enclave("Faring").CountOf(CrimeType.Theft), Is.EqualTo(1));
                Assert.That(s.Pickpockets.MayPickpocket(victim), Is.False, "robbed once");

                var loaded = f.Reload(s);
                Assert.That(loaded.Pickpockets.WasRobbed(victim), Is.True);
            }
        }

        /// <summary>
        /// fix-248: G3 PickPocket's failure is AssessTarget(victim, hero, Theft): the victim fights the hero for Theft and
        /// memorizes the theft (a non-guard: personally); nothing is stolen or counted. A too strong victim only makes the
        /// hero say "TooHard". The port's HUD names the missing perk and says "You were caught stealing!".
        /// </summary>
        [Test]
        public void Pickpocket_Caught_VictimAttacksForTheft_RemembersIt_AndTheHudSaysSo_TooHard_NamesThePerk()
        {
            var victim = new ActorId("Tjalf");
            var strong = new ActorId("Skalveram");
            var set = new TreasureSetDef("TS_PickPocket_Tjalf", TreasureUse.Pickpocket, 1, 1, new[] { new TreasureStack("It_Key_Tjalf", 1, 0) });
            using (var f = Fixture()
                .Add(SimulationSchema.NpcFolder, Npc(victim, 1, "Human", "Reb", "Faring", levelMax: 10), Npc(strong, 3, "Human", "Reb", "Faring", levelMax: 35))
                .Add(RulesSchema.TreasureFolder, set)
                .Add(RulesSchema.PickpocketFolder, new PickpocketDef(victim, new[] { set.Id }), new PickpocketDef(strong, new[] { set.Id })))
            {
                var s = f.NewGame();
                using var notices = new GameNotices(s);
                var posted = new List<(string text, NoticeKind kind)>();
                notices.Posted += (text, kind) => posted.Add((text, kind));
                var attempts = new List<PickpocketAttempted>();
                using var sub = s.Events.Subscribe<PickpocketAttempted>(e => attempts.Add(e));
                s.Progression.LearnSkill(PickpocketRules.OfferPerk);

                Assert.That(s.Pickpockets.Pickpocket(strong), Is.EqualTo(PickpocketVerdict.TooHard), "level 35 needs Perk_PickPocket_2");
                Assert.That(attempts.Last().MissingPerk, Is.EqualTo(PickpocketRules.HardPerk));
                string missing = notices.ShortfallText(new LearnShortfall(LearnShortfallKind.Skill, PickpocketRules.HardPerk, 0));
                Assert.That(posted.Last(), Is.EqualTo((missing, NoticeKind.Error)), "the HUD names the missing perk");
                Assert.That(s.NpcCombat.Get(strong)?.IsFighting ?? false, Is.False, "a refusal is no crime");
                Assert.That(s.Crime.Ledger.PersonalOf(strong), Is.EqualTo(CrimeType.None));

                s.Hero.Stats.Set(PickpocketRules.TheftAttribute, new StatValue(-400)); // -200 - 10 + 85 < any roll
                Assert.That(s.Pickpockets.Pickpocket(victim), Is.EqualTo(PickpocketVerdict.Caught));
                var brain = s.NpcCombat.Get(victim);
                Assert.That(brain.IsFighting, Is.True, "the victim attacks");
                Assert.That(brain.State.Target, Is.EqualTo(ActorId.Player));
                Assert.That(brain.State.Reason, Is.EqualTo(AttackReason.Theft), "its shout is \"DirtyThief\"");
                Assert.That(BattleLines.AttackReasonLine(AttackReason.Theft, false, true, "Human", false, null), Is.EqualTo("DirtyThief"));
                Assert.That(s.Crime.Ledger.PersonalOf(victim), Is.EqualTo(CrimeType.Theft), "G3 AssessTarget memorizes the theft");
                Assert.That(s.Crime.Ledger.Enclave("Faring").CountOf(CrimeType.Theft), Is.EqualTo(0), "nothing was stolen");
                Assert.That(s.Items.Inventories.Of(ActorId.Player).Has("It_Key_Tjalf"), Is.False);
                Assert.That(s.Pickpockets.WasRobbed(victim), Is.False);
                Assert.That(attempts.Last().Verdict, Is.EqualTo(PickpocketVerdict.Caught));
                Assert.That(posted.Last().kind, Is.EqualTo(NoticeKind.Error));
                Assert.That(posted.Last().text, Is.EqualTo(s.Localizer.Text(new TextId(GameNotices.PickpocketCaughtText))));
                Assert.That(PatchStrings.TryGet("Russian", GameNotices.PickpocketCaughtText, out var ru) && ru == "Вас поймали на краже!", Is.True);
                Assert.That(PatchStrings.TryGet("English", GameNotices.PickpocketCaughtText, out var en) && en == "You were caught stealing!", Is.True);
            }
        }

        [Test]
        public void Revolution_MembersOfAnEnclaveInAttack_FightTheOppressed()
        {
            var orc = new ActorId("Faring_Orc");
            var slave = new ActorId("Faring_Slave");
            using (var f = Fixture().Add(SimulationSchema.NpcFolder, Npc(orc, 0), Npc(slave, 3, "Human", "Reb", "Faring")))
            {
                var s = f.NewGame();
                s.Npcs.SetDetail(orc, DetailLevel.Active);
                s.Npcs.SetDetail(slave, DetailLevel.Active);
                Assert.That(s.WorldState.Revolution.Check(), Is.EqualTo(0), "the enclave is quiet");

                s.WorldState.Director.SetEnclaveFlag("Faring", EnclaveFlag.Revolution, true);
                s.WorldState.Director.StartRevolution(orc);
                Assert.That(s.Crime.Attitudes.Of(orc, slave), Is.EqualTo(Attitude.Hostile), "revolution attitude: loyal vs oppressed");
                Assert.That(s.WorldState.Revolution.Check(), Is.GreaterThan(0));
                Assert.That(s.NpcCombat.Get(orc)?.IsFighting, Is.True);
            }
        }
    }
}
