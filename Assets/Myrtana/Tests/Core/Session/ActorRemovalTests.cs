using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-14: a runtime actor (dev spawn, summon) leaves every per-actor store through <see cref="ActorRemoval"/>, and the
    /// session's tick survives an actor whose definition is gone (editor-runner-17: 152 NREs in ActorVitals.Change).
    /// </summary>
    public class ActorRemovalTests
    {
        static readonly ActorId Template = new ActorId("Bloodfly#a1");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder, new NpcDef(Template, default, null, "Bloodfly", spawn: new WorldPose(100, 0, 100, 0),
                                                        combat: new NpcCombatDef(5, 10, "Bloodfly", false, animal: true)));

        /// <summary>As the scenario's spawn op: a new actor from the template, in the registry and the simulation.</summary>
        static ActorId Spawn(GameSession s, WorldPose pose)
        {
            Assert.That(s.Actors.TryGet(Template, out var template), Is.True);
            var id = DevNpcs.NewId(Template.Name, s.Actors.Exists);
            var def = DevNpcs.CreatureFrom(template.Definition, id, pose, Attitude.Hostile);
            Assert.That(s.Actors.Add(def), Is.True);
            Assert.That(s.Npcs.Add(def, pose), Is.Not.Null);
            return id;
        }

        static void TickSeconds(GameSession s, float seconds)
        {
            for (float t = 0; t < seconds; t += 0.1f) s.Tick(0.1f);
        }

        [Test]
        public void SpawnThenRemove_LeavesNoEntryInAnyStore_AndTicksCleanly()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var pose = new WorldPose(0, 0, 0, 0);
            var victim = Spawn(s, pose);
            var attacker = Spawn(s, new WorldPose(2, 0, 0, 0));

            // Touch the stores: vitals, statuses, fight (both sides), party, inventory.
            s.Combat.Vitals.AddStamina(victim, -30);
            s.Combat.Vitals.AddHitPoints(victim, -5);
            s.Magic.Status.Burn(victim, 10f);
            s.NpcCombat.Engage(attacker, victim, AttackReason.Enemy);
            s.NpcCombat.Engage(victim, ActorId.Player, AttackReason.Enemy);
            s.Party.Members.Join(victim, PartyMemberType.Mob);
            s.Items.Inventories.Of(victim).Add("It_Apple", 2);
            TickSeconds(s, 0.5f);
            Assert.That(s.Removal.Holding(victim), Is.Not.Empty, "the stores know the spawn before it goes");

            Assert.That(s.Removal.Remove(victim), Is.True);

            Assert.That(s.Removal.Holding(victim).Select(x => x.GetType().Name), Is.Empty);
            Assert.That(s.Actors.Exists(victim), Is.False);
            Assert.That(s.Npcs.TryGet(victim, out _), Is.False);
            Assert.That(s.NpcCombat.Get(attacker)?.IsFighting ?? false, Is.False, "nobody keeps fighting a removed actor");
            Assert.DoesNotThrow(() => TickSeconds(s, 5f));
            Assert.That(s.Removal.Holding(victim), Is.Empty, "ticking does not bring entries back");
            Assert.That(s.Removal.Remove(ActorId.Player), Is.False, "the hero is never removed");
        }

        [Test]
        public void ActorGoneWithoutTheRemovalPath_TickDropsItsVitals()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var id = Spawn(s, new WorldPose(0, 0, 0, 0));
            s.Combat.Vitals.AddStamina(id, -30); // an entry that regenerates every tick
            s.Npcs.Remove(id);
            s.Actors.Remove(id); // the definition is gone, the vitals entry is not

            Assert.DoesNotThrow(() => TickSeconds(s, 3f));
            Assert.DoesNotThrow(() => s.Combat.Vitals.AddStamina(id, 5));
            Assert.That(s.Combat.Vitals.Holds(id), Is.False);
        }

        [Test]
        public void ContentActor_KeepsItsDefinition_ButLeavesTheSimulation()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Combat.Vitals.AddStamina(Template, -30);
            Assert.That(s.Removal.Remove(Template), Is.True);
            Assert.That(s.Actors.Exists(Template), Is.True, "a content NPC keeps its identity");
            Assert.That(s.Removal.Holding(Template), Is.Empty);
            Assert.DoesNotThrow(() => TickSeconds(s, 2f));
        }
    }
}
