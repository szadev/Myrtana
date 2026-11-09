using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-65 (editor-runner-32 R32-1..6): the dev runner's escort/save/equip rules and the "in battle" rule.</summary>
    public sealed class Fix65Tests
    {
        // R32-1: Okara_SlaveZombie_01 "attacked" Rakus from 137.8 m below; the escort waited at the target until it timed out
        [Test]
        public void EscortFight_CountsOnlyNearTheGuide()
        {
            Assert.That(ScenarioPacing.EscortFightNear(true, 3f, 0.5f, 4f), Is.True, "a boar biting the guide");
            Assert.That(ScenarioPacing.EscortFightNear(true, 0.5f, -137.8f, 0.5f), Is.False, "a zombie on another level");
            Assert.That(ScenarioPacing.EscortFightNear(true, 14f, 6f, 0f), Is.False, "outside the height band");
            Assert.That(ScenarioPacing.EscortFightNear(true, 16f, 0f, 0f), Is.False, "too far");
            Assert.That(ScenarioPacing.EscortFightNear(false, 1f, 0f, 1f), Is.False, "fights someone else");
        }

        // R32-3: G3 "in battle" (CP FUN_10001880): AIMode Combat, combat state Active (token), target = the player
        [Test]
        public void InBattle_IsAnAttackerOfThePlayerThatHoldsTheToken()
        {
            var npc = new ActorId("Bandit");
            Assert.That(NpcCombatSystem.InBattleWithPlayer(CombatPhase.Fight, ActorId.Player, holdsToken: true, atCoordinator: true), Is.True);
            Assert.That(NpcCombatSystem.InBattleWithPlayer(CombatPhase.Fight, ActorId.Player, holdsToken: false, atCoordinator: true), Is.False,
                        "a waiting fighter of a group (combat state Passive)");
            Assert.That(NpcCombatSystem.InBattleWithPlayer(CombatPhase.Fight, ActorId.Player, holdsToken: false, atCoordinator: false), Is.True,
                        "engaged this frame: AssessTarget hands out the token at once");
            Assert.That(NpcCombatSystem.InBattleWithPlayer(CombatPhase.WaitForFlee, ActorId.Player, false, false), Is.False, "waits for the player to flee");
            Assert.That(NpcCombatSystem.InBattleWithPlayer(CombatPhase.Flee, ActorId.Player, false, false), Is.False);
            Assert.That(NpcCombatSystem.InBattleWithPlayer(CombatPhase.Fight, npc, true, true), Is.False, "a fight between NPCs");
        }

        // R32-4: 'safe' forgets the grudges the ended fights leave (G3 IsUpset attacks again at once), not murders or results
        [Test]
        public void ForgiveGrudges_KeepsMurdersAndFightResults()
        {
            var ledger = new CrimeLedger();
            ActorId ali = new ActorId("Ali"), orc = new ActorId("Orc_Warrior_01"), gorn = new ActorId("Gorn"), killer = new ActorId("Witness");
            ledger.RecordFight(orc, FightResult.Cancelled, AttackReason.Enemy, 1);
            ledger.RecordFight(gorn, FightResult.PlayerWon, AttackReason.Duel, 1);
            ledger.RememberPersonally(ali, CrimeType.Theft);
            ledger.RememberPersonally(killer, CrimeType.Murder);
            ledger.Count("Faring", CrimeType.Theft, 3);
            ledger.Report("Faring", CrimeType.Theft);
            ledger.Report("Geldern", CrimeType.Murder);
            Assert.That(ledger.ForgiveGrudges(), Is.EqualTo(3));
            Assert.That(ledger.FightOf(orc).Result, Is.EqualTo(FightResult.None));
            Assert.That(ledger.FightOf(gorn).Result, Is.EqualTo(FightResult.PlayerWon));
            Assert.That(ledger.PersonalOf(ali), Is.EqualTo(CrimeType.None));
            Assert.That(ledger.PersonalOf(killer), Is.EqualTo(CrimeType.Murder));
            Assert.That(ledger.Enclave("Faring").IsClean, Is.True);
            Assert.That(ledger.Enclave("Geldern").Known, Is.EqualTo(CrimeType.Murder));
        }

        // R32-5: the rotation's loadout keeps its weapons against a scenario's own equip
        [Test]
        public void Equip_DoesNotFightTheRotationsLoadout()
        {
            Assert.That(ScenarioLoadout.KeepsRotationWeapons(requestedLoadout: true, fromLoadoutOps: false, weaponSlot: true), Is.True);
            Assert.That(ScenarioLoadout.KeepsRotationWeapons(true, fromLoadoutOps: true, weaponSlot: true), Is.False, "the loadout's own ops");
            Assert.That(ScenarioLoadout.KeepsRotationWeapons(true, false, weaponSlot: false), Is.False, "armour, rings");
            Assert.That(ScenarioLoadout.KeepsRotationWeapons(requestedLoadout: false, false, true), Is.False, "pinned by the file or none");
        }

        // R32-6: a pass save blocked by combat waits for the fight to end
        [Test]
        public void SaveWait_IsLongerForCombat()
        {
            string combat = CombatWiring.BlockedByCombat;
            Assert.That(ScenarioCheckpoints.KeepWaiting(null, 0f, 10f, 60f, combat), Is.False, "not blocked: save");
            Assert.That(ScenarioCheckpoints.KeepWaiting(combat, 30f, 10f, 60f, combat), Is.True);
            Assert.That(ScenarioCheckpoints.KeepWaiting(combat, 60f, 10f, 60f, combat), Is.False, "gives up (logged)");
            Assert.That(ScenarioCheckpoints.KeepWaiting("interaction", 30f, 10f, 60f, combat), Is.False, "other blocks: the plain wait");
            Assert.That(ScenarioCheckpoints.KeepWaiting(combat, 5f, 10f, 0f, combat), Is.True);
            Assert.That(ScenarioCheckpoints.KeepWaiting(combat, 10f, 10f, 0f, combat), Is.False, "no combat wait asked");
        }
    }
}
