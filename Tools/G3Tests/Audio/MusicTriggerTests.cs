using System.Linq;
using G3.Audio;
using NUnit.Framework;

namespace G3Tests.Audio
{
    /// <summary>
    /// The music sequencer with G3's per-situation trigger parameters (Docs/audio-runtime.md "Music rules").
    /// Compiled only here (the Unity test assembly does not reference G3.Audio.Logic).
    /// </summary>
    [TestFixture]
    public class MusicTriggerTests
    {
        static MusicFragmentData Piece(string loc, string sit, int connectIn, int connectOut, string next = null)
        {
            var f = new MusicFragmentData { location = loc, situation = sit, dayTime = "day", variation = "0", nextVariation = next,
                clip = $"{loc}_{sit}_day_01", duration = 60f };
            f.links.Add(new MusicLinkData { kind = MusicLinkKind.Entrance, time = 10f, connectIn = connectIn });
            f.links.Add(new MusicLinkData { kind = MusicLinkKind.Entrance, time = 30f, connectIn = connectIn });
            for (int t = 5; t < 60; t += 8) f.links.Add(new MusicLinkData { kind = MusicLinkKind.Exit, time = t, connectOut = connectOut });
            return f;
        }

        static MusicCatalog Catalog()
        {
            var c = new MusicCatalog();
            c.fragments.Add(Piece("faring", "explore", 23, 0));
            c.fragments.Add(Piece("myrtana", "fight", 6, 30));
            c.fragments.Add(Piece("myrtana", "revolutionwon", 6, 0, "-1"));
            c.transitions.Add(new MusicTransitionData { id = "t23", clip = "transition_23", connectIn = 23, connectOut = 23, offset = 1.3f,
                fadeOutBegin = 0.3f, fadeOutEnd = 2f, fadeInBegin = 0.1f, fadeInEnd = 1.4f, duration = 3.2f });
            c.transitions.Add(new MusicTransitionData { id = "t06", clip = "transition_06", connectIn = 6, connectOut = 6, offset = 1f, duration = 3f });
            c.transitions.Add(new MusicTransitionData { id = "t30", clip = "transition_30", connectIn = 0, connectOut = 30, offset = 1f, duration = 3f });
            return c;
        }

        static MusicSequencer Running(MusicCatalog c, out double now)
        {
            var s = new MusicSequencer(c, new System.Random(1));
            s.SetTarget(new MusicState("faring", "explore", "day"), MusicTriggers.Explore);
            now = 0;
            s.Update(now);
            now = 7.5;   // between exits 5 and 13
            s.Update(now);
            return s;
        }

        [Test]
        public void TriggerTableMatchesG3()
        {
            Assert.AreEqual(MusicEntrance.RandomEntrance, MusicTriggers.ForSituation("explore").Entrance);
            Assert.IsFalse(MusicTriggers.ForSituation("fight").UseTransition);
            Assert.AreEqual(MusicTriggerTime.Immediately, MusicTriggers.ForSituation("Showdown").Time);
            Assert.IsTrue(MusicTriggers.ForSituation("revolutionwon").DisableTriggers);
            Assert.AreEqual(MusicTriggerTime.NextExit, MusicTriggers.ForSituation("innos").Time);
        }

        [Test]
        public void FightStartsAtOnceWithACrossfadeFromTheBeginning()
        {
            var s = Running(Catalog(), out double now);
            s.SetTarget(new MusicState("myrtana", "fight", "day"), MusicTriggers.Fight);
            s.Update(now);
            Assert.AreEqual("myrtana_fight_day_01", s.Next.Clip);
            Assert.That(s.Next.Start, Is.LessThan(now + 0.5));
            Assert.AreEqual(0, s.Next.Offset, 1e-6);
            Assert.IsFalse(s.Cues.Any(c => c.Transition != null));
        }

        [Test]
        public void ExploreWaitsForTheNextExitAndUsesATransition()
        {
            var s = Running(Catalog(), out double now);
            s.SetTarget(new MusicState("myrtana", "fight", "day"), MusicTriggers.Fight);
            s.Update(now);
            now = 20; s.Update(now);   // fight playing
            s.SetTarget(new MusicState("faring", "explore", "day"), MusicTriggers.Explore);
            s.Update(now);
            var t = s.Cues.Single(c => c.Transition != null);
            Assert.AreEqual("transition_30", t.Clip);
            Assert.That(t.Start + 1.0, Is.GreaterThan(now));   // its offset lands on a future exit link
            Assert.That(s.Next.Offset, Is.EqualTo(10).Or.EqualTo(30).Within(1e-6));   // a random entrance
        }

        [Test]
        public void VictoryRefusesOtherTriggersUntilItHasPlayed()
        {
            var s = Running(Catalog(), out double now);
            Assert.IsTrue(s.SetTarget(new MusicState("myrtana", "revolutionwon", "day"), MusicTriggers.Victory));
            Assert.IsFalse(s.AcceptsTriggers);
            Assert.IsFalse(s.SetTarget(new MusicState("faring", "explore", "day"), MusicTriggers.Explore));
            for (double t = now; t < 200 && !s.AcceptsTriggers; t += 0.5) s.Update(t);
            Assert.IsTrue(s.AcceptsTriggers);
            Assert.IsTrue(s.SetTarget(new MusicState("faring", "explore", "day"), MusicTriggers.Explore));
        }
    
        // ---------------------------------------------------------------- fix-177: no fight music after the fight

        static readonly MusicState Explore = new MusicState("faring", "explore", "day"), Fight = new MusicState("myrtana", "fight", "day"),
            Victory = new MusicState("myrtana", "revolutionwon", "day");

        static bool FightAudible(MusicSequencer s, double t) =>
            s.Cues.Any(c => c.Fragment != null && c.Fragment.situation == "fight" && t >= c.Start && t < c.End && c.Gain.Evaluate(t) > 0.001f);

        [Test]
        public void FightEndingBeforeItsPieceStartsIsCancelled()
        {
            var s = Running(Catalog(), out double now);
            var explore = s.Current;
            s.SetTarget(Fight, MusicTriggers.Fight);
            s.Update(now);
            Assert.AreEqual("myrtana_fight_day_01", s.Next.Clip);   // pending: starts after MinLead
            MusicCue cancelled = null;
            s.Cancelled += c => cancelled = c;
            s.SetTarget(Explore, MusicTriggers.Explore);
            s.Update(now + 0.05);
            Assert.IsNotNull(cancelled);
            Assert.IsNull(s.Next);
            Assert.AreSame(explore, s.Current);
            Assert.IsFalse(explore.FadingOut);
            Assert.AreEqual(explore.SampleEnd, explore.End, 1e-6);
            for (double t = now + 0.05; t < now + 30; t += 0.1)
            {
                s.Update(t);
                Assert.IsFalse(FightAudible(s, t), $"fight music at {t:F1}");
                Assert.AreEqual(1f, explore.Gain.Evaluate(t), 1e-4, "explore keeps full volume");
            }
        }

        [Test]
        public void FightEndingDuringItsFadeInReturnsToTheAmbientPiece()
        {
            var s = Running(Catalog(), out double now);
            var explore = s.Current;
            s.SetTarget(Fight, MusicTriggers.Fight);
            s.Update(now);
            now += 1.0; s.Update(now);   // one situation tick later: the fight piece has started, still fading in
            Assert.AreEqual("myrtana_fight_day_01", s.Current.Clip);
            var fight = s.Current;
            s.SetTarget(Explore, MusicTriggers.Explore);
            s.Update(now);
            Assert.AreSame(explore, s.Current);
            Assert.IsTrue(fight.FadingOut);
            Assert.That(fight.End, Is.LessThanOrEqualTo(now + s.FallbackCrossfade + 1e-6));
            Assert.IsFalse(s.Cues.Any(c => c.Transition != null), "no transition back from fight");
            for (double t = now + s.FallbackCrossfade; t < now + 30; t += 0.1)
            {
                s.Update(t);
                Assert.IsFalse(FightAudible(s, t), $"fight music at {t:F1}");
            }
            Assert.AreEqual(1f, explore.Gain.Evaluate(now + 30), 1e-4);
            Assert.AreEqual("faring_explore_day_01", s.Current.Clip);
        }

        [Test]
        public void FightThatHasFadedInLeavesThroughTheNextExitAsBefore()
        {
            var s = Running(Catalog(), out double now);
            s.SetTarget(Fight, MusicTriggers.Fight);
            s.Update(now);
            now += 5; s.Update(now);   // crossfade done
            MusicCue cancelled = null;
            s.Cancelled += c => cancelled = c;
            s.SetTarget(Explore, MusicTriggers.Explore);
            s.Update(now);
            Assert.IsNull(cancelled);
            Assert.AreEqual("myrtana_fight_day_01", s.Current.Clip);
            Assert.AreEqual("transition_30", s.Cues.Single(c => c.Transition != null).Clip);
        }

        [Test]
        public void ReturningFightCancelsThePendingExitFromFight()
        {
            var s = Running(Catalog(), out double now);
            s.SetTarget(Fight, MusicTriggers.Fight);
            s.Update(now);
            now = 20; s.Update(now);
            var fight = s.Current;
            s.SetTarget(Explore, MusicTriggers.Explore);   // waits for the next exit of the fight piece
            s.Update(now);
            Assert.IsNotNull(s.Next);
            s.SetTarget(Fight, MusicTriggers.Fight);       // the fight goes on before the transition is heard
            s.Update(now + 0.5);
            Assert.AreSame(fight, s.Current);
            Assert.IsNull(s.Next);
            Assert.IsFalse(fight.FadingOut);
            Assert.IsFalse(s.Cues.Any(c => c.Transition != null));
        }

        [Test]
        public void PendingChangeIsReplannedForANewTarget()
        {
            var s = Running(Catalog(), out double now);   // 7.5 s: next exit at 13 s
            var explore = s.Current;
            s.SetTarget(new MusicState("myrtana", "fight", "day"), new MusicTrigger(MusicEntrance.Begin, MusicTriggerTime.NextExit, true));
            s.Update(now);
            Assert.That(s.Next.Start, Is.GreaterThan(now + 1));
            s.SetTarget(Victory, MusicTriggers.Situation);
            s.Update(now + 0.1);
            Assert.AreSame(explore, s.Current);
            Assert.AreEqual("myrtana_revolutionwon_day_01", s.Next.Clip);
            Assert.IsFalse(s.Cues.Any(c => c.Fragment != null && c.Fragment.situation == "fight"), "the dropped fight cue is gone");
        }
}
}
