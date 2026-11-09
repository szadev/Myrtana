using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-216 (owner: the hero and the Hammer clan's rebels attacked the orc-held smelter of the Nordmar mine from below and
    /// the orcs on the ledges above never came to help): every orc of the mine valley is one enclave, Nrd_BigMine. G3 brings
    /// members in by the fight sound (CP AssessFightSound: 2000, 4000 while the enclave is in Attack and the revolution
    /// attitude toward the victim is Friendly or Hostile; 3D distance to the victim or its attacker; no height limit, no
    /// occlusion) and by NotifyEnclave. The port's fix-204 muffling must not cut members off from their comrades' fights.
    /// </summary>
    public class EnclaveHelpTests
    {
        static readonly PerceptionRules Perception = G3CrimeImporter.Perception();
        static readonly CrimeRules Crime = G3CrimeImporter.Crime();
        static readonly AttitudeRules AttitudeTable = G3CrimeImporter.Attitudes();

        const string Mine = "Nrd_BigMine";
        static readonly ActorId Victim = new ActorId("Orc_Raider_Warrior_01#1"), Above = new ActorId("Orc_Raider_Boss_02#2"),
                                Stranger = new ActorId("Nomad_01"), Mort = new ActorId("Mort");

        sealed class Facts : IAttitudeFacts
        {
            public readonly Dictionary<ActorId, AttitudeProfile> Profiles = new Dictionary<ActorId, AttitudeProfile>();
            public void Add(AttitudeProfile p) => Profiles[p.Id] = p;
            public bool TryGetProfile(ActorId actor, out AttitudeProfile profile) => Profiles.TryGetValue(actor, out profile);
        }

        sealed class World : IPerceptionWorld
        {
            public readonly Dictionary<ActorId, BroadcastReceiver> Npcs = new Dictionary<ActorId, BroadcastReceiver>();
            public WorldPose PlayerPose;

            public void Place(ActorId id, float x, float y, float z) =>
                Npcs[id] = new BroadcastReceiver { Id = id, Pose = new WorldPose(x, y, z, 0), Detail = DetailLevel.Active, Mode = ReceiverMode.Routine };

            public bool TryGetPose(ActorId actor, out WorldPose pose)
            {
                if (actor.IsPlayer) { pose = PlayerPose; return true; }
                bool found = Npcs.TryGetValue(actor, out var r);
                pose = r.Pose;
                return found;
            }

            public string IndoorZoneOf(ActorId actor) => null;
            public IEnumerable<BroadcastReceiver> Receivers() => Npcs.Values;
            public float DistanceToWorkplace(ActorId npc, WorldPose point) => float.MaxValue;
        }

        /// <summary>Rock everywhere: no free line between any two points.</summary>
        sealed class Rock : ILineOfSight
        {
            public bool IsClear(WorldPose from, WorldPose to) => false;
        }

        sealed class Fixture
        {
            public readonly Facts Facts = new Facts();
            public readonly World World = new World();
            public readonly EnclaveStates Enclaves = new EnclaveStates();
            public readonly EventBus Events = new EventBus();
            public readonly List<(ActorId npc, ActorId target, AttackReason reason)> Engaged = new List<(ActorId, ActorId, AttackReason)>();
            public readonly WitnessSystem Witnesses;
            public readonly CrimeSystem System;

            public Fixture()
            {
                var ledger = new CrimeLedger();
                var attitudes = new AttitudeService(AttitudeTable, TestRules.CombatAi(), Facts, ledger, new FameLedger(), () => 1, () => 0)
                {
                    Enclaves = Enclaves,
                };
                var catalogue = new CrimeCatalogue(Crime, attitudes);
                EngageFight engage = (npc, target, reason) => { Engaged.Add((npc, target, reason)); return true; };
                Witnesses = new WitnessSystem(Perception, attitudes, catalogue, ledger, World, new Rock(), engage, null);
                System = new CrimeSystem(Perception, Crime, attitudes, catalogue, ledger, World, engage, Events, RuleOptions.Default,
                                         _ => false, () => 0) { Enclaves = Enclaves, LineOfSight = new Rock() };
                Facts.Add(new AttitudeProfile { Id = ActorId.Player, IsPlayer = true, Species = "Human", Humanoid = true });
                Facts.Add(Orc(Victim));
                Facts.Add(Orc(Above));
                Facts.Add(new AttitudeProfile { Id = Stranger, Species = "Human", Humanoid = true, Alignment = "Out" });
                Facts.Add(new AttitudeProfile
                {
                    Id = Mort, Species = "Human", Humanoid = true, Alignment = "Nrd", PartyLeader = ActorId.Player, PartyRole = PartyRole.Member,
                });
                World.PlayerPose = new WorldPose(1, 0, 0, 0);
                World.Place(Victim, 0, 0, 0);
            }

            static AttitudeProfile Orc(ActorId id) => new AttitudeProfile
            {
                Id = id, Species = "Orc", Humanoid = true, Alignment = "Out", Enclave = Mine, EnclaveAlignment = "Out",
            };

            /// <summary>The hero's hit on the orc below (a landed hit's fight sound, heard near the victim or the hero).</summary>
            public IReadOnlyList<KeyValuePair<ActorId, Reaction>> HeroHitsTheOrc() =>
                Witnesses.Deliver(new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Victim, World.Npcs[Victim].Pose,
                                                          AttackReason.Enemy, sourcePose: World.PlayerPose));

            public bool Joins(ActorId npc) => HeroHitsTheOrc().Any(r => r.Key == npc && r.Value.Kind == ReactionKind.Attack);
        }

        [Test]
        public void AnAlarmedEnclaveHearsItsFightsFortyMetresAwayThroughRock()
        {
            var f = new Fixture();
            f.World.Place(Above, 10, 25, 0); // a ledge 25 m up, 27 m away, the cliff edge between
            Assert.That(f.Joins(Above), Is.False, "a quiet enclave: 2000, so 27 m is too far");
            f.Enclaves.SetStatus(Mine, EnclaveStatus.Attack);
            Assert.That(f.Joins(Above), Is.True, "in Attack: the revolution range 4000, no occlusion for a comrade");
            Assert.That(f.Engaged.Last(), Is.EqualTo((Above, ActorId.Player, AttackReason.Enemy)));
        }

        [Test]
        public void BroadcastsHaveNoHeightLimitOnlyThe3DRange()
        {
            var f = new Fixture();
            f.Enclaves.SetStatus(Mine, EnclaveStatus.Attack);
            f.World.Place(Above, 0, 35, 0);
            Assert.That(f.Joins(Above), Is.True, "35 m straight up (perception's 10 m height limit is not a broadcast rule)");
            f.World.Place(Above, 0, 45, 0);
            Assert.That(f.Joins(Above), Is.False, "45 m: beyond 4000");
        }

        [Test]
        public void TheFightIsHeardNearTheAttackerToo()
        {
            var f = new Fixture();
            f.World.PlayerPose = new WorldPose(15, 0, 0, 0);
            f.World.Place(Above, 30, 0, 0); // 30 m from the victim, 15 m from the hero
            Assert.That(f.Joins(Above), Is.True, "CP AssessFightSound measures the receiver to both fighters");
        }

        [Test]
        public void OnlyOutsidersAreMuffledByRock()
        {
            var f = new Fixture();
            f.World.Place(Above, 0, 0, 15);
            Assert.That(f.Joins(Above), Is.True, "a comrade 15 m behind rock (fix-204 muffled it to 10 m)");

            var receiver = new BroadcastReceiver { Id = Stranger, Pose = new WorldPose(0, 0, 15, 0), Detail = DetailLevel.Active, Mode = ReceiverMode.Routine };
            var fight = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Victim, new WorldPose(0, 0, 0, 0), AttackReason.Enemy);
            var stranger = f.Facts.Profiles[Stranger];
            Assert.That(BroadcastReception.CanReceive(receiver, fight, stranger, Perception, new Rock()), Is.False, "an outsider: 10 m through rock");
            Assert.That(BroadcastReception.CanReceive(receiver, fight, stranger, Perception, null), Is.True, "in the open: 20 m");
        }

        [Test]
        public void OwnSideIsTheEnclaveOrTheHerosParty()
        {
            var f = new Fixture();
            var p = f.Facts.Profiles;
            Assert.That(BroadcastReception.OwnSide(p[Above], p[ActorId.Player], p[Victim]), Is.True, "the victim is a comrade");
            Assert.That(BroadcastReception.OwnSide(p[Above], p[Victim], p[Mort]), Is.True, "the attacker is a comrade");
            Assert.That(BroadcastReception.OwnSide(p[Mort], p[Victim], p[ActorId.Player]), Is.True, "a companion: the hero is attacked");
            Assert.That(BroadcastReception.OwnSide(p[Stranger], p[ActorId.Player], p[Victim]), Is.False);
            Assert.That(BroadcastReception.OwnSide(p[Mort], p[Victim], p[Stranger]), Is.False, "not the hero's fight");
        }

        [Test]
        public void TheEnclaveAlarmIgnoresRockAndHeight()
        {
            var f = new Fixture();
            f.World.Place(Above, 20, 50, 0); // far above, behind rock
            var sent = f.System.RaiseEnclaveAlarm(Mine, ActorId.Player);
            Assert.That(sent, Does.Contain(Above), "G3 NotifyEnclave sends the nearest members, no sight or height test");
            Assert.That(f.Enclaves.Of(Mine).Status, Is.EqualTo(EnclaveStatus.Attack));
        }
    }
}
