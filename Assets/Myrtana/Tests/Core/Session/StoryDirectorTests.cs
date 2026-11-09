using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The session carries out the story's requests to actors (StoryActorDirector) and saves what they changed.</summary>
    public class StoryDirectorTests
    {
        static SessionFixture Fixture(params DialogCommand[] commands) => new SessionFixture()
            .Add(StorySchema.DialogFolder, new DialogEntryDef(new Id<DialogEntryDef>("NEWS"), SessionFixture.Npc, default, DialogEntryKind.News,
                                                              0, default, false, false, false, null, null, null, commands));

        [Test]
        public void SetAlignment_ChangesThePoliticalFameGate_AndIsSaved()
        {
            using (var f = Fixture(new SetAlignment(ActorRef.Partner, "Reb"), new SetRole(ActorRef.Partner, ActorRole.Party, true)))
            {
                var s = f.NewGame();
                s.Fame.Add(FameKind.Political, "Reb", 80);
                Assert.That(s.Crime.Reputation.PoliticalFameOf(SessionFixture.Npc), Is.Zero, "no alignment yet");
                s.Dialog.Talk(SessionFixture.Npc);
                Assert.That(s.Crime.Reputation.PoliticalFameOf(SessionFixture.Npc), Is.EqualTo(80));

                var loaded = f.Reload(s);
                Assert.That(loaded.Crime.Reputation.PoliticalFameOf(SessionFixture.Npc), Is.EqualTo(80));
                Assert.That(loaded.Items.Roles.Has(SessionFixture.Npc, ActorRole.Party), Is.True);
            }
        }

        [Test]
        public void StartFight_EngagesTheNpcAgainstTheHero()
        {
            using (var f = Fixture(new StartFight(ActorRef.Partner, ActorRef.Player, AttackReason.Duel)))
            {
                var s = f.NewGame();
                s.Dialog.Talk(SessionFixture.Npc);
                var brain = s.NpcCombat.Get(SessionFixture.Npc);
                Assert.That(brain, Is.Not.Null);
                Assert.That(brain.State.Target, Is.EqualTo(ActorId.Player));
            }
        }

        [Test]
        public void Sleep_SkipsToTheHour()
        {
            using (var f = Fixture(new Sleep(20)))
            {
                var s = f.NewGame();
                s.Dialog.Talk(SessionFixture.Npc);
                Assert.That(s.Clock.HourOfDay, Is.EqualTo(20f).Within(0.01f));
                Assert.That(s.Dialog.IsActive, Is.False);
            }
        }
    }
}
