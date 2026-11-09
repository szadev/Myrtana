using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The hero's party (G3 CP 1.75 rules, Docs/party.md): topics, the join limit, waiting, teleports, deliveries, guides.</summary>
    public class PartyTests
    {
        static readonly ActorId Gorn = new ActorId("Gorn"), Diego = new ActorId("Diego"), Slave = new ActorId("Lago_Slave_03"),
                                Mob = new ActorId("Fireclan_Mob"), Tragak = new ActorId("Tragak");

        static (PlayerParty Party, ActorRoles Roles, List<PartyChanged> Changes) NewParty()
        {
            var roles = new ActorRoles(new ContentDatabase());
            var events = new EventBus();
            var changes = new List<PartyChanged>();
            events.Subscribe<PartyChanged>(changes.Add);
            return (new PlayerParty(roles, events), roles, changes);
        }

        // ---------------------------------------------------------------- rules

        [Test]
        public void Topics_FollowTheDialogFlagsAndMembership()
        {
            var (party, roles, _) = NewParty();
            Assert.That(party.Offers(Gorn, PartyOffer.Join), Is.False, "PartyEnabled off");
            roles.Set(Gorn, ActorRole.Party, true);
            Assert.That(party.Offers(Gorn, PartyOffer.Join), Is.True);
            Assert.That(party.Offers(Gorn, PartyOffer.Dismiss), Is.False, "not led yet");

            party.Join(Gorn, PartyMemberType.Party);
            Assert.That(party.Offers(Gorn, PartyOffer.Join), Is.False, "already following");
            Assert.That(party.Offers(Gorn, PartyOffer.Dismiss), Is.True);
            Assert.That(party.Offers(Gorn, PartyOffer.Wait), Is.True);
            Assert.That(party.Offers(Gorn, PartyOffer.Companion), Is.True);

            party.Wait(Gorn);
            Assert.That(party.Offers(Gorn, PartyOffer.Wait), Is.False, "already waiting");
            Assert.That(party.Offers(Gorn, PartyOffer.Join), Is.True, "a waiting member may be asked again");
            party.Join(Gorn, PartyMemberType.Party);
            Assert.That(party.IsWaiting(Gorn), Is.False);

            roles.Set(Gorn, ActorRole.Party, false);
            Assert.That(party.Offers(Gorn, PartyOffer.Dismiss), Is.False, "Dismiss needs PartyEnabled (or a controlled member)");

            roles.Set(Mob, ActorRole.Mob, true);
            Assert.That(party.Offers(Mob, PartyOffer.MobJoin), Is.True);
            party.Join(Mob, PartyMemberType.Mob);
            Assert.That(party.Offers(Mob, PartyOffer.MobJoin), Is.False);
            Assert.That(party.Offers(Mob, PartyOffer.MobDismiss), Is.True);

            roles.Set(Slave, ActorRole.Slavery, true);
            Assert.That(party.Offers(Slave, PartyOffer.SlaveJoin), Is.True);
            party.Join(Slave, PartyMemberType.Slave);
            Assert.That(party.Offers(Slave, PartyOffer.SlaveJoin), Is.False);
            Assert.That(party.Offers(Slave, PartyOffer.Companion), Is.True);
        }

        /// <summary>
        /// fix-100 (owner: "I told Kliff to wait, now I can't do anything with him"): CP AreConditionsFulfilled cases 14 Join,
        /// 28 MobJoin and 29 SlaveJoin share one test — the flag and not (leader = player and not waiting). A waiting
        /// slave or mob member is offered its join topic again; joining resumes following and keeps the member type.
        /// </summary>
        [Test]
        public void AWaitingSlaveOrMobMember_IsOfferedItsJoinTopicAgain_AndFollowsAgain()
        {
            var (party, roles, _) = NewParty();
            roles.Set(Slave, ActorRole.Slavery, true);
            roles.Set(Mob, ActorRole.Mob, true);
            party.Join(Slave, PartyMemberType.Slave);
            party.Join(Mob, PartyMemberType.Mob);
            Assert.That(party.Offers(Slave, PartyOffer.Wait), Is.True);
            party.Wait(Slave);
            party.Wait(Mob);

            Assert.That(party.Offers(Slave, PartyOffer.SlaveJoin), Is.True, "Kliff waiting: 'Follow me' again");
            Assert.That(party.Offers(Mob, PartyOffer.MobJoin), Is.True);
            Assert.That(party.Offers(Slave, PartyOffer.Wait), Is.False, "already waiting");
            Assert.That(party.Offers(Slave, PartyOffer.Join), Is.False, "no PartyEnabled");

            party.Join(Slave, PartyMemberType.Slave);
            Assert.That(party.IsWaiting(Slave), Is.False);
            Assert.That(party.IsLedByPlayer(Slave), Is.True);
            Assert.That(party.Offers(Slave, PartyOffer.SlaveJoin), Is.False, "follows again");
            Assert.That(party.Offers(Slave, PartyOffer.Wait), Is.True);
        }

        [Test]
        public void OnlyOneActiveCompanion_WaitingOrDistantOnesAreDismissed()
        {
            var (party, roles, changes) = NewParty();
            party.Join(Gorn, PartyMemberType.Party);
            party.Join(Slave, PartyMemberType.Slave);
            Assert.That(party.HasRoomFor(Diego), Is.False, "Gorn follows the hero");
            Assert.That(party.ActiveCompanionOtherThan(Diego), Is.EqualTo(Gorn));

            party.Wait(Gorn);
            Assert.That(party.HasRoomFor(Diego), Is.True, "slaves and mob members do not count, waiting companions neither");
            party.Join(Diego, PartyMemberType.Party);
            Assert.That(party.IsLedByPlayer(Gorn), Is.False, "the waiting companion is dismissed");
            Assert.That(party.IsLedByPlayer(Slave), Is.True);
            Assert.That(changes.Any(c => c.Member == Gorn && c.Change == PartyChange.Dismissed && c.Message.Equals(PlayerParty.PartyDismissMessage)));

            party.InProcessingRange = id => id != Diego;
            Assert.That(party.HasRoomFor(Gorn), Is.True, "a companion out of the processing range does not block");
        }

        [Test]
        public void FollowDistance_IsSlotPlusThreeTimesOneAndAHalfMetres()
        {
            var (party, _, _) = NewParty();
            party.Join(Gorn, PartyMemberType.Party);
            party.Join(Slave, PartyMemberType.Slave);
            party.Join(Mob, PartyMemberType.Mob);
            Assert.That(party.FollowDistance(Gorn), Is.EqualTo(4.5f));
            Assert.That(party.FollowDistance(Slave), Is.EqualTo(6f));
            party.Wait(Gorn);
            Assert.That(party.FollowSlot(Gorn), Is.EqualTo(-1));
            Assert.That(party.FollowDistance(Gorn), Is.EqualTo(5f), "not following: 500 cm");
            Assert.That(party.FollowDistance(Slave), Is.EqualTo(4.5f), "moves up a slot");
            Assert.That(party.FollowDistance(Mob), Is.EqualTo(6f));
        }

        [Test]
        public void Teleport_MembersWaitBehind_DeathsAndDeliveriesRelease()
        {
            var (party, roles, changes) = NewParty();
            roles.Set(Tragak, ActorRole.Party, true);
            party.Join(Gorn, PartyMemberType.Party);
            party.Join(Tragak, PartyMemberType.Party);
            party.Join(Mob, PartyMemberType.Controlled);
            party.PlayerTeleported();
            Assert.That(party.IsWaiting(Gorn) && party.IsWaiting(Tragak), Is.True);
            Assert.That(party.IsLedByPlayer(Mob), Is.False, "a controlled member is released");
            Assert.That(changes.Count(c => c.Change == PartyChange.Waits && c.Message.Equals(PlayerParty.WaitMessage)), Is.EqualTo(2));

            party.Delivered(Tragak);
            Assert.That(party.IsLedByPlayer(Tragak), Is.False);
            Assert.That(roles.Has(Tragak, ActorRole.Party), Is.False, "StopBringNPC clears PartyEnabled");

            party.MemberDied(Gorn);
            Assert.That(party.Members, Is.Empty);
        }

        [Test]
        public void Teleport_PortOption_FollowingMembersComeAlong_WaitingStayAndControlledGo()
        {
            var (party, roles, changes) = NewParty();
            roles.Set(Tragak, ActorRole.Party, true);
            party.FollowsHeroOnTeleport = true; // PortOptions.CompanionsFollowTeleport (player feedback item 5)
            party.Join(Gorn, PartyMemberType.Party);
            party.Join(Tragak, PartyMemberType.Party);
            party.Join(Mob, PartyMemberType.Controlled);
            party.Wait(Tragak);
            int waitsBefore = changes.Count(c => c.Change == PartyChange.Waits);
            party.PlayerTeleported();
            Assert.That(party.IsLedByPlayer(Gorn) && !party.IsWaiting(Gorn), Is.True, "a following companion keeps following");
            Assert.That(party.IsWaiting(Tragak), Is.True, "a member told to wait keeps waiting");
            Assert.That(party.IsLedByPlayer(Mob), Is.False, "a controlled member is released as in G3");
            Assert.That(changes.Count(c => c.Change == PartyChange.Waits), Is.EqualTo(waitsBefore), "no wait message");
            Assert.That(party.FollowSlot(Gorn), Is.EqualTo(0));
        }

        [Test]
        public void HeroSide_IsTheHeroHisMembersAndTheGuide()
        {
            var (party, _, _) = NewParty();
            party.Join(Gorn, PartyMemberType.Party);
            party.StartGuide(Diego, "FP_Reddock", "Diego_Guide");
            Assert.That(party.IsOnHeroSide(ActorId.Player), Is.True);
            Assert.That(party.IsOnHeroSide(Gorn), Is.True);
            Assert.That(party.IsOnHeroSide(Diego), Is.True, "the guide and a follower never fight each other");
            Assert.That(party.IsOnHeroSide(Mob), Is.False);
        }

        [Test]
        public void FightWithinSide_EndsOnceTheOpponentStartsGuiding()
        {
            var (party, _, _) = NewParty();
            party.Join(Gorn, PartyMemberType.Party);
            Assert.That(party.IsFightWithinSide(Gorn, Diego), Is.False, "Diego is not the guide yet: a real enemy");
            party.StartGuide(Diego, "FP_Reddock", "Diego_Guide");
            Assert.That(party.IsFightWithinSide(Gorn, Diego), Is.True, "the follower's fight with the new guide ends");
            Assert.That(party.IsFightWithinSide(Diego, Gorn), Is.True, "and the guide's with the follower");
            Assert.That(party.IsFightWithinSide(Gorn, ActorId.Player), Is.False, "a fight with the hero himself is not touched");
            Assert.That(party.IsFightWithinSide(Gorn, Mob), Is.False);
            party.StopGuide();
            Assert.That(party.IsFightWithinSide(Gorn, Diego), Is.False);
        }

        [Test]
        public void EnclaveAlarm_CompanionsOfThatEnclaveLeave()
        {
            var (party, _, _) = NewParty();
            party.Join(Gorn, PartyMemberType.Party);
            party.Join(Mob, PartyMemberType.Mob);
            party.StartGuide(Diego, "FP_Reddock", "Diego_Guide");
            party.EnclaveTurnedAgainstPlayer("Fireclan", id => id == Mob || id == Diego ? "Fireclan" : null);
            Assert.That(party.Members.Select(m => m.Id), Is.EqualTo(new[] { Gorn }));
            Assert.That(party.Guide, Is.Null);
        }

        [Test]
        public void SaveSection_KeepsMembersOrderWaitingAndGuide()
        {
            var (party, roles, _) = NewParty();
            party.Join(Gorn, PartyMemberType.Party);
            party.Join(Slave, PartyMemberType.Slave);
            party.Wait(Gorn);
            party.StartGuide(Diego, "FP_Reddock", "Gorn_ShowReddock");
            var stream = new MemoryStream();
            new PartySaveSection(party).Write(new BinarySaveWriter(stream));
            stream.Position = 0;
            var loaded = new PlayerParty(roles);
            new PartySaveSection(loaded).Read(new BinarySaveReader(stream), 1);
            Assert.That(loaded.Members.Select(m => m.ToString()), Is.EqualTo(new[] { "Gorn Party waiting", "Lago_Slave_03 Slave" }));
            Assert.That(loaded.Guide.Npc, Is.EqualTo(Diego));
            Assert.That(loaded.Guide.Destination, Is.EqualTo("FP_Reddock"));
            Assert.That(loaded.FollowDistance(Slave), Is.EqualTo(4.5f));
        }

        // ---------------------------------------------------------------- dialogs

        static StoryFixture Fixture() => new StoryFixture()
            .Define(new SharedLines(SharedLines.DefinitionId, "SVM_{voice}_{line}"))
            .Define(new NpcDef(Tragak, new TextId("FO_Tragak"), "Hum_Mid"));

        static DialogEntryDef Entry(string id, Condition condition, IReadOnlyList<PreCheck> preChecks = null, ActorId? owner = null,
                                    params DialogCommand[] commands) =>
            new DialogEntryDef(new Id<DialogEntryDef>(id), owner ?? StoryFixture.Npc, default, DialogEntryKind.Choice, 0, default, true, false,
                               false, null, condition, preChecks, commands);

        [Test]
        public void JoinTopic_IsRefusedWhileAnotherCompanionFollows()
        {
            var f = Fixture();
            f.Entry(Entry("join", new PartyAllows(ActorRef.Partner, PartyOffer.Join),
                          new PreCheck[] { new PartyHasRoom(ActorRef.Partner, new DialogCommand[] { new PartyRefusal() }) },
                          null, new JoinParty(ActorRef.Partner, PartyMemberType.Party)));
            f.Entry("other", repeatable: true).Entry("other2", repeatable: true);
            var messages = new List<GameMessage>();
            f.Events.Subscribe<GameMessage>(messages.Add);
            var _ = f.Dialog;
            f.Items.Roles.Set(StoryFixture.Npc, ActorRole.Party, true);
            f.Party.Join(Gorn, PartyMemberType.Party);

            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Contain("join"));
            f.Choose("join");
            f.FinishLines();
            Assert.That(f.Party.IsLedByPlayer(StoryFixture.Npc), Is.False);
            Assert.That(messages.Single().Text, Is.EqualTo(PlayerParty.NoPartyMessage));
            Assert.That(messages.Single().Subject, Is.EqualTo(Gorn), "$(name) is the companion with the hero");

            f.Party.Wait(Gorn);
            f.Choose("join");
            f.FinishLines();
            Assert.That(f.Party.IsFollowing(StoryFixture.Npc), Is.True);
            Assert.That(f.Party.IsLedByPlayer(Gorn), Is.False);
            Assert.That(f.OfferedIds(), Does.Not.Contain("join"));
        }

        [Test]
        public void BringNpcDelivery_NeedsTheNpcLedAndNear_ThenSucceedsAndReleasesIt()
        {
            var quest = new QuestDef(new Id<QuestDef>("Tragak_Bring"), new TextId("QUEST_Tragak"), null,
                                     new QuestObjective(QuestObjectiveKind.BringNpc, new[] { new QuestTarget("Tragak", 1) }, StoryFixture.Npc));
            var f = Fixture().Quest(quest);
            var running = new QuestStatusIs(quest.Id, new[] { QuestStatus.Running });
            // As the importer translates Grompel's Delivery info: CondOwnerNearEntity=Tragak, BringNpc test (led, 20 m).
            f.Entry(Entry("delivery", new AllOf(new Condition[]
            {
                running, new InPlayerParty(new[] { ActorRef.To(Tragak) }), new Near(ActorRef.To(Tragak), "Gnar", 20),
                new Near(ActorRef.Partner, "Tragak", 5),
            }), null, null, new SetRole(ActorRef.To(Tragak), ActorRole.Party, false), new CompleteDelivery(quest.Id)));
            f.Entry("other", repeatable: true).Entry("other2", repeatable: true);
            var _ = f.Dialog;
            f.Quests.TrySetStatus(quest.Id, QuestStatus.Running);
            f.Items.Roles.Set(Tragak, ActorRole.Party, true);
            f.Positions.Put("Gnar", 0, 0).Put("Tragak", 30, 0);

            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Not.Contain("delivery"), "not led by the hero");
            f.Dialog.Abort();
            f.Party.Join(Tragak, PartyMemberType.Party);
            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Not.Contain("delivery"), "30 m away");
            f.Dialog.Abort();
            f.Positions.Put("Tragak", 4, 0);
            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Contain("delivery"));
            f.Choose("delivery");
            f.FinishLines();
            Assert.That(f.Quests.StatusOf(quest.Id), Is.EqualTo(QuestStatus.Success));
            Assert.That(f.Party.IsLedByPlayer(Tragak), Is.False, "StopBringNPC");
        }

        [Test]
        public void FollowNpcQuest_StartsTheGuide_OneAtATime_AndTheDeliveryStopsIt()
        {
            QuestDef Follow(string id, string guide, string destination) =>
                new QuestDef(new Id<QuestDef>(id), new TextId("QUEST_" + id), null,
                             new QuestObjective(QuestObjectiveKind.FollowNpc, new[] { new QuestTarget(guide, 1) }, new ActorId(destination)));
            var wolf = Follow("Mort_GuideWolf", "Gnar", "FP_Wolfclan");
            var mine = Follow("Mort_GuideMine", "Tragak", "FP_Mine_Valley");
            var f = Fixture().Quest(wolf).Quest(mine);
            f.Entry(Entry("arrived", new AllOf(new Condition[]
            {
                new QuestStatusIs(wolf.Id, new[] { QuestStatus.Running }), new Near(ActorRef.To(StoryFixture.Npc), "FP_Wolfclan", 5),
            }), null, null, new CompleteDelivery(wolf.Id)));
            f.Entry("other", repeatable: true).Entry("other2", repeatable: true);
            var _ = f.Dialog;
            var guides = new FollowQuestGuides(f.Quests, f.Party, new[] { wolf, mine }, f.Events);

            f.Quests.TrySetStatus(mine.Id, QuestStatus.Running);
            Assert.That(f.Party.Guide.Npc, Is.EqualTo(Tragak));
            f.Quests.TrySetStatus(wolf.Id, QuestStatus.Running);
            Assert.That(f.Quests.StatusOf(mine.Id), Is.EqualTo(QuestStatus.Lost), "starting one FollowNpc quest loses the other");
            Assert.That(f.Party.Guide.Npc, Is.EqualTo(StoryFixture.Npc));
            Assert.That(f.Party.Guide.Destination, Is.EqualTo("FP_Wolfclan"));

            f.Positions.Put("Gnar", 0, 0).Put("FP_Wolfclan", 3, 4);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("arrived");
            f.FinishLines();
            Assert.That(f.Quests.StatusOf(wolf.Id), Is.EqualTo(QuestStatus.Success));
            Assert.That(f.Party.Guide, Is.Null, "StopGuidePlayer");

            f.Party.StartGuide(Tragak, "x", "nothing");
            f.Party.StopGuide();
            f.Quests.TrySetStatus(mine.Id, QuestStatus.Running); // Lost -> Running (Reactivator)
            f.Party.StopGuide();
            guides.Resume();
            Assert.That(f.Party.Guide.Npc, Is.EqualTo(Tragak), "after loading a Running quest gets its guide back");
        }

        /// <summary>
        /// fix-190 (sideq_kapdun_01: Wenzel, a KapDun member made Reb by BPANKRATZ31570, stood still for 150 s after the hero
        /// killed the orcs attacking him; quest Running, no guide): G3 NotifyEnclave resets the player's party leader and
        /// sets the first Running FollowNpc quest Lost; CP's NEWM0121 ("Let's attack!", Lost) starts it again.
        /// </summary>
        [Test]
        public void GuideOfAnAlarmedEnclave_TurnsAway_AndItsQuestIsLost_UntilStartedAgain()
        {
            var metzel = new QuestDef(new Id<QuestDef>("Wenzel_KapDunMetzel"), new TextId("QUEST_Metzel"), null,
                                      new QuestObjective(QuestObjectiveKind.FollowNpc, new[] { new QuestTarget("Tragak", 1) }, new ActorId("CheckPoint")));
            var f = Fixture().Quest(metzel);
            var _ = f.Dialog;
            var guides = new FollowQuestGuides(f.Quests, f.Party, new[] { metzel }, f.Events);
            f.Quests.TrySetStatus(metzel.Id, QuestStatus.Running);
            Assert.That(f.Party.Guide.Npc, Is.EqualTo(Tragak));

            f.Party.EnclaveTurnedAgainstPlayer("Silden", id => id == Tragak ? "Silden" : null);
            Assert.That(f.Party.Guide, Is.Null);
            Assert.That(f.Quests.StatusOf(metzel.Id), Is.EqualTo(QuestStatus.Lost), "G3: the FollowNpc quest is Lost");

            f.Party.EnclaveTurnedAgainstPlayer("KapDun", id => "Silden");
            Assert.That(f.Quests.StatusOf(metzel.Id), Is.EqualTo(QuestStatus.Lost), "another enclave's alarm changes nothing");

            f.Quests.TrySetStatus(metzel.Id, QuestStatus.Running); // NEWM0121 startQuest
            Assert.That(f.Party.Guide.Npc, Is.EqualTo(Tragak), "taken up again, it guides again");
            guides.Dispose();
        }

        /// <summary>
        /// fix-100 (owner: "I brought Kliff to Javier but the quest won't finish"): Javier's delivery BPANKRATZ31233 has
        /// CondOwnerNearEntity=Kliff. G3 gCInfo_PS::GetDistOwnerToEntity multiplies the distance to an entity with gCNPC_PS
        /// by 0.25, so with an NPC (or the hero) the 5 m test passes up to 20 m; a place stays 5 m. Ours took 5 m for
        /// every entity: Kliff following 4.5 m behind the hero stood 6.6 m from Javier (owner's save) and the topic never came.
        /// </summary>
        [Test]
        public void OwnerNearEntity_IsTwentyMetresForAnNpc_AndFiveForAPlace()
        {
            var kliff = new ActorId("Kliff");
            var actors = new Dictionary<string, ActorId> { [StoryFixture.Npc.Name] = StoryFixture.Npc, ["Kliff"] = kliff };
            var translator = new InfoConditionTranslator(new QuestImporter(new G3.Formats.Story.Quest[0]), ItemNames.None, new ImportReport());
            Condition Translate(string near) => translator.Translate(new InfoScope(
                new G3.Formats.Story.Info { Name = "BPANKRATZ31233", Owner = StoryFixture.Npc.Name, ConditionType = (int)G3.Formats.Story.InfoCondType.General, CondOwnerNearEntity = near },
                actors, new ImportReport()));

            Assert.That(((Near)Translate("Kliff")).Metres, Is.EqualTo(20f), "an NPC: 5 m / 0.25");
            Assert.That(((Near)Translate("PC_Hero")).Metres, Is.EqualTo(20f), "the hero has gCNPC_PS too");
            Assert.That(((Near)Translate("FP_Wolfclan")).Metres, Is.EqualTo(5f), "a place");

            var f = Fixture();
            var ctx = f.Conditions();
            f.Positions.Put(StoryFixture.Npc.Name, 0, 0).Put("Kliff", 6.6f, 0).Put("FP_Wolfclan", 6.6f, 0);
            Assert.That(Translate("Kliff").Evaluate(ctx), Is.True, "Kliff 6.6 m away behind the hero (owner's save018)");
            Assert.That(Translate("FP_Wolfclan").Evaluate(ctx), Is.False);
            f.Positions.Put("Kliff", 20.5f, 0);
            Assert.That(Translate("Kliff").Evaluate(ctx), Is.False);
        }

        [Test]
        public void Near_IsFalseForUnknownPositions_AndTrueForTheEntityItself()
        {
            var f = Fixture();
            var ctx = f.Conditions();
            Assert.That(new Near(ActorRef.Partner, "FP_Nowhere", 5).Evaluate(ctx), Is.False);
            Assert.That(new Near(ActorRef.Partner, "Gnar", 5).Evaluate(ctx), Is.True);
            f.Positions.Put("Gnar", 0, 0).Put("FP_Here", 0, 5);
            Assert.That(new Near(ActorRef.Partner, "FP_Here", 5).Evaluate(ctx), Is.True);
            Assert.That(new Near(ActorRef.Partner, "FP_Here", 4.9f).Evaluate(ctx), Is.False);
            Assert.That(new InPlayerParty(new[] { ActorRef.Partner }).Evaluate(ctx), Is.False);
            f.Party.Join(StoryFixture.Npc, PartyMemberType.Slave);
            Assert.That(new InPlayerParty(new[] { ActorRef.Partner }).Evaluate(ctx), Is.True);
        }
    }
}
