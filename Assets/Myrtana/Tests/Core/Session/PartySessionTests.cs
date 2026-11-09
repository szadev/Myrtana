using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The party in a whole session (Docs/party.md): scripted joins, attitudes, the simulation, teleports, deaths, save/load.</summary>
    public class PartySessionTests
    {
        static readonly ActorId Gorn = new ActorId("Gorn");

        static readonly ActorId Kliff = new ActorId("Kliff"), Mitch = new ActorId("Mitch"), Lares = new ActorId("Lares");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder, new NpcDef(Gorn, new TextId("FO_Gorn"), "Gorn", "Human", "visual", new WorldPose(20, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Kliff, new TextId("FO_Kliff"), "Kliff", "Human", "visual", new WorldPose(30, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Lares, new TextId("FO_Lares"), "Lares", "Human", "visual", new WorldPose(40, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Mitch, new TextId("FO_Mitch"), "Mitch", "Human", "visual", new WorldPose(60, 0, 0, 0)))
            .Add(RulesSchema.PlaceFolder, new WorldPlaceDef("FP_Reddock", new[] { 100f, 0f, 0f }, 0));

        [Test]
        public void ScriptedJoin_MakesACompanionTheSimulationEscorts_AndSurvivesSaveLoad()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                Assert.That(s.Party.Members.IsFollowing(Gorn), Is.True);
                Assert.That(s.Items.Roles.Has(Gorn, ActorRole.Party), Is.True, "can be dismissed and asked again");
                Assert.That(s.Crime.Attitudes.Facts.TryGetProfile(Gorn, out var profile) && profile.InPlayerParty, Is.True);
                Assert.That(s.Crime.Attitudes.Of(Gorn, ActorId.Player), Is.EqualTo(Attitude.Friendly));

                s.Crime.ReportPlayer(new WorldPose(50, 0, 0, 0));
                s.Tick(0.5f);
                s.Tick(0.5f);
                s.Npcs.TryGet(Gorn, out var agent);
                Assert.That(agent.Activity, Is.EqualTo(NpcActivity.Escorting));
                Assert.That(s.Npcs.PoseOf(agent).HorizontalDistanceTo(new WorldPose(50, 0, 0, 0)), Is.EqualTo(4.5f).Within(0.01f));

                var loaded = f.Reload(s);
                Assert.That(loaded.Party.Members.IsFollowing(Gorn), Is.True);
                loaded.Npcs.TryGet(Gorn, out var again);
                Assert.That(again.Activity, Is.EqualTo(NpcActivity.Escorting));
                Assert.That(loaded.Npcs.PoseOf(again).X, Is.EqualTo(45.5f).Within(0.01f));
            }
        }

        [Test]
        public void Teleport_LeavesTheCompanionWaiting_AndDeathRemovesIt()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                s.Events.Publish(new TeleportRequested(new TeleportDestinationDef("Teleport_Faring", new[] { 0f, 0f, 0f }, 0), "It_Teleport_Faring"));
                Assert.That(s.Party.Members.IsWaiting(Gorn), Is.True);
                Assert.That(s.Party.Escorts.ModeOf(Gorn), Is.EqualTo(EscortMode.Wait));
                s.Combat.Vitals.Kill(Gorn, byPlayerParty: false);
                Assert.That(s.Party.Members.IsLedByPlayer(Gorn), Is.False);
            }
        }

        [Test]
        public void Teleport_PortOption_TheCompanionComesAlong()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.Members.FollowsHeroOnTeleport = true; // PortOptions.CompanionsFollowTeleport
                s.Party.JoinCompanion(Gorn);
                s.Events.Publish(new TeleportRequested(new TeleportDestinationDef("Teleport_Faring", new[] { 0f, 0f, 0f }, 0), "It_Teleport_Faring"));
                Assert.That(s.Party.Members.IsWaiting(Gorn), Is.False);
                Assert.That(s.Party.Members.IsLedByPlayer(Gorn), Is.True);
                Assert.That(s.Party.Escorts.ModeOf(Gorn), Is.Not.EqualTo(EscortMode.Wait));
            }
        }

        [Test]
        public void Positions_KnowTheHeroNpcsAndNamedPlaces()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                Assert.That(s.Party.Positions.TryGetDistance(Gorn, "FP_Reddock", out float d), Is.True);
                Assert.That(d, Is.EqualTo(80f).Within(0.01f));
                Assert.That(s.Party.Positions.TryGetDistance(ActorId.Player, "FP_Reddock", out _), Is.False, "the hero is not placed yet");
                s.Crime.ReportPlayer(new WorldPose(97, 0, 4, 0));
                Assert.That(s.Party.Positions.TryGetDistance(ActorId.Player, "FP_Reddock", out d), Is.True);
                Assert.That(d, Is.EqualTo(5f).Within(0.01f));
                Assert.That(s.Party.Positions.TryGetDistance(Gorn, "FP_Nowhere", out _), Is.False);
            }
        }

        [Test]
        public void SaveLoad_KeepsMembersTypesWaitingOrderAndGuide()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Crime.ReportPlayer(new WorldPose(50, 0, 0, 0));
                s.Party.JoinCompanion(Kliff, PartyMemberType.Slave);
                s.Party.JoinCompanion(Gorn);
                s.Party.JoinCompanion(Lares, PartyMemberType.Mob);
                s.Party.Members.Wait(Gorn);
                s.Party.Members.StartGuide(Mitch, "FP_Reddock", "Mitch_Trolls");
                s.Tick(0.5f);
                s.Tick(0.5f);

                var loaded = f.Reload(s);
                var p = loaded.Party.Members;
                Assert.That(string.Join(",", System.Linq.Enumerable.Select(p.Members, m => m.ToString())),
                            Is.EqualTo("Kliff Slave,Gorn Party waiting,Lares Mob"));
                Assert.That(p.FollowSlot(Lares), Is.EqualTo(1), "join order kept");
                Assert.That(p.Guide, Is.Not.Null);
                Assert.That(p.Guide.Npc, Is.EqualTo(Mitch));
                Assert.That(p.Guide.Destination, Is.EqualTo("FP_Reddock"));
                Assert.That(p.Guide.Quest, Is.EqualTo("Mitch_Trolls"));
                Assert.That(loaded.Party.Escorts.ModeOf(Kliff), Is.EqualTo(EscortMode.Follow));
                Assert.That(loaded.Party.Escorts.ModeOf(Gorn), Is.EqualTo(EscortMode.Wait));
                Assert.That(loaded.Party.Escorts.ModeOf(Mitch), Is.EqualTo(EscortMode.Guide));
                loaded.Npcs.TryGet(Kliff, out var kliff);
                Assert.That(kliff.Activity, Is.EqualTo(NpcActivity.Escorting), "the follower keeps escorting after loading");
                loaded.Crime.ReportPlayer(new WorldPose(50, 0, 0, 0));
                loaded.Tick(0.5f);
                loaded.Tick(0.5f);
                Assert.That(loaded.Party.Members.IsFollowing(Kliff), Is.True, "nothing after the load dismisses it");
                Assert.That(loaded.Npcs.PoseOf(kliff).HorizontalDistanceTo(new WorldPose(50, 0, 0, 0)), Is.LessThan(10f));
            }
        }
    }
}
