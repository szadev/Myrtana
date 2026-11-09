using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-204 (owner: enemies noticed the hero high on a cliff or behind a rock wall): 3D distance, the height limit, sight
    /// through walls and the ground for humanoids and creatures, occluded hearing and sound broadcasts, the sized sight rays,
    /// and fight music only for an attacker that perceives the hero.
    /// </summary>
    public class PerceptionOcclusionTests
    {
        static readonly PerceptionRules Rules = G3CrimeImporter.Perception();
        static readonly ActorId Npc = new ActorId("Wolf#1");

        sealed class Wall : ILineOfSight
        {
            public bool IsClear(WorldPose from, WorldPose to) => false;
        }

        /// <summary>Records the sized query; blocked unless <see cref="Clear"/>.</summary>
        sealed class Probe : ISightLines
        {
            public bool Clear;
            public float Eye, Top;
            public int Calls;
            public bool IsClear(WorldPose from, WorldPose to) => IsClear(from, SightLines.HumanEyeHeight, to, SightLines.HumanTop);
            public bool IsClear(WorldPose from, float eyeHeight, WorldPose to, float targetTop)
            {
                Calls++;
                Eye = eyeHeight;
                Top = targetTop;
                return Clear;
            }
        }

        static SenseObserver Observer(bool humanoid) => new SenseObserver { Id = Npc, Pose = new WorldPose(0, 0, 0, 0), Humanoid = humanoid };

        static SenseTarget Hero(float x, float y, float z, bool moving = false) =>
            new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(x, y, z, 0), Moving = moving };

        static float PlayerRange => Rules.RangeOf(PerceptionSlot.Player);

        [Test]
        public void DistanceIs3D()
        {
            // 18 m away and 9 m up: 20.1 m in 3D, beyond the player slot's 20 m (a 2D distance would say 18 m)
            Assert.That(Senses.Distance(new WorldPose(0, 0, 0, 0), new WorldPose(0, 9, 18, 0)), Is.GreaterThan(PlayerRange));
            Assert.That(Senses.Perceive(Observer(true), Hero(0, 9, 18), PlayerRange, Rules, null), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(Observer(false), Hero(0, 9, 18), PlayerRange, Rules, null), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(Observer(true), Hero(0, 9, 16), PlayerRange, Rules, null), Is.EqualTo(Sense.Seen), "18.4 m");
        }

        [Test]
        public void NothingIsPerceivedAcrossMoreThanTenMetresOfHeight()
        {
            // straight above a wolf pack, 10.5 m up a cliff: close in 3D, yet beyond the senses' height limit
            Assert.That(Senses.Perceive(Observer(false), Hero(2, 10.5f, 0, moving: true), PlayerRange, Rules, null), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(Observer(true), Hero(0, 10.5f, 2, moving: true), PlayerRange, Rules, null), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(Observer(false), Hero(2, 9.5f, 0), PlayerRange, Rules, null), Is.EqualTo(Sense.Sensed), "9.5 m: G3 still senses");
        }

        [Test]
        public void HumanoidsDoNotSeeThroughWalls()
        {
            Assert.That(Senses.Perceive(Observer(true), Hero(0, 0, 15), PlayerRange, Rules, new Wall()), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(Observer(true), Hero(0, 0, 15), PlayerRange, Rules, null), Is.EqualTo(Sense.Seen));
        }

        [Test]
        public void CreaturesNeedSightBeyondTheirNose()
        {
            var wolf = Observer(false);
            Assert.That(Senses.Perceive(wolf, Hero(0, 0, -15), PlayerRange, Rules, new Wall()), Is.EqualTo(Sense.None), "15 m behind rock");
            Assert.That(Senses.Perceive(wolf, Hero(0, 0, -15), PlayerRange, Rules, null), Is.EqualTo(Sense.Sensed), "15 m in the open, behind it");
            Assert.That(Senses.Perceive(wolf, Hero(0, 0, -9), PlayerRange, Rules, new Wall()), Is.EqualTo(Sense.Sensed), "9 m: smelled through rock");
            // below a cliff edge: 8 m down and 14 m out, the edge hides him
            Assert.That(Senses.Perceive(wolf, Hero(0, 8, 14), PlayerRange, Rules, new Wall()), Is.EqualTo(Sense.None));
        }

        [Test]
        public void HearingThroughWallsCarriesHalfAsFar()
        {
            var guard = Observer(true);
            Assert.That(Senses.Perceive(guard, Hero(0, 0, -7, moving: true), PlayerRange, Rules, null), Is.EqualTo(Sense.Heard), "7 m behind, open");
            Assert.That(Senses.Perceive(guard, Hero(0, 0, -7, moving: true), PlayerRange, Rules, new Wall()), Is.EqualTo(Sense.None), "7 m behind rock");
            Assert.That(Senses.Perceive(guard, Hero(0, 0, -4, moving: true), PlayerRange, Rules, new Wall()), Is.EqualTo(Sense.Heard), "4 m behind rock");
            Assert.That(Senses.Perceive(guard, Hero(0, 0, -12, moving: true), PlayerRange, Rules, null), Is.EqualTo(Sense.None), "hearing ends at 10 m");
        }

        [Test]
        public void SightRaysAreSizedFromTheBodies()
        {
            var probe = new Probe { Clear = true };
            var wolf = Observer(false);
            wolf.EyeHeight = 0.7f;
            var hero = Hero(0, 0, 15);
            hero.Height = 1.25f; // sneaking
            Assert.That(Senses.Perceive(wolf, hero, PlayerRange, Rules, probe), Is.EqualTo(Sense.Sensed));
            Assert.That(probe.Eye, Is.EqualTo(0.7f));
            Assert.That(probe.Top, Is.EqualTo(1.25f));
            Senses.Perceive(Observer(true), Hero(0, 0, 15), PlayerRange, Rules, probe);
            Assert.That(probe.Eye, Is.EqualTo(SightLines.HumanEyeHeight), "unknown sizes: a human's");
            Assert.That(probe.Top, Is.EqualTo(SightLines.HumanTop));
            int before = probe.Calls;
            Senses.Perceive(wolf, Hero(0, 0, 5), PlayerRange, Rules, probe);
            Assert.That(probe.Calls, Is.EqualTo(before), "within the nose range no ray is cast");
        }

        [Test]
        public void FightSoundAndWarningsAreMuffledByRock()
        {
            var profile = new AttitudeProfile { Id = new ActorId("Rebel#1"), Species = "Human", Humanoid = true };
            var r = new BroadcastReceiver { Id = profile.Id, Pose = new WorldPose(0, 0, 15, 0), Detail = DetailLevel.Active };
            var fight = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, new ActorId("Wolf#2"), new WorldPose(0, 0, 0, 0));
            Assert.That(BroadcastReception.CanReceive(r, fight, profile, Rules, null), Is.True, "15 m in the open");
            Assert.That(BroadcastReception.CanReceive(r, fight, profile, Rules, new Wall()), Is.False, "15 m behind rock");
            r.Pose = new WorldPose(0, 0, 8, 0);
            Assert.That(BroadcastReception.CanReceive(r, fight, profile, Rules, new Wall()), Is.True, "8 m behind rock");
            var warn = new PerceptionBroadcast(BroadcastKind.Warn, new ActorId("Guard#1"), ActorId.Player, new WorldPose(0, 0, 0, 0));
            Assert.That(BroadcastReception.CanReceive(r, warn, profile, Rules, new Wall()), Is.False, "warn 1000: 8 m behind rock is beyond 5 m");
            r.Pose = new WorldPose(0, 0, 4, 0);
            Assert.That(BroadcastReception.CanReceive(r, warn, profile, Rules, new Wall()), Is.True);
        }

        [Test]
        public void SleepingAnimalsWakeThroughRockOnlyClose()
        {
            var sleeper = new WorldPose(0, 0, 0, 0);
            Assert.That(Senses.WakesAnimal(sleeper, Hero(0, 0, 8), false, false, Rules, null), Is.True, "8 m open");
            Assert.That(Senses.WakesAnimal(sleeper, Hero(0, 0, 8), false, false, Rules, new Wall()), Is.False, "8 m behind rock");
            Assert.That(Senses.WakesAnimal(sleeper, Hero(0, 0, 4), false, false, Rules, new Wall()), Is.True, "4 m behind rock");
            Assert.That(Senses.WakesAnimal(sleeper, Hero(0, 10.5f, 1), false, false, Rules, null), Is.False, "on the cliff above");
        }

        [Test]
        public void G3ValuesRestoreTheOriginalSenses()
        {
            var g3 = new PerceptionRules(PerceptionRules.DefinitionId, Rules.ActiveInterval, Rules.GateGuardInterval, Rules.FieldOfView,
                Rules.HearingRange, Rules.MaxHeightDifference, Rules.SneakVsAnimalsRange, Rules.Slots, Rules.ArenaOrAttackerRange,
                Rules.Broadcasts, Rules.WeaponWarnings, Rules.ZoneWarnings, Rules.WeaponRange, Rules.ZoneMaxHeight, Rules.SneakRange,
                Rules.WatchFightMin, Rules.WatchFightMax, Rules.WakeRangeIndoor, Rules.WakeRangeOutdoor, Rules.PursueGiveUpRange,
                creatureSenseRange: 1e6f, occludedHearingFactor: 1f);
            Assert.That(Senses.Perceive(Observer(false), Hero(0, 0, -15), PlayerRange, g3, new Wall()), Is.EqualTo(Sense.Sensed));
            Assert.That(Senses.Perceive(Observer(true), Hero(0, 0, -7, moving: true), PlayerRange, g3, new Wall()), Is.EqualTo(Sense.Heard));
        }

        [Test]
        public void FightMusicStartsOnlyForAnAttackerThatPerceivesTheHero()
        {
            var rule = new MusicSituationRule();
            var blind = new MusicNpc(new ActorId("Orc#1"), 20f, true, ActorId.Player, perceives: false);
            var seeing = new MusicNpc(new ActorId("Orc#2"), 20f, true, ActorId.Player, perceives: true);
            Assert.AreEqual(MusicSituationRule.Explore, rule.Evaluate(false, false, false, new[] { blind }));
            Assert.AreEqual(MusicSituationRule.Fight, rule.Evaluate(false, false, false, new[] { blind, seeing }));
            Assert.AreEqual(MusicSituationRule.Fight, rule.Evaluate(false, false, false, new[] { blind }), "once playing it stays");
            Assert.AreEqual(MusicSituationRule.Explore, rule.Evaluate(false, false, false, new MusicNpc[0]));
            Assert.AreEqual(MusicSituationRule.Explore, rule.Evaluate(false, false, false,
                new[] { new MusicNpc(new ActorId("Xardas"), 20f, true, ActorId.Player, perceives: false) }), "no showdown either");
        }
    }
}
