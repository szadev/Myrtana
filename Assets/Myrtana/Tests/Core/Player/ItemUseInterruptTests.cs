using System.Collections.Generic;
using G3.Player;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-201 (owner: "while the hero eats, an arrow hits him: then no attack, item use, weapon or interaction works until
    /// the menu is opened and closed"): the stumble took the body's motion from the eat/drink use, whose end never came on
    /// time, and its control lock and busy flag stayed. Now every interruption ends the use at once (<see cref="ItemUseFlow"/>):
    /// the item is kept before the consume point, applied after it, the lock goes; and no watched control lock outlives its
    /// action (<see cref="ActionLockWatchdog"/>). The hero model mirrors HeroConsumption + HeroControlLock.
    /// </summary>
    public class ItemUseInterruptTests
    {
        sealed class Hero
        {
            public readonly ActionLockWatchdog Watchdog = new ActionLockWatchdog();
            public readonly HashSet<object> Locks = new HashSet<object>();
            public readonly ItemUseFlow Use = new ItemUseFlow();
            readonly object _consumption = "HeroConsumption";
            public int Items = 3, Effects;
            public bool Reacting, Down, Dead, Swimming, DialogStarted, MotionOwned, CoroutineAlive, HandsBack, WeaponStowed;
            public ItemUseEnd Ended = ItemUseEnd.Finished;

            /// <summary>Use1 in a fight, a quick slot, the use key: everything that the bug blocked.</summary>
            public bool ActionsWork => Locks.Count == 0 && !Use.Running;

            public bool Eat(bool consumes = true, bool weaponDrawn = false)
            {
                if (Use.Running || Locks.Count > 0) return false;
                Use.Start(consumes);
                WeaponStowed = weaponDrawn;
                HandsBack = false;
                Locks.Add(_consumption);
                CoroutineAlive = MotionOwned = true;
                Watchdog.Watch(_consumption, () => Use.Running && CoroutineAlive);
                return true;
            }

            public void Frame(float dt = 1 / 60f)
            {
                if (Use.Running)
                {
                    var watch = new ItemUseWatch { Reacting = Reacting, Down = Down, Dead = Dead, Swimming = Swimming, DialogStarted = DialogStarted, MotionOwned = MotionOwned };
                    var cut = Use.Check(watch, dt);
                    if (cut != ItemUseCut.None) End(cut);
                }
                foreach (var orphan in new List<object>(Watchdog.Sweep(dt)))
                {
                    if (Use.Running) End(ItemUseCut.MotionLost); // IActionLockOwner.ActionOrphaned
                    Locks.Remove(orphan);
                }
            }

            /// <summary>The Use_Begin motion reached its end: ConsumeItems.</summary>
            public void BeginMotionEnds()
            {
                if (!Use.Running) return;
                if (Use.ReachConsumePoint()) { Items--; Effects++; }
            }

            public void EndMotionEnds()
            {
                if (Use.Running) End(ItemUseCut.None);
            }

            void End(ItemUseCut cut)
            {
                Ended = Use.End(cut);
                if (WeaponStowed && ItemUseFlow.RestoresHands(cut)) HandsBack = true;
                WeaponStowed = false;
                Locks.Remove(_consumption);
                Watchdog.Forget(_consumption);
            }

            /// <summary>An arrow: the combat animator's stumble takes the body (Mode.Reaction, its own motion key).</summary>
            public void Arrow()
            {
                Reacting = true;
                MotionOwned = false;
            }

            public void StumbleOver() => Reacting = false;
        }

        [Test]
        public void Eat_ArrowBeforeConsumePoint_ItemKept_ActionsWork()
        {
            var h = new Hero();
            Assert.That(h.Eat(), Is.True);
            h.Frame();
            Assert.That(h.ActionsWork, Is.False, "eating holds the controls");
            h.Arrow();
            h.Frame();
            Assert.That(h.Use.LastCut, Is.EqualTo(ItemUseCut.HitReaction));
            Assert.That(h.Ended, Is.EqualTo(ItemUseEnd.CutKept));
            Assert.That(h.Items, Is.EqualTo(3), "ConsumeItems never ran: the meat is kept");
            Assert.That(h.Effects, Is.Zero);
            Assert.That(h.ActionsWork, Is.True, "the cut use leaves no lock: attack, item use, weapon, interaction work");
            h.BeginMotionEnds(); // a late end of the cut motion consumes nothing
            Assert.That(h.Items, Is.EqualTo(3));
            h.StumbleOver();
            Assert.That(h.Eat(), Is.True, "the next use starts");
        }

        [Test]
        public void Eat_ArrowAfterConsumePoint_AppliedOnce_ActionsWork()
        {
            var h = new Hero();
            h.Eat();
            h.Frame();
            h.BeginMotionEnds();
            Assert.That(h.Items, Is.EqualTo(2));
            h.Arrow();
            h.Frame();
            Assert.That(h.Ended, Is.EqualTo(ItemUseEnd.CutApplied));
            Assert.That(h.Effects, Is.EqualTo(1), "the effect stays, applied once");
            Assert.That(h.Items, Is.EqualTo(2));
            Assert.That(h.ActionsWork, Is.True);
        }

        [Test]
        public void Drink_WithWeaponDrawn_Arrow_WeaponBack_ActionsWork()
        {
            var h = new Hero();
            h.Eat(weaponDrawn: true);
            h.Frame();
            h.Arrow();
            h.Frame();
            Assert.That(h.Ended, Is.EqualTo(ItemUseEnd.CutKept), "the potion is kept");
            Assert.That(h.HandsBack, Is.True, "the weapon held before goes back into the hands (HoldStacks)");
            Assert.That(h.ActionsWork, Is.True);
        }

        [Test]
        public void Drink_KnockedDown_WeaponNotBack()
        {
            var h = new Hero();
            h.Eat(weaponDrawn: true);
            h.Frame();
            h.Down = true;
            h.MotionOwned = false;
            h.Frame();
            Assert.That(h.Use.LastCut, Is.EqualTo(ItemUseCut.KnockDown));
            Assert.That(h.HandsBack, Is.False, "lying, the weapon stays out of the hand");
            Assert.That(h.ActionsWork, Is.True, "the use's lock is gone (the knock-out holds its own)");
        }

        [Test]
        public void Read_Arrow_NothingConsumed_ActionsWork()
        {
            // a shown use (a letter read or a drink in a talk, fix-153): the core applied it, the motion only shows it
            var h = new Hero();
            h.Eat(consumes: false);
            h.Frame();
            h.Arrow();
            h.Frame();
            Assert.That(h.Ended, Is.EqualTo(ItemUseEnd.CutApplied));
            Assert.That(h.Items, Is.EqualTo(3));
            Assert.That(h.ActionsWork, Is.True);
        }

        [Test]
        public void PlayedThrough_ConsumedOnce()
        {
            var h = new Hero();
            h.Eat();
            h.Frame();
            h.BeginMotionEnds();
            h.Frame();
            h.EndMotionEnds();
            Assert.That(h.Ended, Is.EqualTo(ItemUseEnd.Finished));
            Assert.That(h.Items, Is.EqualTo(2));
            Assert.That(h.ActionsWork, Is.True);
        }

        [TestCase(ItemUseCut.Death)]
        [TestCase(ItemUseCut.KnockDown)]
        [TestCase(ItemUseCut.Fall)]
        [TestCase(ItemUseCut.HitReaction)]
        [TestCase(ItemUseCut.Swim)]
        [TestCase(ItemUseCut.Transform)]
        [TestCase(ItemUseCut.Dialog)]
        [TestCase(ItemUseCut.MotionLost)]
        public void EveryInterruption_EndsTheUse(ItemUseCut kind)
        {
            var w = new ItemUseWatch
            {
                MotionOwned = kind != ItemUseCut.MotionLost,
                Dead = kind == ItemUseCut.Death, Down = kind == ItemUseCut.KnockDown, Falling = kind == ItemUseCut.Fall,
                Reacting = kind == ItemUseCut.HitReaction, Swimming = kind == ItemUseCut.Swim,
                Transformed = kind == ItemUseCut.Transform, DialogStarted = kind == ItemUseCut.Dialog,
            };
            var flow = new ItemUseFlow();
            flow.Start(consumes: true);
            Assert.That(flow.Check(w, 0.016f), Is.EqualTo(kind));
            Assert.That(flow.End(kind), Is.EqualTo(ItemUseEnd.CutKept));
            Assert.That(flow.Running, Is.False);
        }

        [Test]
        public void Interruptions_Rank_DeathFirst()
        {
            var w = new ItemUseWatch { Dead = true, Down = true, Reacting = true, MotionOwned = false };
            Assert.That(ItemUseFlow.CutOf(w), Is.EqualTo(ItemUseCut.Death));
            Assert.That(ItemUseFlow.CutOf(new ItemUseWatch { MotionOwned = true }), Is.EqualTo(ItemUseCut.None));
        }

        [Test]
        public void LostMotion_ThatNeverEnds_TimesOut()
        {
            var flow = new ItemUseFlow();
            flow.Start(true);
            var owned = new ItemUseWatch { MotionOwned = true };
            Assert.That(flow.Check(owned, ItemUseFlow.MaxSeconds - 0.1f), Is.EqualTo(ItemUseCut.None));
            Assert.That(flow.Check(owned, 0.2f), Is.EqualTo(ItemUseCut.MotionLost));
        }

        [Test]
        public void Watchdog_ReleasesALockWhoseCoroutineDied()
        {
            var h = new Hero();
            h.Eat();
            h.Frame();
            h.CoroutineAlive = false; // the use's coroutine stopped without its end (the old bug's shape)
            h.Frame(0.3f);
            Assert.That(h.ActionsWork, Is.False, "within the grace the lock stays");
            h.Frame(0.3f);
            Assert.That(h.ActionsWork, Is.True, "past the grace the watchdog ended the use and freed the lock");
            Assert.That(h.Items, Is.EqualTo(3));
        }

        [Test]
        public void Watchdog_PausedNeverExpires_OwnedResets()
        {
            var dog = new ActionLockWatchdog();
            bool owns = false;
            dog.Watch("use", () => owns, grace: 0.5f);
            for (int i = 0; i < 100; i++) Assert.That(dog.Sweep(0f), Is.Empty, "paused: dt 0");
            Assert.That(dog.Sweep(0.4f), Is.Empty);
            owns = true;
            Assert.That(dog.Sweep(0.4f), Is.Empty, "owning again resets the time");
            owns = false;
            Assert.That(dog.Sweep(0.4f), Is.Empty);
            Assert.That(dog.Sweep(0.2f), Is.EqualTo(new object[] { "use" }));
            Assert.That(dog.Watches("use"), Is.False, "reported once");
        }

        [Test]
        public void Watchdog_ThrowingCheck_CountsAsGone_ForgetStopsWatching()
        {
            var dog = new ActionLockWatchdog();
            dog.Watch("bad", () => throw new System.InvalidOperationException(), grace: 0f);
            dog.Watch("released", () => false, grace: 0f);
            dog.Forget("released");
            Assert.That(dog.Sweep(0.1f), Is.EqualTo(new object[] { "bad" }));
            Assert.That(dog.Count, Is.Zero);
        }
    }
}
