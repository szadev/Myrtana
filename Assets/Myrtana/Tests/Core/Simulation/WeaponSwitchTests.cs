using System;
using G3.Player;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The drawn weapon has one source of truth (<see cref="WeaponHand"/>) and the brain's moves follow the held weapon
    /// (<see cref="WeaponSelection"/>): Play bugs Orc_Warrior_04 (drawn, nothing in the hand, never redrawn) and
    /// Orc_Scout_02 (brain "Shoot" with a sword in the hand). G3 _AI_SelectWeapon / _AI_HoldInventoryItems_Base.
    /// </summary>
    public class WeaponSwitchTests
    {
        const string Axe = "G3_Weapon_Orc_Axe_02", Xbow = "G3_Weapons_Ranged_CrossBow_Orc_01";

        static readonly CombatAiRules Ai = TestRules.CombatAi();
        static readonly CombatRules Rules = TestRules.Combat();
        static readonly NpcCombatDef Orc = new NpcCombatDef(15, 35, "Orc", humanoid: true, role: NpcRole.Guard, @class: NpcClass.Warrior, hasEnclave: true);
        static readonly CombatLoadout AxeAndCrossbow = CombatLoadout.From(new[] { Item("It_Axe_OrcWaraxe_01", "Axe"), Item("It_CrossBow_01", "CrossBow"), Item("It_Bolt", "Bolt") });

        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default);

        static CombatSituation Situation(float distance, HeldWeapon held, double now = 0) =>
            new CombatSituation
            {
                Now = now, Distance = distance, LineOfSight = true, FreeBack = true, FreeBackLeft = true, FreeBackRight = true,
                TargetIsPlayer = true, TargetState = VitalState.Alive, TargetKillable = true, TargetHumanoid = true, TargetSpecies = "Human",
                TargetHoldsMelee = true, TargetPhase = MovePhase.Raise, TargetEngagedWithSelf = true,
                AttitudeToTarget = Attitude.Hostile, PlayerLevel = 10, HitPointsPercent = 100, Held = held,
            };

        static NpcCombatBrain Brain()
        {
            var options = RuleOptions.Default;
            var brain = new NpcCombatBrain(new ActorId("Orc_Warrior_04"), Orc, AxeAndCrossbow, Ai, Rules, options, new SeededRandom(5),
                                           new CombatCoordinator(Ai.Token, options));
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            return brain;
        }

        static WeaponHand Holding(string field, string template)
        {
            var hand = new WeaponHand();
            hand.BeginDraw(field, template);
            hand.Finish();
            return hand;
        }

        static void AssertConsistent(WeaponHand hand)
        {
            if (!hand.Settled) return;
            Assert.That(hand.Drawn, Is.EqualTo(hand.InHand), "settled: drawn exactly when something is in the hand");
            Assert.That(hand.MotionField, Is.EqualTo(hand.Field ?? WeaponHand.Fist), "motions use the hand's weapon");
        }

        // ------------------------------------------------------------------------------------------- the hand

        [Test]
        public void UsedItem_EmptiesTheHandAndTakesTheWeaponBackWithoutMotions()
        {
            // fix-68: G3 _AI_UseInventoryItem → HoldStacks: the weapon leaves the hand for the potion, then comes back.
            var hand = new WeaponHand();
            Assert.That(hand.BeginDraw("1H", "Sword"), Is.True);
            hand.Finish();
            string field = hand.Field, template = hand.Template;
            Assert.That(hand.Clear(), Is.True, "stowed at once");
            Assert.That(hand.Drawn, Is.False);
            Assert.That(hand.Hold(field, template), Is.True, "back at once");
            Assert.That(hand.Drawn && hand.Settled && hand.Field == "1H" && hand.Template == "Sword", Is.True);
            Assert.That(hand.Hold("2H", "Axe"), Is.False, "not into a full hand");
            Assert.That(hand.BeginSheathe(), Is.True);
            Assert.That(hand.Hold("1H", "Sword"), Is.False, "not while a transition runs");
        }

        [Test]
        public void Draw_TheWeaponEntersTheHandOnce()
        {
            var hand = new WeaponHand();
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Draw));
            Assert.IsTrue(hand.BeginDraw("2H", Axe));
            Assert.IsTrue(hand.Drawn);
            Assert.IsFalse(hand.InHand);
            Assert.That(hand.MotionField, Is.EqualTo("2H"), "the draw motion is the new weapon's");
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Wait));
            Assert.IsTrue(hand.SwitchHands(), "half-way: in the hand");
            Assert.IsFalse(hand.SwitchHands());
            Assert.IsFalse(hand.Finish(), "already switched");
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Ready));
            AssertConsistent(hand);
        }

        [Test]
        public void CrossbowToAxe_PutAwayThenDraw()
        {
            var hand = Holding("CrossBow", Xbow);
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Sheathe));
            Assert.IsFalse(hand.BeginDraw("2H", Axe), "no draw over a held weapon");
            Assert.IsTrue(hand.BeginSheathe());
            Assert.IsFalse(hand.Drawn);
            Assert.That(hand.MotionField, Is.EqualTo("CrossBow"), "the put-away motion is the crossbow's");
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Wait));
            Assert.IsTrue(hand.Finish(), "no half-way report: the hand changes at the end");
            Assert.IsFalse(hand.InHand);
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Draw));
            hand.BeginDraw("2H", Axe);
            hand.Finish();
            Assert.That(hand.Field, Is.EqualTo("2H"));
            Assert.That(hand.Template, Is.EqualTo(Axe));
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Ready));
            AssertConsistent(hand);
        }

        [Test]
        public void DrawCutShortBeforeTheHand_NotDrawn_AndDrawnAgain()
        {
            // Orc_Warrior_04: a hit reaction during the axe draw left Drawn=True, WeaponInHand=False for good.
            var hand = new WeaponHand();
            hand.BeginDraw("2H", Axe);
            hand.Interrupt();
            Assert.IsFalse(hand.Drawn);
            Assert.IsFalse(hand.InHand);
            Assert.That(hand.MotionField, Is.EqualTo(WeaponHand.Fist), "unarmed motions with empty hands");
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Draw), "the draw is retried");
            AssertConsistent(hand);
        }

        [Test]
        public void DrawCutShortAfterTheHand_Drawn()
        {
            var hand = new WeaponHand();
            hand.BeginDraw("2H", Axe);
            hand.SwitchHands();
            hand.Interrupt();
            Assert.IsTrue(hand.Drawn);
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Ready));
            AssertConsistent(hand);
        }

        [Test]
        public void SheatheCutShortBeforeTheHand_StillDrawnWithTheOldWeapon()
        {
            // Orc_Scout_02: an interrupted put-away left the old weapon in the hand while the next draw changed only the field.
            var hand = Holding("2H", Axe);
            hand.BeginSheathe();
            hand.Interrupt();
            Assert.IsTrue(hand.Drawn);
            Assert.That(hand.Field, Is.EqualTo("2H"));
            Assert.That(hand.Next("CrossBow", Xbow), Is.EqualTo(WeaponStep.Sheathe), "put the axe away again, never draw over it");
            Assert.IsFalse(hand.BeginDraw("CrossBow", Xbow));
            AssertConsistent(hand);
        }

        [Test]
        public void SheatheCutShortAfterTheHand_Sheathed()
        {
            var hand = Holding("CrossBow", Xbow);
            hand.BeginSheathe();
            Assert.IsTrue(hand.SwitchHands());
            hand.Interrupt();
            Assert.IsFalse(hand.Drawn);
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Draw));
            AssertConsistent(hand);
        }

        [Test]
        public void KnockOut_DropsTheWeapon_HandsEmpty()
        {
            // G3 Defeat(): droppable hand weapons fall to the ground (A2 notes, ZS_Unconscious); ZS_FetchWeapon later.
            var hand = Holding("2H", Axe);
            Assert.IsTrue(hand.Clear());
            Assert.IsFalse(hand.Drawn);
            Assert.That(hand.MotionField, Is.EqualTo(WeaponHand.Fist));
            hand.BeginDraw("2H", Axe);
            Assert.IsFalse(hand.Clear(), "nothing was in the hand yet");
            Assert.IsTrue(hand.Settled);
        }

        [Test]
        public void SpellHandAndFists_AreHandFields()
        {
            var hand = Holding("Cast", null);
            Assert.That(hand.Next("2H", Axe), Is.EqualTo(WeaponStep.Sheathe));
            Assert.That(hand.Next("Cast", null), Is.EqualTo(WeaponStep.Ready));
            var fists = Holding(null, null);
            Assert.That(fists.Field, Is.EqualTo(WeaponHand.Fist));
            Assert.IsTrue(fists.InHand, "drawn fists count as held");
            Assert.That(fists.Next(null, null), Is.EqualTo(WeaponStep.Ready));
        }

        [Test]
        public void AnySequence_KeepsDrawnAndHandInStep()
        {
            var random = new Random(7);
            var hand = new WeaponHand();
            string[] fields = { "2H", "CrossBow", "Cast", "Fist" };
            for (int i = 0; i < 5000; i++)
            {
                string field = fields[random.Next(fields.Length)];
                switch (random.Next(6))
                {
                    case 0: hand.BeginDraw(field, field + "_mesh"); break;
                    case 1: hand.BeginSheathe(); break;
                    case 2: hand.SwitchHands(); break;
                    case 3: hand.Finish(); break;
                    case 4: hand.Interrupt(); break;
                    case 5: if (random.Next(10) == 0) hand.Clear(); break;
                }
                AssertConsistent(hand);
                if (hand.Transition == WeaponTransition.Drawing) Assert.IsTrue(hand.Drawn);
                if (hand.Transition == WeaponTransition.Sheathing) Assert.IsFalse(hand.Drawn);
            }
        }

        // ------------------------------------------------------------------------------------------- brain vs hand

        [Test]
        public void HeldOf_MapsTheHand()
        {
            Assert.That(WeaponSelection.HeldOf(false, false, false, false), Is.EqualTo(HeldWeapon.None));
            Assert.That(WeaponSelection.HeldOf(true, true, true, false), Is.EqualTo(HeldWeapon.None), "switching");
            Assert.That(WeaponSelection.HeldOf(true, false, true, false), Is.EqualTo(HeldWeapon.Ranged));
            Assert.That(WeaponSelection.HeldOf(true, false, false, true), Is.EqualTo(HeldWeapon.Magic));
            Assert.That(WeaponSelection.HeldOf(true, false, false, false), Is.EqualTo(HeldWeapon.Melee));
        }

        [Test]
        public void KeepCurrentBand_FollowsTheHand()
        {
            // weaponChoice: < 700 melee, 700–1000 the current weapon (melee with empty hands), beyond a ranged weapon.
            Assert.That(Brain().Tick(Situation(800, HeldWeapon.Ranged)).Weapon, Is.EqualTo(WeaponMode.Ranged));
            Assert.That(Brain().Tick(Situation(800, HeldWeapon.Melee)).Weapon, Is.EqualTo(WeaponMode.Melee));
            Assert.That(Brain().Tick(Situation(800, HeldWeapon.None)).Weapon, Is.EqualTo(WeaponMode.Melee));
            Assert.That(Brain().Tick(Situation(500, HeldWeapon.Ranged)).Weapon, Is.EqualTo(WeaponMode.Melee));
            Assert.That(Brain().Tick(Situation(1500, HeldWeapon.Melee)).Weapon, Is.EqualTo(WeaponMode.Ranged));
            Assert.That(WeaponSelection.KeepCurrent(HeldWeapon.Unknown, WeaponMode.Ranged), Is.EqualTo(WeaponMode.Ranged));
        }

        [Test]
        public void CrossbowOrcForcedToMelee_WaitsUntilTheAxeIsInTheHand()
        {
            var brain = Brain();
            var order = brain.Tick(Situation(300, HeldWeapon.Ranged));
            Assert.That(order.Weapon, Is.EqualTo(WeaponMode.Melee));
            Assert.That(order.Move, Is.EqualTo(CombatMove.Wait), "crossbow still in the hand");
            Assert.That(brain.Tick(Situation(300, HeldWeapon.None, now: 0.5)).Move, Is.EqualTo(CombatMove.Wait), "switching");
            var armed = brain.Tick(Situation(300, HeldWeapon.Melee, now: 1));
            Assert.That(armed.Weapon, Is.EqualTo(WeaponMode.Melee));
            Assert.That(armed.Move, Is.Not.EqualTo(CombatMove.Shoot));
        }

        [Test]
        public void ScoutWithSwordInHand_NeverShoots()
        {
            var brain = Brain();
            for (int i = 0; i < 20; i++)
            {
                var order = brain.Tick(Situation(1500, HeldWeapon.Melee, now: i * 0.1));
                Assert.That(order.Weapon, Is.EqualTo(WeaponMode.Ranged));
                Assert.That(order.Move, Is.EqualTo(CombatMove.Wait), "brain mode follows the drawn weapon");
            }
            Assert.That(brain.Tick(Situation(1500, HeldWeapon.Ranged, now: 3)).Move, Is.EqualTo(CombatMove.Shoot));
        }
    }
}
