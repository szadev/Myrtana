using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Perception, attitudes, crime and witnesses against the CP 1.75 tables (Docs/perception-crime.md).</summary>
    public class CrimeTests
    {
        static readonly PerceptionRules Perception = G3CrimeImporter.Perception();
        static readonly CrimeRules Crime = G3CrimeImporter.Crime();
        static readonly AttitudeRules AttitudeTable = G3CrimeImporter.Attitudes();

        static readonly ActorId Guard = new ActorId("Faring_Guard"), Smith = new ActorId("Faring_Smith"), Boss = new ActorId("Faring_Boss"),
                                Rebel = new ActorId("Rebel_Scout"), Bandit = new ActorId("Bandit_01"), Pig = new ActorId("Pig_01"),
                                Wolf = new ActorId("Wolf_01"), Companion = new ActorId("Gorn"), Sheep = new ActorId("Deer_01");

        // ---------------------------------------------------------------- fixture

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
            public float Workplace = float.MaxValue;

            public void Place(ActorId id, float x, float z, float yaw = 0, ReceiverMode mode = ReceiverMode.Routine) =>
                Npcs[id] = new BroadcastReceiver { Id = id, Pose = new WorldPose(x, 0, z, yaw), Detail = DetailLevel.Active, Mode = mode };

            public bool TryGetPose(ActorId actor, out WorldPose pose)
            {
                if (actor.IsPlayer) { pose = PlayerPose; return true; }
                bool found = Npcs.TryGetValue(actor, out var r);
                pose = r.Pose;
                return found;
            }

            public string IndoorZoneOf(ActorId actor) => null;
            public IEnumerable<BroadcastReceiver> Receivers() => Npcs.Values;
            public float DistanceToWorkplace(ActorId npc, WorldPose point) => Workplace;
        }

        sealed class Fixture
        {
            public readonly Facts Facts = new Facts();
            public readonly World World = new World();
            public readonly CrimeLedger Ledger = new CrimeLedger();
            public readonly FameLedger Fame = new FameLedger();
            public readonly EventBus Events = new EventBus();
            public readonly List<(ActorId npc, ActorId target, AttackReason reason)> Engaged = new List<(ActorId, ActorId, AttackReason)>();
            public readonly List<PerceptionBroadcast> Broadcasts = new List<PerceptionBroadcast>();
            public double Hours;
            public int Chapter = 1;
            public readonly AttitudeService Attitudes;
            public readonly CrimeCatalogue Catalogue;
            public readonly WitnessSystem Witnesses;
            public readonly CrimeSystem System;
            public readonly HashSet<string> Perks = new HashSet<string>();

            public Fixture(RuleOptions options = null)
            {
                Attitudes = new AttitudeService(AttitudeTable, TestRules.CombatAi(), Facts, Ledger, Fame, () => Chapter, () => Hours);
                Catalogue = new CrimeCatalogue(Crime, Attitudes);
                EngageFight engage = (npc, target, reason) => { Engaged.Add((npc, target, reason)); return true; };
                Witnesses = new WitnessSystem(Perception, Attitudes, Catalogue, Ledger, World, OpenLineOfSight.Instance, engage, Events);
                System = new CrimeSystem(Perception, Crime, Attitudes, Catalogue, Ledger, World, engage, Events, options ?? RuleOptions.Default,
                                         Perks.Contains, () => Hours);
                Events.Subscribe<PerceptionBroadcast>(Broadcasts.Add);
                Facts.Add(new AttitudeProfile { Id = ActorId.Player, IsPlayer = true, Species = "Human", Humanoid = true });
                Facts.Add(Human(Guard, "Reb", "Faring", NpcRole.Guard));
                Facts.Add(Human(Smith, "Reb", "Faring"));
                var boss = Human(Boss, "Reb", "Faring", NpcRole.Political);
                boss.Boss = true;
                Facts.Add(boss);
                Facts.Add(Human(Rebel, "Reb", null));
                Facts.Add(Human(Bandit, "Out", null));
                Facts.Add(new AttitudeProfile { Id = Pig, Species = "Pig", CannotFight = true });
                Facts.Add(new AttitudeProfile { Id = Wolf, Species = "Wolf" });
                Facts.Add(new AttitudeProfile { Id = Sheep, Species = "Deer", CannotFight = true });
                var gorn = Human(Companion, "Reb", null);
                gorn.PartyLeader = ActorId.Player;
                gorn.PartyRole = PartyRole.Member;
                Facts.Add(gorn);
            }

            public static AttitudeProfile Human(ActorId id, string alignment, string enclave, NpcRole role = NpcRole.Personal) =>
                new AttitudeProfile
                {
                    Id = id, Species = "Human", Humanoid = true, Alignment = alignment, Enclave = enclave,
                    EnclaveAlignment = enclave != null ? alignment : null, Role = role,
                };

            public AttitudeProfile Profile(ActorId id) => Facts.Profiles[id];
        }

        static SenseObserver Observer(bool humanoid = true, float yaw = 0) =>
            new SenseObserver { Id = Guard, Pose = new WorldPose(0, 0, 0, yaw), Humanoid = humanoid };

        static SenseTarget Target(float x, float z, bool moving = false, bool sneaking = false) =>
            new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(x, 0, z, 0), Moving = moving, Sneaking = sneaking };

        sealed class Wall : ILineOfSight
        {
            public bool IsClear(WorldPose from, WorldPose to) => false;
        }

        // ---------------------------------------------------------------- senses

        [Test]
        public void HumanoidsSeeTheFrontHalfAndHearMovingTargetsBehind()
        {
            var o = Observer();
            Assert.That(Senses.Perceive(o, Target(5, 10), 2000, Perception, null), Is.EqualTo(Sense.Seen), "in front (yaw 0 = +Z)");
            Assert.That(Senses.Perceive(o, Target(10, 0.1f), 2000, Perception, null), Is.EqualTo(Sense.Seen), "the side: 180° field of view");
            Assert.That(Senses.Perceive(o, Target(0, -5), 2000, Perception, null), Is.EqualTo(Sense.None), "behind, standing");
            Assert.That(Senses.Perceive(o, Target(0, -5, moving: true), 2000, Perception, null), Is.EqualTo(Sense.Heard), "behind, walking");
            Assert.That(Senses.Perceive(o, Target(0, -5, moving: true, sneaking: true), 2000, Perception, null), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(o, Target(0, -15, moving: true), 2000, Perception, null), Is.EqualTo(Sense.None), "hearing reaches 1000");
            Assert.That(Senses.Perceive(o, Target(0, 25), 2000, Perception, null), Is.EqualTo(Sense.None), "out of range");
        }

        [Test]
        public void WallsBlockSightButNotHearingOrTheSameHouse()
        {
            var o = Observer();
            Assert.That(Senses.Perceive(o, Target(0, 5), 2000, Perception, new Wall()), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(o, Target(0, 5, moving: true), 2000, Perception, new Wall()), Is.EqualTo(Sense.Heard));
            o.IndoorZone = "House";
            var t = Target(0, 5);
            t.IndoorZone = "House";
            Assert.That(Senses.Perceive(o, t, 2000, Perception, new Wall()), Is.EqualTo(Sense.Seen));
        }

        [Test]
        public void HeightMistAndAnimals()
        {
            var high = Target(0, 5);
            high.Pose = new WorldPose(0, 11, 5, 0);
            Assert.That(Senses.Perceive(Observer(), high, 2000, Perception, null), Is.EqualTo(Sense.None), "more than 1000 above");
            var misted = Target(0, 5);
            misted.Undetectable = true;
            Assert.That(Senses.Perceive(Observer(), misted, 2000, Perception, null), Is.EqualTo(Sense.None));

            var wolf = Observer(humanoid: false);
            Assert.That(Senses.Perceive(wolf, Target(0, -15), 2000, Perception, null), Is.EqualTo(Sense.Sensed), "animals need no field of view");
            Assert.That(Senses.Perceive(wolf, Target(0, -8), 2000, Perception, new Wall()), Is.EqualTo(Sense.Sensed), "nor sight close by (fix-204)");
            Assert.That(Senses.Perceive(wolf, Target(0, -15, sneaking: true), 2000, Perception, null), Is.EqualTo(Sense.None));
            Assert.That(Senses.Perceive(wolf, Target(0, -8, sneaking: true), 2000, Perception, null), Is.EqualTo(Sense.Sensed));
        }

        [Test]
        public void SleepersWakeIndoorsWithin1000AndOutdoorsWithin500()
        {
            var sleeper = Observer();
            Assert.That(Senses.WakesSleeper(sleeper, Target(0, 4, moving: true), Perception), Is.True);
            Assert.That(Senses.WakesSleeper(sleeper, Target(0, 7, moving: true), Perception), Is.False);
            Assert.That(Senses.WakesSleeper(sleeper, Target(0, 4, moving: true, sneaking: true), Perception), Is.False);
            sleeper.IndoorZone = "Hut";
            var t = Target(0, 7, moving: true);
            t.IndoorZone = "Hut";
            Assert.That(Senses.WakesSleeper(sleeper, t, Perception), Is.True);
        }

        [Test]
        public void TheScanKeepsTheNearestNoticedActorPerSlot()
        {
            PerceptionCandidate C(string id, float z, Attitude a, bool body = false) => new PerceptionCandidate
            {
                Target = new SenseTarget { Id = new ActorId(id), Pose = new WorldPose(0, 0, z, 0) }, Attitude = a, Body = body,
            };
            var snapshot = PerceptionScan.Scan(Observer(), new[]
            {
                C("far_friend", 8, Attitude.Friendly), C("friend", 3, Attitude.Friendly), C("wolf", 15, Attitude.Hostile),
                C("corpse", 5, Attitude.Predator, body: true), C("deer", 9, Attitude.Neutral),
            }, Perception, null);
            Assert.That(snapshot.TryGet(PerceptionSlot.Friendly, out var f) && f.Actor.Name == "friend", Is.True);
            Assert.That(snapshot.TryGet(PerceptionSlot.Hostile, out var h) && h.Distance == 1500, Is.True);
            Assert.That(snapshot.Has(PerceptionSlot.Body) && snapshot.Has(PerceptionSlot.Food), Is.True);
            Assert.That(snapshot.Has(PerceptionSlot.Neutral), Is.False, "neutral actors only within 700");
        }

        [Test]
        public void OnlyActiveNpcsScan()
        {
            Assert.That(Perception.ScanIntervalOf(DetailLevel.Active), Is.EqualTo(1));
            Assert.That(Perception.ScanIntervalOf(DetailLevel.Active, gateGuard: true), Is.EqualTo(0.1));
            Assert.That(Perception.ScanIntervalOf(DetailLevel.Near), Is.Null);
            Assert.That(PerceptionRules.ReceivesBroadcasts(DetailLevel.Near), Is.True);
            Assert.That(PerceptionRules.ReceivesBroadcasts(DetailLevel.Abstract), Is.False);
        }

        // ---------------------------------------------------------------- attitudes

        [Test]
        public void PoliticalMatrixWithTheChapterTwoBreak()
        {
            Assert.That(AttitudeTable.PoliticalAttitude("Orc", "Reb", 1), Is.EqualTo(Attitude.Hostile));
            Assert.That(AttitudeTable.PoliticalAttitude("Reb", "Slave", 1), Is.EqualTo(Attitude.Friendly));
            Assert.That(AttitudeTable.PoliticalAttitude("Orc", "Ass", 1), Is.EqualTo(Attitude.Friendly));
            Assert.That(AttitudeTable.PoliticalAttitude("Orc", "Ass", 2), Is.EqualTo(Attitude.Hostile));
            Assert.That(AttitudeTable.PoliticalAttitude("Orc", "Ass", 2, "Ass_MoraSulTemple_01"), Is.EqualTo(Attitude.Friendly));
            Assert.That(AttitudeTable.PoliticalAttitude("Pirate", "Out", 1), Is.EqualTo(Attitude.Neutral));
            Assert.That(AttitudeTable.PoliticalAttitude(null, "Orc", 1), Is.Null);
        }

        [Test]
        public void OutlawsPartiesAndFriendsOfTheSameFaction()
        {
            var f = new Fixture();
            Assert.That(f.Attitudes.Of(Guard, Bandit), Is.EqualTo(Attitude.Hostile), "outlaws");
            Assert.That(f.Attitudes.Of(Guard, Smith), Is.EqualTo(Attitude.Friendly), "same alignment");
            Assert.That(f.Attitudes.Of(Companion, ActorId.Player), Is.EqualTo(Attitude.Friendly), "party");
            Assert.That(f.Attitudes.Of(Guard, Wolf), Is.EqualTo(Attitude.Hostile), "humanoids vs dangerous monsters");
            Assert.That(f.Attitudes.Of(Wolf, ActorId.Player), Is.EqualTo(Attitude.Angry), "species table");
            Assert.That(f.Attitudes.Of(ActorId.Player, Wolf), Is.EqualTo(Attitude.Angry), "the player mirrors");
        }

        [Test]
        public void GuardsJudgeByTheEnclaveRecordAndFame()
        {
            var f = new Fixture();
            Assert.That(f.Attitudes.Of(Guard, ActorId.Player), Is.EqualTo(Attitude.Neutral));
            f.Fame.Add(FameKind.Enclave, "Faring", 75);
            Assert.That(f.Attitudes.Of(Guard, ActorId.Player), Is.EqualTo(Attitude.Friendly), "fame ≥ 75");
            f.Ledger.Report("Faring", CrimeType.Theft);
            Assert.That(f.Attitudes.Of(Guard, ActorId.Player), Is.EqualTo(Attitude.Angry));
            f.Ledger.Report("Faring", CrimeType.Murder);
            Assert.That(f.Attitudes.Of(Guard, ActorId.Player), Is.EqualTo(Attitude.Hostile));
            Assert.That(f.Attitudes.Of(Smith, ActorId.Player), Is.EqualTo(Attitude.Angry), "a known murder angers loyal residents");
            f.Ledger.Amnesty("Faring", new[] { Smith }, f.Fame, Crime.AmnestyFame);
            Assert.That(f.Attitudes.Of(Guard, ActorId.Player), Is.EqualTo(Attitude.Friendly));
            Assert.That(f.Fame.Of(FameKind.Enclave, "Faring"), Is.EqualTo(100));
        }

        [Test]
        public void BossesNeedPoliticalFame()
        {
            var f = new Fixture();
            Assert.That(f.Attitudes.Of(Boss, ActorId.Player), Is.EqualTo(Attitude.Neutral));
            f.Fame.Add(FameKind.Political, "Reb", 80);
            Assert.That(f.Attitudes.Of(Boss, ActorId.Player), Is.EqualTo(Attitude.Friendly));
            f.Ledger.AddPoliticalCrime("Reb");
            Assert.That(f.Attitudes.Of(Boss, ActorId.Player), Is.EqualTo(Attitude.Hostile), "a liberated enclave");
        }

        [Test]
        public void PersonalMemoryAndFights()
        {
            var f = new Fixture();
            Assert.That(f.Attitudes.Of(Rebel, ActorId.Player), Is.EqualTo(Attitude.Neutral));
            f.Ledger.RememberPersonally(Rebel, CrimeType.Attack);
            Assert.That(f.Attitudes.Of(Rebel, ActorId.Player), Is.EqualTo(Attitude.Neutral), "a knock-out is remembered but does not anger");
            f.Ledger.RecordFight(Rebel, FightResult.PlayerLost, AttackReason.StopFight, 0);
            Assert.That(f.Attitudes.Of(Rebel, ActorId.Player), Is.EqualTo(Attitude.Angry));
            f.Ledger.RememberPersonally(Rebel, CrimeType.Murder);
            Assert.That(f.Attitudes.Of(Rebel, ActorId.Player), Is.EqualTo(Attitude.Hostile));
            f.Ledger.RememberPersonally(Rebel, CrimeType.Theft);
            Assert.That(f.Ledger.PersonalOf(Rebel), Is.EqualTo(CrimeType.Murder), "crimes only escalate");
        }

        [Test]
        public void GrudgesExpireByReason()
        {
            var f = new Fixture();
            f.Hours = 10;
            f.Ledger.RecordFight(Rebel, FightResult.Cancelled, AttackReason.Theft, 10);
            f.Hours = 10.3;
            Assert.That(f.Attitudes.IsUpset(Rebel, out var reason) && reason == AttackReason.Theft, Is.True, "18 minutes later");
            f.Hours = 10.5;
            Assert.That(f.Attitudes.IsUpset(Rebel, out _), Is.False, "minor grudges last 24 game minutes");
            f.Ledger.RecordFight(Rebel, FightResult.Cancelled, AttackReason.Murder, 10.5);
            f.Hours = 11.5;
            Assert.That(f.Attitudes.IsUpset(Rebel, out _), Is.True, "major grudges last 1 h 20 min");
            f.Ledger.RecordFight(Rebel, FightResult.Cancelled, AttackReason.Kill, 0);
            f.Hours = 1000;
            Assert.That(f.Attitudes.IsUpset(Rebel, out _), Is.True, "Kill is never forgotten");
        }

        [Test]
        public void FriendTypeNpcsAreOnThePlayersSide()
        {
            var f = new Fixture();
            var lester = Fixture.Human(new ActorId("Lester"), null, null, NpcRole.Friend);
            lester.FriendType = true;
            f.Facts.Add(lester);
            Assert.That(f.Attitudes.Of(lester.Id, ActorId.Player), Is.EqualTo(Attitude.Friendly));
            f.Ledger.RememberPersonally(Rebel, CrimeType.Murder);
            Assert.That(f.Attitudes.Of(lester.Id, Rebel), Is.EqualTo(Attitude.Hostile), "mirrors the other's attitude to the player");
        }

        // ---------------------------------------------------------------- crimes

        [Test]
        public void AttackReasonsStandForCrimes()
        {
            Assert.That(CrimeCatalogue.CrimeOf(AttackReason.Theft), Is.EqualTo(CrimeType.Theft));
            Assert.That(CrimeCatalogue.CrimeOf(AttackReason.UseInteractObject), Is.EqualTo(CrimeType.Theft));
            Assert.That(CrimeCatalogue.CrimeOf(AttackReason.Murder), Is.EqualTo(CrimeType.Murder));
            Assert.That(CrimeCatalogue.CrimeOf(AttackReason.Livestock), Is.EqualTo(CrimeType.None), "livestock is never stored");
            Assert.That(CrimeCatalogue.CrimeOf(AttackReason.StopFight), Is.EqualTo(CrimeType.None));
        }

        [Test]
        public void WitnessesReportToTheEnclaveOrRememberThemselves()
        {
            var f = new Fixture();
            f.Catalogue.Memorize(f.Ledger, f.Profile(Smith), CrimeType.Theft);
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.EqualTo(CrimeType.None), "only guards and bosses report thefts");
            Assert.That(f.Ledger.PersonalOf(Smith), Is.EqualTo(CrimeType.Theft));
            f.Catalogue.Memorize(f.Ledger, f.Profile(Guard), CrimeType.Theft);
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.EqualTo(CrimeType.Theft));
            Assert.That(f.Ledger.PersonalOf(Guard), Is.EqualTo(CrimeType.None));
            f.Catalogue.Memorize(f.Ledger, f.Profile(Smith), CrimeType.Murder);
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.EqualTo(CrimeType.Murder), "anyone loyal reports a murder");
            f.Catalogue.Memorize(f.Ledger, f.Profile(Guard), CrimeType.Theft);
            Assert.That(f.Ledger.PersonalOf(Guard), Is.EqualTo(CrimeType.Theft), "below what the enclave knows: personal");
            f.Catalogue.Memorize(f.Ledger, f.Profile(Rebel), CrimeType.Murder);
            Assert.That(f.Ledger.PersonalOf(Rebel), Is.EqualTo(CrimeType.Murder), "no enclave");
        }

        [Test]
        public void GuardNewsBribesAndExcuses()
        {
            var ledger = new CrimeLedger();
            ledger.Count("Faring", CrimeType.Theft, 19);
            Assert.That(GuardNews.LevelOf(ledger.Enclave("Faring"), CrimeType.Theft, Crime), Is.EqualTo(NewsLevel.None));
            ledger.Count("Faring", CrimeType.Theft, 21);
            Assert.That(GuardNews.LevelOf(ledger.Enclave("Faring"), CrimeType.Theft, Crime), Is.EqualTo(NewsLevel.Second));
            Assert.That(GuardNews.NewsToTell(ledger.Enclave("Faring"), CrimeType.Theft, Crime), Is.EqualTo(NewsLevel.Second));
            ledger.MarkDiscussed("Faring", CrimeType.Theft);
            Assert.That(GuardNews.NewsToTell(ledger.Enclave("Faring"), CrimeType.Theft, Crime), Is.EqualTo(NewsLevel.None));
            ledger.Count("Faring", CrimeType.Murder);
            Assert.That(GuardNews.LevelOf(ledger.Enclave("Faring"), CrimeType.Murder, Crime), Is.EqualTo(NewsLevel.First));
            ledger.Report("Faring", CrimeType.Murder);
            var known = GuardNews.LevelOf(ledger.Enclave("Faring"), CrimeType.Murder, Crime);
            Assert.That(known, Is.EqualTo(NewsLevel.Known));
            Assert.That(GuardNews.CanBribe(NewsLevel.Second, Crime) && !GuardNews.CanBribe(known, Crime), Is.True);
            Assert.That(GuardNews.ExcuseSucceeds(NewsLevel.First, false, 100, 0, Crime), Is.False, "needs the perk");
            Assert.That(GuardNews.ExcuseSucceeds(NewsLevel.First, true, 0, 129, Crime), Is.True);
            Assert.That(GuardNews.ExcuseSucceeds(known, true, 100, 90, Crime), Is.True);
            Assert.That(GuardNews.ExcuseSucceeds(known, true, 80, 90, Crime), Is.False, "roll above the Theft skill");
            Assert.That(GuardNews.ExcuseSucceeds(known, true, 200, 118, Crime), Is.False, "roll must stay below 118");
        }

        [Test]
        public void WhatIsFreeToTake()
        {
            var f = new Fixture();
            var house = new OwnedZone { Id = "Smith_House", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave, Indoor = true };
            var street = new OwnedZone { Id = "Faring_Street", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.None };
            Assert.That(f.Catalogue.IsFree("It_Arrow"), Is.True);
            Assert.That(f.Catalogue.IsFree("It_Teleport_Faring"), Is.True, "teleport stones");
            Assert.That(f.Catalogue.IsFree("It_Sword"), Is.False);
            Assert.That(f.Catalogue.IsFree("It_Apple", "Plant"), Is.True);
            Assert.That(f.Catalogue.IsFree("It_Sword", dropped: true), Is.True);
            Assert.That(f.Catalogue.Bothers(f.Profile(Smith), "It_Sword", false, house, float.MaxValue), Is.True, "secured zone");
            Assert.That(f.Catalogue.Bothers(f.Profile(Smith), "It_Sword", false, street, 500), Is.False, "street items are free");
            Assert.That(f.Catalogue.Bothers(f.Profile(Smith), "It_WeaponPack", false, street, 500), Is.True, "weapon pack at a Rebel's workplace");
            Assert.That(f.Catalogue.Bothers(f.Profile(Smith), "It_WeaponPack", false, street, 1500), Is.False);
            Assert.That(f.Catalogue.Bothers(f.Profile(Smith), "Chest_01", true, street, 500), Is.True, "objects at the workplace");
            // fix-65 (r32 object_anims): G3 IsBotheredByPlayerTheftOf needs an AI zone; a chest outside every zone is free
            Assert.That(f.Catalogue.Bothers(f.Profile(Smith), "Chest_01", true, default, 0), Is.False, "outside every AI zone");
            Assert.That(f.Catalogue.Bothers(f.Profile(Companion), "It_Sword", false, house, 0), Is.False, "the party never minds");
            var orc = Fixture.Human(new ActorId("Orc"), "Orc", null);
            Assert.That(f.Catalogue.Bothers(orc, "It_Sword", false, house, 0), Is.False, "not friendly to the owner");
        }

        [Test]
        public void LivestockAndMurderWorthiness()
        {
            var f = new Fixture();
            Assert.That(f.Catalogue.IsLivestock(f.Profile(Pig), f.Profile(Smith)), Is.True);
            Assert.That(f.Catalogue.IsLivestock(f.Profile(Wolf), f.Profile(Smith)), Is.False);
            var cow = new AttitudeProfile { Id = new ActorId("Montera_Cow_01"), Species = "Cow" };
            Assert.That(f.Catalogue.IsLivestock(cow, f.Profile(Smith)), Is.False, "Montera's cow is livestock only for orcs");
            Assert.That(f.Catalogue.IsLivestock(cow, Fixture.Human(new ActorId("Orc"), "Orc", null)), Is.True);
            // fix-135: the placed cows are instances ("Montera_Cow_01#176689f0"); G3 compares the entity name, so the slaves of
            // the Montera farm do not own them (they attacked the hero who killed one)
            var placed = new AttitudeProfile { Id = new ActorId("Montera_Cow_01#176689f0"), Species = "Cow" };
            Assert.That(f.Catalogue.IsLivestock(placed, Fixture.Human(new ActorId("Slave_Normal_01#305cdb2e"), "Slave", "Montera")), Is.False);
            Assert.That(f.Catalogue.IsLivestock(placed, f.Profile(Smith)), Is.False);
            Assert.That(f.Catalogue.IsLivestock(placed, Fixture.Human(new ActorId("Merc_04#ac3f27e5"), "Orc", "Montera")), Is.True);
            var pig = new AttitudeProfile { Id = new ActorId("Pig#6cb1f889"), Species = "Pig" };
            Assert.That(f.Catalogue.IsLivestock(pig, Fixture.Human(new ActorId("Slave_Normal_01#305cdb2e"), "Slave", "Montera")), Is.True, "pigs are everyone's");
            Assert.That(CrimeCatalogue.IsMurderWorthy(Fixture.Human(new ActorId("Nobody"), null, null), AttackReason.None), Is.False);
            Assert.That(CrimeCatalogue.IsMurderWorthy(f.Profile(Smith), AttackReason.None), Is.True);
            Assert.That(CrimeCatalogue.IsMurderWorthy(f.Profile(Smith), AttackReason.Kill), Is.False);
            Assert.That(CrimeCatalogue.KnockOutIsCrime(f.Profile(Smith), AttackReason.StopFight), Is.True);
            Assert.That(CrimeCatalogue.KnockOutIsCrime(f.Profile(Guard), AttackReason.StopFight), Is.False);
            Assert.That(CrimeCatalogue.KnockOutIsCrime(f.Profile(Smith), AttackReason.Arena), Is.False);
        }

        [Test]
        public void TheMasterThiefIsNeverCountedWithAlternativeBalancing()
        {
            var zone = new OwnedZone { Id = "House", Owner = "Faring", Security = ZoneSecurity.Enclave };
            var f = new Fixture();
            f.System.PlayerTookItem("It_Sword", default, zone, stacks: 3);
            Assert.That(f.Ledger.Enclave("Faring").CountOf(CrimeType.Theft), Is.EqualTo(3), "counted unseen: + stacks");
            f.Perks.Add("Perk_MasterThief");
            f.System.PlayerTookItem("It_Sword", default, zone);
            Assert.That(f.Ledger.Enclave("Faring").CountOf(CrimeType.Theft), Is.EqualTo(3));
            var classic = new Fixture(new RuleOptions(alternativeBalancing: false));
            classic.Perks.Add("Perk_MasterThief");
            classic.System.PlayerTookItem("It_Sword", default, zone);
            Assert.That(classic.Ledger.Enclave("Faring").CountOf(CrimeType.Theft), Is.EqualTo(1));
        }

        // ---------------------------------------------------------------- broadcasts and reactions

        [Test]
        public void ReceptionExcludesSleepersBossesAndRequiresSight()
        {
            var b = new PerceptionBroadcast(BroadcastKind.Murder, ActorId.Player, Smith, new WorldPose(0, 0, 0, 0));
            var r = new BroadcastReceiver { Id = Guard, Pose = new WorldPose(0, 0, 15, 0), Detail = DetailLevel.Active };
            var guard = new Fixture().Profile(Guard);
            Assert.That(BroadcastReception.CanReceive(r, b, guard, Perception, null), Is.True);
            Assert.That(BroadcastReception.CanReceive(r, b, guard, Perception, new Wall()), Is.False, "murder needs line of sight");
            r.Mode = ReceiverMode.Asleep;
            Assert.That(BroadcastReception.CanReceive(r, b, guard, Perception, null), Is.False);
            r.Mode = ReceiverMode.Routine;
            r.Pose = new WorldPose(0, 0, 25, 0);
            Assert.That(BroadcastReception.CanReceive(r, b, guard, Perception, null), Is.False, "beyond 2000");
            r.Pose = new WorldPose(0, 0, 15, 0);
            r.Detail = DetailLevel.Abstract;
            Assert.That(BroadcastReception.CanReceive(r, b, guard, Perception, null), Is.False, "no off-screen events");
            var sound = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, default);
            var boss = new Fixture().Profile(Boss);
            r.Detail = DetailLevel.Active;
            Assert.That(BroadcastReception.CanReceive(r, sound, boss, Perception, null), Is.False, "bosses ignore fight sounds");
            r.Mode = ReceiverMode.Combat;
            Assert.That(BroadcastReception.CanReceive(r, sound, guard, Perception, null), Is.False, "already fighting");
        }

        [Test]
        public void FightSound_IndoorTestIsOnTheVictimsZone()
        {
            // fix-147, CP AssessFightSound 0x1002a510: only a victim inside an indoor zone limits the hearers to that zone.
            var guard = new Fixture().Profile(Guard);
            var r = new BroadcastReceiver { Id = Guard, Pose = new WorldPose(0, 0, 10, 0), Detail = DetailLevel.Active };
            var outdoors = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, new WorldPose(0, 0, 0, 0), AttackReason.ReactToDamage);
            var inTent = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, new WorldPose(0, 0, 0, 0), AttackReason.ReactToDamage,
                                                 sourceIndoorZone: "Tent");
            var arenaInTent = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, new WorldPose(0, 0, 0, 0), AttackReason.Arena,
                                                      sourceIndoorZone: "Tent");

            Assert.That(BroadcastReception.CanReceive(r, outdoors, guard, Perception, null), Is.True, "outdoors hears an outdoor fight");
            Assert.That(BroadcastReception.CanReceive(r, inTent, guard, Perception, null), Is.False, "outdoors does not hear a fight in a tent");
            Assert.That(BroadcastReception.CanReceive(r, arenaInTent, guard, Perception, null), Is.True, "an arena fight skips the zone test");
            r.IndoorZone = "Tent";
            Assert.That(BroadcastReception.CanReceive(r, outdoors, guard, Perception, null), Is.True,
                        "in the tent: hears the fight outside (Potros and his camp; was deaf before fix-147)");
            Assert.That(BroadcastReception.CanReceive(r, inTent, guard, Perception, null), Is.True, "same tent");
            r.IndoorZone = "House";
            Assert.That(BroadcastReception.CanReceive(r, inTent, guard, Perception, null), Is.False, "another indoor zone");
        }

        [Test]
        public void PartyMembers_DefendTheHero_UnlessWaiting_AndAttackWhoBeatHim()
        {
            var f = new Fixture();
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var player = f.Profile(ActorId.Player);
            var wolfBitesHero = new PerceptionBroadcast(BroadcastKind.FightSound, Wolf, ActorId.Player, default, AttackReason.Enemy);
            Assert.That(select.Select(wolfBitesHero, f.Profile(Companion), f.Profile(Wolf), player).ToString(), Is.EqualTo("Attack Wolf_01 (PlayerCommand)"));
            var waiting = f.Profile(Companion);
            waiting.PartyWaiting = true;
            Assert.That(select.Select(wolfBitesHero, waiting, f.Profile(Wolf), player).Kind, Is.Not.EqualTo(ReactionKind.Attack), "told to wait");
            // fix-81: CP AssessFightSound skips only duels and the arena for the party, not a scripted Kill attack.
            var killHero = new PerceptionBroadcast(BroadcastKind.FightSound, Bandit, ActorId.Player, default, AttackReason.Kill);
            Assert.That(select.Select(killHero, f.Profile(Companion), f.Profile(Bandit), player).ToString(), Is.EqualTo("Attack Bandit_01 (PlayerCommand)"));
            var angryScavenger = new PerceptionBroadcast(BroadcastKind.FightSound, Wolf, ActorId.Player, default, AttackReason.Angry);
            Assert.That(select.Select(angryScavenger, f.Profile(Companion), f.Profile(Wolf), player).ToString(), Is.EqualTo("Attack Wolf_01 (PlayerCommand)"));

            // CP AssessDefeat: the party attacks the hero's opponent (not in duels).
            var heroDown = new PerceptionBroadcast(BroadcastKind.Defeat, Bandit, ActorId.Player, default, AttackReason.Enemy);
            Assert.That(select.Select(heroDown, f.Profile(Companion), f.Profile(Bandit), player).ToString(), Is.EqualTo("Attack Bandit_01 (PlayerCommand)"));
            var duel = new PerceptionBroadcast(BroadcastKind.Defeat, Smith, ActorId.Player, default, AttackReason.Duel);
            Assert.That(select.Select(duel, f.Profile(Companion), f.Profile(Smith), player).Kind, Is.Not.EqualTo(ReactionKind.Attack));
        }

        [Test]
        public void PartyMembers_DoNotTurnOnACompanionTheHeroHit()
        {
            var f = new Fixture();
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var other = f.Profile(Smith);
            other.PartyLeader = ActorId.Player; // a second companion
            var heroHitsCompanion = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, default);
            Assert.That(select.Select(heroHitsCompanion, f.Profile(Companion), f.Profile(ActorId.Player), other).Kind,
                        Is.Not.EqualTo(ReactionKind.Attack));
        }

        [Test]
        public void FightSoundsBringFriendsOfTheVictim()
        {
            var f = new Fixture();
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var player = f.Profile(ActorId.Player);
            var sound = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, default);
            Assert.That(select.Select(sound, f.Profile(Guard), player, f.Profile(Smith)).ToString(), Is.EqualTo("Attack player (StopFight)"));
            Assert.That(select.Select(sound, f.Profile(Companion), player, f.Profile(Smith)).Reason, Is.EqualTo(AttackReason.PlayerCommand));
            Assert.That(select.Select(sound, f.Profile(Wolf), player, f.Profile(Smith)).Kind, Is.EqualTo(ReactionKind.None));
            Assert.That(select.Select(sound, f.Profile(Pig), player, f.Profile(Smith)).Kind, Is.EqualTo(ReactionKind.Flee));

            // The guard chases the thief: only guards join, the rest watch.
            var chase = new PerceptionBroadcast(BroadcastKind.FightSound, Guard, ActorId.Player, default, AttackReason.Theft);
            var otherGuard = Fixture.Human(new ActorId("Faring_Guard_02"), "Reb", "Faring", NpcRole.Guard);
            Assert.That(select.Select(chase, otherGuard, f.Profile(Guard), player).ToString(), Is.EqualTo("Attack player (StopThief)"));
            Assert.That(select.Select(chase, f.Profile(Smith), f.Profile(Guard), player).Kind, Is.EqualTo(ReactionKind.Watch));

            var duel = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, default, AttackReason.Duel);
            Assert.That(select.Select(duel, f.Profile(Guard), player, f.Profile(Smith)).Kind, Is.EqualTo(ReactionKind.Watch));

            var slaughter = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Pig, default);
            Assert.That(select.Select(slaughter, f.Profile(Smith), player, f.Profile(Pig)).Reason, Is.EqualTo(AttackReason.Livestock));
        }

        [Test]
        public void DefeatsAndMurders()
        {
            var f = new Fixture();
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var player = f.Profile(ActorId.Player);
            var defeat = new PerceptionBroadcast(BroadcastKind.Defeat, ActorId.Player, Smith, default);
            Assert.That(select.Select(defeat, f.Profile(Guard), player, f.Profile(Smith)).ToString(), Is.EqualTo("Attack player (StopFight)"));
            Assert.That(select.Select(defeat, f.Profile(Smith), f.Profile(Guard), f.Profile(Rebel)).Kind, Is.EqualTo(ReactionKind.Comment),
                        "a friend of the winner comments");

            var murder = new PerceptionBroadcast(BroadcastKind.Murder, ActorId.Player, Smith, default);
            Assert.That(select.Select(murder, f.Profile(Guard), player, f.Profile(Smith)).ToString(), Is.EqualTo("Attack player (Murder)"));
            Assert.That(select.Select(murder, f.Profile(Sheep), player, f.Profile(Smith)).Kind, Is.EqualTo(ReactionKind.Flee));
            var pigKill = new PerceptionBroadcast(BroadcastKind.Murder, ActorId.Player, Pig, default);
            Assert.That(select.Select(pigKill, f.Profile(Smith), player, f.Profile(Pig)).Reason, Is.EqualTo(AttackReason.Livestock));
            var banditKill = new PerceptionBroadcast(BroadcastKind.Murder, ActorId.Player, Bandit, default);
            Assert.That(select.Select(banditKill, f.Profile(Guard), player, f.Profile(Bandit)).Kind, Is.EqualTo(ReactionKind.Comment),
                        "CP AssessMurder: a neutral witness who disliked the victim comments (StartSayDefeatComment)");
        }

        [Test]
        public void ASeenTheftMakesTheGuardAttackAndTheEnclaveKnow()
        {
            var f = new Fixture();
            f.World.Place(Guard, 0, 5);
            f.World.Place(Smith, 0, 8);
            f.World.Place(new ActorId("Faring_Guard_far"), 0, 30);
            f.Facts.Add(Fixture.Human(new ActorId("Faring_Guard_far"), "Reb", "Faring", NpcRole.Guard));
            var house = new OwnedZone { Id = "Smith_House", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave };
            f.System.PlayerTookItem("It_Ring", new WorldPose(0, 0, 0, 0), house);

            Assert.That(f.Engaged.Select(e => (e.npc.Name, e.reason)),
                        Is.EquivalentTo(new[] { ("Faring_Guard", AttackReason.Theft), ("Faring_Smith", AttackReason.Theft) }));
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.EqualTo(CrimeType.Theft), "the guard reported it");
            Assert.That(f.Ledger.PersonalOf(Smith), Is.EqualTo(CrimeType.Theft));
            Assert.That(f.Ledger.Enclave("Faring").CountOf(CrimeType.Theft), Is.EqualTo(1));
        }

        [Test]
        public void WarningsComeAtZeroAndFourSecondsThenAnAttack()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            var drawn = new PlayerConduct { WeaponDrawn = true };
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 0).Kind, Is.EqualTo(ReactionKind.Warn));
            Assert.That(f.Broadcasts.Count(b => b.Kind == BroadcastKind.Warn), Is.EqualTo(1), "warnings are announced");
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 2).Kind, Is.EqualTo(ReactionKind.None));
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 4).Kind, Is.EqualTo(ReactionKind.Warn));
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 8).ToString(), Is.EqualTo("Attack player (ReactToWeapon)"));

            f.System.AssessPlayer(Smith, player, drawn, 20);
            Assert.That(f.System.WarningOf(Smith), Is.EqualTo(WarningStep.FirstWarning));
            f.System.AssessPlayer(Smith, player, default, 21);
            Assert.That(f.System.WarningOf(Smith), Is.EqualTo(WarningStep.None), "putting the weapon away ends it");
            Assert.That(f.System.AssessPlayer(Smith, new Percept(ActorId.Player, 900, Sense.Seen), drawn, 22).Kind, Is.EqualTo(ReactionKind.None),
                        "weapons are noticed within 700");
        }

        [Test]
        public void IntrudersAreWarnedUnlessFamous()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var zone = new OwnedZone { Id = "Faring", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave };
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone }, 0).Kind, Is.EqualTo(ReactionKind.Warn));
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone }, 8).ToString(), Is.EqualTo("Attack player (Intruder)"));
            f.Fame.Add(FameKind.Enclave, "Faring", 75);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone }, 10).Kind, Is.EqualTo(ReactionKind.None));
            var market = new OwnedZone { Id = "Market", Owner = "Faring", Security = ZoneSecurity.Public };
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = market }, 11).Kind, Is.EqualTo(ReactionKind.Observe));
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Sneaking = true }, 12).Kind, Is.EqualTo(ReactionKind.Observe));
        }

        [Test]
        public void AWeaponWarningEndsWithAWiseMoveAndAPauseWhenTheWeaponIsPutAway()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            var drawn = new PlayerConduct { WeaponDrawn = true };
            var first = f.System.AssessPlayer(Smith, player, drawn, 0);
            Assert.That((first.Kind, first.Line), Is.EqualTo((ReactionKind.Warn, "WeaponDown")));
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 4).Line, Is.EqualTo("ISaidWeaponDown"));
            var calm = f.System.AssessPlayer(Smith, player, default, 5);
            Assert.That((calm.Kind, calm.Line), Is.EqualTo((ReactionKind.Observe, "WiseMove")), "ZS_ReactToWeapon_End");
            Assert.That(f.System.WarningOf(Smith), Is.EqualTo(WarningStep.None));
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 7).Kind, Is.EqualTo(ReactionKind.None), "still observing (4 s)");
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 9).Line, Is.EqualTo("WeaponDown"), "a new sequence from the start");
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 17).ToString(), Is.EqualTo("Attack player (ReactToWeapon)"));
            Assert.That(f.Engaged.Count, Is.EqualTo(1));
        }

        [Test]
        public void AWeaponWarnerFollowsUpTo1000ThenLetsThePlayerGo()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var drawn = new PlayerConduct { WeaponDrawn = true };
            f.System.AssessPlayer(Smith, new Percept(ActorId.Player, 600, Sense.Seen), drawn, 0);
            Assert.That(f.System.AssessPlayer(Smith, new Percept(ActorId.Player, 900, Sense.Sensed), drawn, 4).Line, Is.EqualTo("ISaidWeaponDown"),
                        "beyond the 700 that start it, the warning goes on");
            var gone = f.System.AssessPlayer(Smith, new Percept(ActorId.Player, 1100, Sense.Sensed), drawn, 5);
            Assert.That((gone.Kind, gone.Line), Is.EqualTo((ReactionKind.Observe, "YesGetLost")));
            Assert.That(f.Engaged, Is.Empty);
        }

        [Test]
        public void SlavesAndFriendTypeNpcsIgnoreAWeaponAndABeatenPlayerHearsLookingForTrouble()
        {
            var f = new Fixture();
            var slave = Fixture.Human(new ActorId("Faring_Slave"), "Slave", "Faring");
            var friend = Fixture.Human(new ActorId("Diego"), "Reb", null);
            friend.FriendType = true;
            f.Facts.Add(slave);
            f.Facts.Add(friend);
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            var drawn = new PlayerConduct { WeaponDrawn = true };
            Assert.That(f.System.AssessPlayer(slave.Id, player, drawn, 0).Kind, Is.EqualTo(ReactionKind.None));
            Assert.That(f.System.AssessPlayer(friend.Id, player, drawn, 0).Kind, Is.EqualTo(ReactionKind.None));

            f.Ledger.RecordFight(Smith, FightResult.PlayerLost, AttackReason.ReactToWeapon, 0);
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 0).Line, Is.EqualTo("LookingForTroubleAgain"));
            Assert.That(f.System.AssessPlayer(Rebel, player, new PlayerConduct { WeaponDrawn = true, SpellDrawn = true }, 0).Line, Is.EqualTo("StopMagic"));
        }

        [Test]
        public void HarmlessSpellsAreNoDangerousWeapon()
        {
            SpellDef Spell(string id, SpellDeity deity) => new SpellDef(id, new[] { "It_" + id }, null, SpellCastType.Regular, SpellTarget.Caster,
                SpellKind.Target, deity, 10, 10, SpellDelivery.Instant, null, null, 0, null, null, null);
            Assert.That(CrimeSystem.IsDangerousSpell(Spell("Spl_Fireball", SpellDeity.Innos)), Is.True);
            Assert.That(CrimeSystem.IsDangerousSpell(Spell("Spl_Light", SpellDeity.Innos)), Is.False);
            Assert.That(CrimeSystem.IsDangerousSpell(Spell("Spl_Teleport_Faring", SpellDeity.None)), Is.False);
        }

        [Test]
        public void AnIntruderWhoKeepsOnlyHisWeaponStartsAWeaponWarningAfresh()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var zone = new OwnedZone { Id = "Castle", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave };
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone, WeaponDrawn = true }, 0).Line, Is.EqualTo("GetLost"),
                        "the zone comes first");
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { WeaponDrawn = true }, 6).Line, Is.EqualTo("WeaponDown"));
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { WeaponDrawn = true }, 8).Kind, Is.EqualTo(ReactionKind.None),
                        "no attack 2 s into the weapon warning");
        }

        [Test]
        public void OfAGroupOnlyTheFirstNpcWarnsAnIntruderTheOthersWatchWhileItWarns()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            f.World.Place(Guard, 2, 3);
            var zone = new OwnedZone { Id = "Camp", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave };
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            var inZone = new PlayerConduct { Zone = zone };
            Assert.That(f.System.AssessPlayer(Smith, player, inZone, 0).Line, Is.EqualTo("GetLost"));
            Assert.That(f.System.AssessPlayer(Guard, player, inZone, 0.5).Kind, Is.EqualTo(ReactionKind.None), "AssessWarn: observes, says nothing");
            Assert.That(f.System.AssessPlayer(Smith, player, inZone, 4).Line, Is.EqualTo("ISaidGetOutOfHere"));
            Assert.That(f.System.AssessPlayer(Guard, player, inZone, 6).Kind, Is.EqualTo(ReactionKind.None), "the second warning renews it");
            Assert.That(f.System.WarningOf(Guard), Is.EqualTo(WarningStep.None));
            Assert.That(f.System.AssessPlayer(Smith, player, inZone, 8).ToString(), Is.EqualTo("Attack player (Intruder)"), "the zone's warner first (fix-18)");
            Assert.That(f.System.AssessPlayer(Guard, player, inZone, 9).Line, Is.EqualTo("GetLost"), "observing ended: its own perception again");
        }

        [Test]
        public void OnlyTheZoneOwnersWarnAndOnlyWithin330Height()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var nomad = Fixture.Human(new ActorId("Nomad_01"), "Nom", null);
            f.Facts.Add(nomad);
            f.World.Place(nomad.Id, 0, 4);
            var zone = new OwnedZone { Id = "Castle", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Political };
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            f.World.PlayerPose = new WorldPose(0, 5, 0, 0);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone }, 0).Kind, Is.EqualTo(ReactionKind.None),
                        "an owner 5 m below does nothing");
            Assert.That(f.System.AssessPlayer(nomad.Id, player, new PlayerConduct { Zone = zone }, 0).Kind, Is.EqualTo(ReactionKind.Observe),
                        "others observe whatever the height");
            f.World.PlayerPose = new WorldPose(0, 0, 0, 0);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone }, 1).Line, Is.EqualTo("GetLost"));
            f.Fame.Add(FameKind.Political, "Reb", 75);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = zone }, 2).Kind, Is.EqualTo(ReactionKind.None),
                        "political fame 75 opens a political zone");
        }

        /// <summary>
        /// fix-78 (owner: Gorn, Diego and Milten shouted "WhyAreYouInHere" while the hero searched a house). CP: the hero's
        /// party never reacts (AssessPlayerZone, AssessWarn, theft, weapon); friend-type NPCs not in the party do not mind a
        /// theft, a weapon or sneaking, and observe a zone silently (ZS_ObserveSuspect has no line); the owner still warns.
        /// </summary>
        [Test]
        public void TheHerosCompanionsAndFriendsDoNotCallOutWhileHeSearchesAHouse()
        {
            var f = new Fixture();
            var diego = Fixture.Human(new ActorId("Diego"), null, null, NpcRole.Friend);
            diego.FriendType = true;
            f.Facts.Add(diego);
            f.World.Place(Companion, 0, 2);
            f.World.Place(diego.Id, 1, 2);
            var player = new Percept(ActorId.Player, 200, Sense.Seen);
            var house = new OwnedZone { Id = "Smith_House", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave, Indoor = true };
            var hut = new OwnedZone { Id = "Smith_Hut", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Public, Indoor = true };
            foreach (var conduct in new[]
                     {
                         new PlayerConduct { Zone = house }, new PlayerConduct { Zone = hut }, new PlayerConduct { Zone = house, Sneaking = true },
                         new PlayerConduct { Zone = house, WeaponDrawn = true },
                     })
                Assert.That(f.System.AssessPlayer(Companion, player, conduct, 0).Kind, Is.EqualTo(ReactionKind.None), "a party member never reacts");
            var watch = f.System.AssessPlayer(diego.Id, player, new PlayerConduct { Zone = hut }, 1);
            Assert.That(watch.Kind, Is.EqualTo(ReactionKind.Observe), "AssessPlayerZone: a non-member observes (ZS_ObserveSuspect)");
            Assert.That(CrimeSystem.ObserveLine(watch, null), Is.Null, "… silently");
            Assert.That(f.System.AssessPlayer(diego.Id, player, new PlayerConduct { Sneaking = true }, 2).Kind, Is.EqualTo(ReactionKind.None),
                        "AssessPlayerSneaking: not when Friendly");

            // nobody but the hero's friends there: no warning, no theft reaction
            f.System.PlayerUsedObject("Chest_01", new WorldPose(0, 0, 0, 0), house);
            f.System.PlayerTookItem("It_Ring", new WorldPose(0, 0, 0, 0), house);
            Assert.That(f.Engaged, Is.Empty);

            // the owner present: it warns and fights the thief; the friends still do not
            f.World.Place(Smith, 0, 3);
            Assert.That(f.System.AssessPlayer(Smith, player, new PlayerConduct { Zone = house }, 3).Line, Is.EqualTo("GetLost"));
            var heard = f.Witnesses.Deliver(f.Broadcasts.Last(b => b.Kind == BroadcastKind.Warn));
            Assert.That(heard.Select(h => h.Key), Has.No.Member(Companion).And.No.Member(diego.Id), "CP AssessWarn skips party and friend-type");
            f.System.PlayerTookItem("It_Ring", new WorldPose(0, 0, 0, 0), house);
            Assert.That(f.Engaged.Select(e => e.npc), Is.EquivalentTo(new[] { Smith }));
        }

        [Test]
        public void OnlyASneakerHearsAnObserverCallOut()
        {
            Assert.That(CrimeSystem.ObserveLine(Reaction.Of(ReactionKind.Observe, ActorId.Player), null), Is.EqualTo(CrimeSystem.SneakLine));
            Assert.That(CrimeSystem.ObserveLine(Reaction.Of(ReactionKind.Observe, ActorId.Player, AttackReason.Intruder), null), Is.Null,
                        "ZS_ObserveSuspect after AssessPlayerZone");
            Assert.That(CrimeSystem.ObserveLine(Reaction.Of(ReactionKind.Observe, ActorId.Player), BroadcastKind.Warn), Is.Null, "AssessWarn");
            Assert.That(CrimeSystem.ObserveLine(Reaction.Of(ReactionKind.Observe, ActorId.Player, AttackReason.ReactToWeapon, "WiseMove"), null),
                        Is.EqualTo("WiseMove"));
            Assert.That(CrimeSystem.ObserveLine(Reaction.Of(ReactionKind.Warn, ActorId.Player, AttackReason.Intruder, "GetLost"), null), Is.Null);
        }

        [Test]
        public void TheEnclaveSendsItsNearestTenAndGivesUpBelowOneInFive()
        {
            var members = Enumerable.Range(0, 15).Select(i => new Defender(new ActorId("M" + i.ToString("00")), 100 * i, active: i % 5 != 0)).ToList();
            var sent = EnclaveAlarm.Defenders(members, Crime);
            Assert.That(sent.Count, Is.EqualTo(10));
            Assert.That(sent.First().Name, Is.EqualTo("M01"), "M00 is down");
            Assert.That(EnclaveAlarm.GivesUp(members, Crime), Is.False);
            var beaten = Enumerable.Range(0, 11).Select(i => new Defender(new ActorId("M" + i), i, active: i < 2)).ToList();
            Assert.That(EnclaveAlarm.GivesUp(beaten, Crime), Is.True, "11 > 5 · 2");
        }

        [Test]
        public void ArenaKnockOut_CallsNobodyIn_ButFinishingTheLoserIsMurder()
        {
            // Faring arena (editor-runner-5): the hero knocks Goran down, Goran's friends watching must not attack the hero.
            var f = new Fixture();
            f.World.Place(Guard, 0, 5);
            f.World.Place(Smith, 0, 1);
            var ai = TestRules.CombatAi();
            var combat = new NpcCombatSystem(ai, TestRules.Combat(), RuleOptions.Default, new SeededRandom(1),
                                             id => new NpcCombatDef(0, 10, "Human", humanoid: true), null, () => 0, () => 0, f.Events);
            var broadcasts = new CombatBroadcasts(combat, f.Attitudes, f.Catalogue, f.Ledger, f.World, f.Events, () => f.Hours,
                                                  (enclave, attacker) => f.System.RaiseEnclaveAlarm(enclave, attacker));

            foreach (var reason in new[] { AttackReason.Arena, AttackReason.Duel })
            {
                f.Engaged.Clear();
                f.Ledger.ForgivePersonally(Smith);
                combat.Engage(Smith, ActorId.Player, reason);
                broadcasts.Tick();
                combat.Get(Smith).Disengage(); // the knock-out ends the loser's fight before the hit is reported
                broadcasts.Report(ActorId.Player, Smith, CombatBroadcasts.HitEffect.KnockedOut);
                Assert.That(f.Broadcasts.Last().Kind, Is.EqualTo(BroadcastKind.Defeat));
                Assert.That(f.Broadcasts.Last().Reason, Is.EqualTo(reason));
                Assert.That(f.Engaged, Is.Empty, $"{reason}: nobody steps in for the loser");
                Assert.That(f.Ledger.PersonalOf(Smith), Is.Not.EqualTo(CrimeType.Attack), $"{reason}: no crime");
            }

            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var arenaDefeat = new PerceptionBroadcast(BroadcastKind.Defeat, ActorId.Player, Smith, default, AttackReason.Arena);
            Assert.That(select.Select(arenaDefeat, f.Profile(Guard), f.Profile(ActorId.Player), f.Profile(Smith)).Kind,
                        Is.EqualTo(ReactionKind.Comment), "a watcher comments");
            var heroDown = new PerceptionBroadcast(BroadcastKind.Defeat, Smith, ActorId.Player, default, AttackReason.Arena);
            Assert.That(select.Select(heroDown, f.Profile(Guard), f.Profile(Smith), f.Profile(ActorId.Player)).Kind,
                        Is.Not.EqualTo(ReactionKind.Attack));

            // Finishing the downed arena opponent is a murder: his friends attack.
            broadcasts.Tick();
            broadcasts.Report(ActorId.Player, Smith, CombatBroadcasts.HitEffect.Killed);
            Assert.That(f.Engaged.Any(e => e.npc == Guard && e.reason == AttackReason.Murder), Is.True);
            broadcasts.Dispose();
            combat.Dispose();
        }

        [Test]
        public void CombatBroadcastsSettleKnockOutsAndMurders()
        {
            var f = new Fixture();
            f.World.Place(Guard, 0, 5);
            f.World.Place(Smith, 0, 1);
            var ai = TestRules.CombatAi();
            var combat = new NpcCombatSystem(ai, TestRules.Combat(), RuleOptions.Default, new SeededRandom(1),
                                             id => new NpcCombatDef(0, 10, "Human", humanoid: true), null, () => 0, () => 0, f.Events);
            var broadcasts = new CombatBroadcasts(combat, f.Attitudes, f.Catalogue, f.Ledger, f.World, f.Events, () => f.Hours,
                                                  (enclave, attacker) => f.System.RaiseEnclaveAlarm(enclave, attacker));

            combat.Engage(Smith, ActorId.Player, AttackReason.StopFight);
            broadcasts.Tick();
            combat.Get(Smith).Disengage();
            broadcasts.Report(ActorId.Player, Smith, CombatBroadcasts.HitEffect.KnockedOut);
            Assert.That(f.Ledger.PersonalOf(Smith), Is.EqualTo(CrimeType.Attack));
            Assert.That(f.Ledger.FightOf(Smith).Result, Is.EqualTo(FightResult.PlayerWon));
            Assert.That(f.Ledger.FightOf(Smith).Reason, Is.EqualTo(AttackReason.StopFight), "the reason survives the knock-out");
            Assert.That(f.Broadcasts.Last().Kind, Is.EqualTo(BroadcastKind.Defeat));
            Assert.That(f.Engaged.Any(e => e.npc == Guard && e.reason == AttackReason.StopFight), Is.True, "the guard defends the loser");

            f.Engaged.Clear();
            broadcasts.Report(ActorId.Player, Smith, CombatBroadcasts.HitEffect.Killed);
            Assert.That(f.Ledger.Enclave("Faring").CountOf(CrimeType.Murder), Is.EqualTo(1));
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.EqualTo(CrimeType.Murder), "the guard saw it");
            Assert.That(f.Engaged.Count(e => e.npc == Guard), Is.EqualTo(2), "the alarm and the murder witness");

            broadcasts.Report(Guard, ActorId.Player, CombatBroadcasts.HitEffect.KnockedOut);
            Assert.That(f.Ledger.FightOf(Guard).Result, Is.EqualTo(FightResult.PlayerLost));
            broadcasts.Dispose();
            combat.Dispose();
        }

        [Test]
        public void AnUnseenMurderOfTheEnclaveBossStillPutsTheEnclaveIntoAttack()
        {
            // editor-runner-12 (Geldern / Grok): the murder is not Known yet when the kill is reported, so only the boss flag
            // of the murder branch's kill alarm (victim.Boss && victim.Enclave == enclave) sends the enclave into Attack.
            var f = new Fixture();
            f.System.Enclaves = new EnclaveStates();
            f.World.Place(Smith, 0, 2); // alone: nobody witnesses the kills
            var combat = new NpcCombatSystem(TestRules.CombatAi(), TestRules.Combat(), RuleOptions.Default, new SeededRandom(1),
                                             id => new NpcCombatDef(0, 10, "Human", humanoid: true), null, () => 0, () => 0, f.Events);
            var broadcasts = new CombatBroadcasts(combat, f.Attitudes, f.Catalogue, f.Ledger, f.World, f.Events, () => f.Hours,
                                                  (enclave, attacker) => f.System.RaiseEnclaveAlarm(enclave, attacker));
            var alarms = new List<(string enclave, bool boss)>();
            broadcasts.KillAlarm = (enclave, killer, victim, boss) =>
            {
                alarms.Add((enclave, boss));
                f.System.RaiseEnclaveAlarm(enclave, killer, murder: true, bossKilled: boss, victim: victim);
            };

            // A plain member murdered unseen: the enclave stays quiet (no Known murder, no boss).
            broadcasts.Report(ActorId.Player, Smith, CombatBroadcasts.HitEffect.Killed);
            Assert.That(alarms, Is.EqualTo(new[] { ("Faring", false) }));
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.Not.EqualTo(CrimeType.Murder), "nobody saw it");
            Assert.That(f.System.Enclaves.Of("Faring").Status, Is.Not.EqualTo(EnclaveStatus.Attack));

            // The boss murdered unseen: a boss kill alarms the enclave → Attack.
            f.World.Npcs.Remove(Smith);
            f.World.Place(Boss, 0, 1);
            broadcasts.Report(ActorId.Player, Boss, CombatBroadcasts.HitEffect.Killed);
            Assert.That(alarms.Last(), Is.EqualTo(("Faring", true)), "the murdered boss counts as a boss kill");
            Assert.That(f.Ledger.Enclave("Faring").Known, Is.Not.EqualTo(CrimeType.Murder));
            Assert.That(f.System.Enclaves.Of("Faring").Status, Is.EqualTo(EnclaveStatus.Attack));
            broadcasts.Dispose();
            combat.Dispose();
        }

        [Test]
        public void TheLedgerSurvivesSaveAndLoad()
        {
            var ledger = new CrimeLedger();
            ledger.Report("Faring", CrimeType.Theft);
            ledger.Count("Faring", CrimeType.Theft, 25);
            ledger.MarkDiscussed("Faring", CrimeType.Theft);
            ledger.Count("Faring", CrimeType.Theft, 2);
            ledger.RememberPersonally(Smith, CrimeType.Attack);
            ledger.RecordFight(Rebel, FightResult.Cancelled, AttackReason.Murder, 12.5);
            ledger.AddPoliticalCrime("Reb");

            var stream = new MemoryStream();
            new CrimeSaveSection(ledger).Write(new BinarySaveWriter(stream));
            stream.Position = 0;
            var loaded = new CrimeLedger();
            loaded.Report("Geldern", CrimeType.Murder);
            new CrimeSaveSection(loaded).Read(new BinarySaveReader(stream), 1);

            var faring = loaded.Enclave("Faring");
            Assert.That((faring.Known, faring.CountOf(CrimeType.Theft), faring.DiscussedOf(CrimeType.Theft)), Is.EqualTo((CrimeType.Theft, 27, 25)));
            Assert.That(loaded.Enclave("Geldern").IsClean, Is.True, "loading replaces the record");
            Assert.That(loaded.PersonalOf(Smith), Is.EqualTo(CrimeType.Attack));
            Assert.That(loaded.FightOf(Rebel).GameHours, Is.EqualTo(12.5));
            Assert.That(loaded.PoliticalCrimesAgainst("Reb"), Is.EqualTo(1));
        }
    
        [Test]
        public void TheSessionLoadsTheG3TablesAndSavesTheLedger()
        {
            using (var f = new SessionFixture()
                .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
                .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
                .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime()))
            {
                var session = f.NewGame();
                Assert.That(session.Crime.Perception.FieldOfView, Is.EqualTo(180));
                Assert.That(session.Crime.Perception.BroadcastOf(BroadcastKind.FightSound).RevolutionRange, Is.EqualTo(4000));
                Assert.That(session.Crime.Rules.Theft.CarersOf("It_WeaponPack"), Is.EqualTo(new[] { "Reb" }));
                Assert.That(session.Crime.Rules.MasterThiefHidesTheft.AlternativeBalancing, Is.True);
                Assert.That(session.Crime.Attitudes.Rules.PoliticalAttitude("Orc", "Ass", 2, "AlShedim_Guard"), Is.EqualTo(Attitude.Friendly));
                Assert.That(session.Crime.Attitudes.Rules.Grudges.DurationOf(AttackReason.Theft), Is.EqualTo(0.4).Within(1e-9));

                session.Crime.Ledger.Report("Faring", CrimeType.Murder);
                session.Crime.Ledger.RememberPersonally(SessionFixture.Npc, CrimeType.Theft);
                var loaded = f.Reload(session);
                Assert.That(loaded.Crime.Ledger.Enclave("Faring").Known, Is.EqualTo(CrimeType.Murder));
                Assert.That(loaded.Crime.Ledger.PersonalOf(SessionFixture.Npc), Is.EqualTo(CrimeType.Theft));
            }
        }

        // ---------------------------------------------------------------- gate guards and important infos (story-3)

        static GateGuards GuardAtGate(bool active = true) =>
            new GateGuards(new[] { new GateGuardDef(Guard, new WorldPose(0, 0, 0, 0), active) });

        [Test]
        public void GateGuard_FirstWarning_SecondWhenEightyCloser_ThenAttack_AndResetBeyondFiveMetres()
        {
            var g = GuardAtGate();
            var guard = new WorldPose(2, 0, 0, 0);
            Assert.That(g.IsGateGuard(Guard), Is.True);
            Assert.That(g.MayWarnFirst(Guard), Is.True);
            Assert.That(g.Assess(Guard, guard, new WorldPose(4, 0, 0, 0)), Is.EqualTo(GateGuardVerdict.MayTalk));
            g.Warned(Guard, second: false, new WorldPose(4, 0, 0, 0)); // 400 cm from the point
            Assert.That(g.StatusOf(Guard), Is.EqualTo(GateGuardStatus.FirstWarnGiven));
            Assert.That(g.MayWarnFirst(Guard), Is.False);
            Assert.That(g.MayWarnSecond(Guard, new WorldPose(3.5f, 0, 0, 0)), Is.False, "only 50 closer");
            Assert.That(g.MayWarnSecond(Guard, new WorldPose(3.1f, 0, 0, 0)), Is.True, "90 closer");
            g.Warned(Guard, second: true, new WorldPose(3.1f, 0, 0, 0));
            Assert.That(g.Assess(Guard, guard, new WorldPose(2.5f, 0, 0, 0)), Is.EqualTo(GateGuardVerdict.MayTalk), "60 closer: not yet");
            Assert.That(g.Assess(Guard, guard, new WorldPose(2.2f, 0, 0, 0)), Is.EqualTo(GateGuardVerdict.Attack));

            Assert.That(g.Assess(Guard, guard, new WorldPose(7.1f, 0, 0, 0)), Is.EqualTo(GateGuardVerdict.None));
            Assert.That(g.StatusOf(Guard), Is.EqualTo(GateGuardStatus.Active), "500 from the guard: warnings forgotten");
        }

        [Test]
        public void GateGuard_PlayerAlreadyPast_IsBehind_AndAnInactiveGuardIsNoGateGuard()
        {
            var g = GuardAtGate();
            var guard = new WorldPose(2, 0, 0, 0);
            Assert.That(g.Assess(Guard, guard, new WorldPose(1, 0, 0, 0)), Is.EqualTo(GateGuardVerdict.MayTalk));
            Assert.That(g.StatusOf(Guard), Is.EqualTo(GateGuardStatus.Behind));
            Assert.That(g.MayWarnFirst(Guard), Is.False, "no warning for one who got past");

            g.SetActive(Guard, false);
            Assert.That(g.IsGateGuard(Guard), Is.False);
            Assert.That(g.Assess(Guard, guard, new WorldPose(4, 0, 0, 0)), Is.EqualTo(GateGuardVerdict.None));
            g.SetActive(Guard, true);
            Assert.That(g.MayWarnFirst(Guard), Is.True);
            Assert.That(GuardAtGate(active: false).IsGateGuard(Guard), Is.False, "GuardStatus Inactive in the world data");
            Assert.That(g.HasGuardPoint(Smith), Is.False);
        }

        [Test]
        public void GateGuard_IgnoresADrawnWeapon_TalksWhenItHasAWarning_AttacksAfterTheSecond()
        {
            var f = new Fixture();
            f.System.GateGuards = GuardAtGate();
            bool important = false;
            f.System.HasImportantInfo = npc => important;
            f.World.Place(Guard, 2, 0);
            f.World.PlayerPose = new WorldPose(4, 0, 0, 0);
            var player = new Percept(ActorId.Player, 200, Sense.Seen);
            var drawn = new PlayerConduct { WeaponDrawn = true };
            Assert.That(f.System.AssessPlayer(Guard, player, drawn, 0).Kind, Is.EqualTo(ReactionKind.None), "gate guards ignore weapons");
            important = true;
            Assert.That(f.System.AssessPlayer(Guard, player, drawn, 1).Kind, Is.EqualTo(ReactionKind.Talk));

            f.System.GateGuards.Warned(Guard, true, f.World.PlayerPose);
            f.World.PlayerPose = new WorldPose(3, 0, 0, 0);
            Assert.That(f.System.AssessPlayer(Guard, player, default, 2).ToString(), Is.EqualTo("Attack player (GateGuard)"));
            Assert.That(f.Engaged.Last(), Is.EqualTo((Guard, ActorId.Player, AttackReason.GateGuard)));

            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 3).Kind, Is.EqualTo(ReactionKind.Warn), "others still warn about the weapon");
        }

        [Test]
        public void ImportantInfo_AnNpcWithinFiveMetresAndInSight_Talks()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            f.World.PlayerPose = new WorldPose(0, 0, 0, 0);
            var near = new Percept(ActorId.Player, 300, Sense.Seen);
            Assert.That(f.System.AssessPlayer(Smith, near, default, 0).Kind, Is.EqualTo(ReactionKind.None), "no dialogs wired");
            f.System.HasImportantInfo = npc => npc == Smith;
            Assert.That(f.System.AssessPlayer(Smith, near, default, 1).Kind, Is.EqualTo(ReactionKind.Talk));
            Assert.That(f.System.AssessPlayer(Smith, new Percept(ActorId.Player, 600, Sense.Seen), default, 2).Kind, Is.EqualTo(ReactionKind.None),
                        "beyond 500");
            f.System.LineOfSight = new Wall();
            Assert.That(f.System.AssessPlayer(Smith, near, default, 3).Kind, Is.EqualTo(ReactionKind.None), "CP: needs a free line of sight");
            f.System.LineOfSight = null;
            Assert.That(f.System.AssessPlayer(Smith, near, new PlayerConduct { WeaponDrawn = true }, 4).Kind, Is.Not.EqualTo(ReactionKind.Talk),
                        "not while the player holds a weapon");
        }

        [Test]
        public void ImportantInfo_CpTalkConditions()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            f.World.PlayerPose = new WorldPose(0, 0, 0, 0);
            var near = new Percept(ActorId.Player, 300, Sense.Seen);
            f.System.HasImportantInfo = npc => npc == Smith;
            Assert.That(f.System.AssessPlayer(Smith, near, new PlayerConduct { Falling = true }, 1).Kind, Is.EqualTo(ReactionKind.None), "player falls");
            Assert.That(f.System.AssessPlayer(Smith, near, new PlayerConduct { Swimming = true }, 2).Kind, Is.EqualTo(ReactionKind.None), "player swims");
            bool fighting = true;
            f.System.InFight = _ => fighting;
            Assert.That(f.System.AssessPlayer(Smith, near, default, 3).Kind, Is.EqualTo(ReactionKind.None), "NPC fights");
            fighting = false;
            Assert.That(f.System.AssessPlayer(Smith, near, default, 4).Kind, Is.EqualTo(ReactionKind.Talk));
            Assert.That(CrimeSystem.MayStartTalk(default, npcHostile: false, npcFighting: false, npcFleeing: true, npcTransformed: false), Is.False);
            Assert.That(CrimeSystem.MayStartTalk(default, false, false, false, npcTransformed: true), Is.False);
            Assert.That(CrimeSystem.MayStartTalk(default, npcHostile: true, false, false, false), Is.False);

            var far = new Percept(ActorId.Player, 1500, Sense.Sensed);
            Assert.That(f.System.AssessPlayer(Smith, far, default, 5).Kind, Is.EqualTo(ReactionKind.None), "with a collision shape: 500");
            f.System.HasCollisionShape = _ => false;
            f.System.LineOfSight = new Wall();
            Assert.That(f.System.AssessPlayer(Smith, far, default, 6).Kind, Is.EqualTo(ReactionKind.Talk), "CP: no collision shape, 2000, no LOS");
            Assert.That(f.System.AssessPlayer(Smith, new Percept(ActorId.Player, 2100, Sense.Sensed), default, 7).Kind, Is.EqualTo(ReactionKind.None));
        }

        [Test]
        public void GateGuards_SaveTheirWarnings()
        {
            var g = GuardAtGate();
            g.Warned(Guard, false, new WorldPose(4, 0, 0, 0));
            g.SetActive(Smith, false); // a story guard status without a guard point is kept too
            var w = new MemoryStream();
            var writer = new BinarySaveWriter(w);
            new GateGuardsSaveSection(g).Write(writer);
            var again = GuardAtGate();
            new GateGuardsSaveSection(again).Read(new BinarySaveReader(new MemoryStream(w.ToArray())), 1);
            Assert.That(again.StatusOf(Guard), Is.EqualTo(GateGuardStatus.FirstWarnGiven));
            Assert.That(again.MayWarnSecond(Guard, new WorldPose(3.1f, 0, 0, 0)), Is.True, "the distance came back");
            Assert.That(again.StatusOf(Smith), Is.EqualTo(GateGuardStatus.Inactive));
        }

        // ---------------------------------------------------------------- fix-18: one zone warner, slaves, guides

        static AttitudeProfile OrcTownSlave(ActorId id) =>
            new AttitudeProfile { Id = id, Species = "Human", Humanoid = true, Alignment = "Slave", Enclave = "Faring", EnclaveAlignment = "Orc" };

        [Test]
        public void OwnersThatNoticeAnIntruderTogetherLeaveTheWarningToOneAndWatch()
        {
            var f = new Fixture();
            f.World.Place(Smith, 0, 300);
            f.World.Place(Guard, 3000, 0); // beyond the Warn broadcast (1000): G3 would let both warn
            var zone = new OwnedZone { Id = "Camp", Owner = "Faring", OwnerAlignment = "Reb", Security = ZoneSecurity.Enclave };
            var inZone = new PlayerConduct { Zone = zone };
            var near = new Percept(ActorId.Player, 300, Sense.Seen);
            var far = new Percept(ActorId.Player, 1500, Sense.Seen);
            Assert.That(f.System.AssessPlayer(Smith, near, inZone, 0).Line, Is.EqualTo("GetLost"), "the first (nearest) owner warns");
            var other = f.System.AssessPlayer(Guard, far, inZone, 0);
            Assert.That((other.Kind, other.Reason), Is.EqualTo((ReactionKind.Observe, AttackReason.Intruder)), "the other watches");
            Assert.That(f.System.WarningOf(Guard), Is.EqualTo(WarningStep.None));
            Assert.That(f.System.ZoneWarner(zone), Is.EqualTo((ActorId?)Smith));
            Assert.That(f.System.AssessPlayer(Guard, far, inZone, 4).Kind, Is.EqualTo(ReactionKind.Observe), "still one voice at the second warning");
            Assert.That(f.System.AssessPlayer(Smith, near, inZone, 4).Line, Is.EqualTo("ISaidGetOutOfHere"));
            Assert.That(f.System.AssessPlayer(Smith, near, inZone, 8).ToString(), Is.EqualTo("Attack player (Intruder)"));
            Assert.That(f.System.ZoneWarner(zone), Is.Null, "the attack ends the warner's claim");
            f.System.StopWarning(Smith);
            Assert.That(f.System.AssessPlayer(Guard, far, inZone, 9).Line, Is.EqualTo("GetLost"), "a new warner may start");
            f.System.Forget(Guard);
            Assert.That(f.System.ZoneWarner(zone), Is.Null, "a removed warner frees the zone");
        }

        [Test]
        public void SlavesOfAnOrcTownNeitherWarnForItsZonesNorSideWithTheOrcs()
        {
            var f = new Fixture();
            var slave = new ActorId("Slave_02");
            var orc = new ActorId("Orc_Warrior_01");
            f.Facts.Add(OrcTownSlave(slave));
            f.Facts.Add(new AttitudeProfile { Id = orc, Species = "Orc", Humanoid = true, Alignment = "Orc", Enclave = "Faring", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            f.World.Place(slave, 0, 300);
            var zone = new OwnedZone { Id = "Boulder", Owner = "Faring", OwnerAlignment = "Orc", Security = ZoneSecurity.Enclave };
            var r = f.System.AssessPlayer(slave, new Percept(ActorId.Player, 300, Sense.Seen), new PlayerConduct { Zone = zone }, 0);
            Assert.That(r.Kind, Is.EqualTo(ReactionKind.Observe), "G3: political attitude Slave -> Orc is Neutral: ZS_ObserveSuspect");
            Assert.That(f.System.ZoneWarner(zone), Is.Null);
            Assert.That(f.Attitudes.Of(slave, orc), Is.EqualTo(Attitude.Neutral), "the NPC's own alignment, not its enclave's");
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var orcAttacksHero = new PerceptionBroadcast(BroadcastKind.FightSound, orc, ActorId.Player, default, AttackReason.Intruder);
            Assert.That(select.Select(orcAttacksHero, f.Profile(slave), f.Profile(orc), f.Profile(ActorId.Player)).Kind,
                        Is.Not.EqualTo(ReactionKind.Attack), "slaves do not defend the orc zone");
        }

        /// <summary>
        /// fix-91 (owner: "orcs attack the smith on the farm"): Kliff, the smith on the farm east of Reddock (663,-205), is a
        /// member of the rebel enclave Reddock with no political alignment of his own (gCNPC_PS None); the orc patrol around
        /// him (Reddock_Orc_Warrior_02, Topork, two scouts) is Orc without an enclave. G3 GetPoliticalAttitude reads the
        /// NPC's own alignment, so neither side has a political opinion and the species rule (humans vs orcs: Neutral)
        /// keeps the peace until the hero frees him (Kliff_BringHimToJavier). The enclave's Reb made them enemies on sight.
        /// </summary>
        [Test]
        public void AnNpcWithoutAnAlignmentOfItsOwnIsNoPoliticalEnemyOfItsEnclavesFoes()
        {
            var f = new Fixture();
            var kliff = new ActorId("Kliff");
            var orc = new ActorId("Reddock_Orc_Warrior_02");
            var rebel = new ActorId("RebWarrior_01");
            f.Facts.Add(new AttitudeProfile { Id = kliff, Species = "Human", Humanoid = true, Enclave = "Reddock", EnclaveAlignment = "Reb", Role = NpcRole.Guard });
            f.Facts.Add(new AttitudeProfile { Id = orc, Species = "Orc", Humanoid = true, Alignment = "Orc" });
            f.Facts.Add(new AttitudeProfile { Id = rebel, Species = "Human", Humanoid = true, Alignment = "Reb", Enclave = "Reddock", EnclaveAlignment = "Reb", Role = NpcRole.Guard });
            Assert.That(f.Attitudes.Of(orc, kliff), Is.EqualTo(Attitude.Neutral), "the orcs leave the farm's smith alone");
            Assert.That(f.Attitudes.Of(kliff, orc), Is.EqualTo(Attitude.Neutral), "and he does not pick a fight with them");
            Assert.That(f.Attitudes.Of(orc, rebel), Is.EqualTo(Attitude.Hostile), "an aligned rebel is still the orcs' enemy");
            Assert.That(f.Attitudes.Of(kliff, ActorId.Player), Is.EqualTo(Attitude.Neutral), "a guard of his enclave toward the hero");
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var heroHitsOrc = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, orc, default, AttackReason.Enemy);
            Assert.That(select.Select(heroHitsOrc, f.Profile(kliff), f.Profile(ActorId.Player), f.Profile(orc)).Kind,
                        Is.Not.EqualTo(ReactionKind.Attack), "he does not join the hero's fight either");
        }

        /// <summary>
        /// fix-94 (review of fix-91): an enclave member without an alignment of its own is no guard (G3 IsGuard: own alignment
        /// == the enclave's) and never loyal to the enclave (GetPoliticalAttitude(npc, enclave) == Friendly), so Phil and
        /// Seruk at Kap Dun neither defend the town nor report crimes; an alignment the story set (SetPAL) counts over the
        /// enclave's: Knut of Bakaresh set to Nom is the Hashishin's enemy (Bakaresh_Revolution).
        /// </summary>
        [Test]
        public void UnalignedMembersAreNoGuardsNorLoyal_AStoryAlignmentBeatsTheEnclaves()
        {
            var f = new Fixture();
            var phil = new ActorId("Phil");
            f.Facts.Add(new AttitudeProfile { Id = phil, Species = "Human", Humanoid = true, Enclave = "KapDun", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            var p = f.Profile(phil);
            Assert.That(p.Guard, Is.False, "type Guard, but no alignment of his own");
            Assert.That(f.Attitudes.LoyalToEnclave(p), Is.False);
            var orcGuard = Fixture.Human(new ActorId("KapDun_Orc_Guard"), "Orc", "KapDun", NpcRole.Guard);
            Assert.That(orcGuard.Guard && f.Attitudes.LoyalToEnclave(orcGuard), Is.True, "an aligned guard still is one");
            var pirate = new AttitudeProfile { Id = new ActorId("Pirate"), Alignment = "Pirate", Enclave = "KapDun", EnclaveAlignment = "Orc", Role = NpcRole.Guard };
            Assert.That(pirate.Guard, Is.True, "G3: a pirate guards any enclave");

            var knut = new ActorId("Knut");
            var hashishin = new ActorId("Bakaresh_Ass_Warrior");
            f.Facts.Add(new AttitudeProfile { Id = knut, Species = "Human", Humanoid = true, Alignment = "Nom", AlignmentFromStory = true,
                                              Enclave = "Bakaresh", EnclaveAlignment = "Ass" });
            f.Facts.Add(Fixture.Human(hashishin, "Ass", "Bakaresh", NpcRole.Guard));
            Assert.That(f.Attitudes.Of(hashishin, knut), Is.EqualTo(Attitude.Hostile), "the story's Nom, not the enclave's Ass");
            Assert.That(f.Attitudes.LoyalToEnclave(f.Profile(knut)), Is.False, "Nom is not friendly to Ass");
            var before = new ActorId("Knut_Content");
            f.Facts.Add(new AttitudeProfile { Id = before, Species = "Human", Humanoid = true, Alignment = "Nom", Enclave = "Bakaresh", EnclaveAlignment = "Ass" });
            // fix-129: a content alignment Hostile to the enclave's is the NPC's own as well (G3 reads only the own one: the
            // rebel camps of Montera, the nomads outside Al Shedim); one friendly to it still goes by the enclave.
            Assert.That(f.Attitudes.Of(hashishin, before), Is.EqualTo(Attitude.Hostile), "a content Nom at odds with its Ass enclave");
            var ally = new ActorId("Bakaresh_Orc_Guest");
            f.Facts.Add(new AttitudeProfile { Id = ally, Species = "Orc", Humanoid = true, Alignment = "Orc", Enclave = "Bakaresh", EnclaveAlignment = "Ass" });
            Assert.That(f.Attitudes.Of(hashishin, ally), Is.EqualTo(Attitude.Friendly), "an ally of its enclave goes by the enclave");
        }

        /// <summary>
        /// fix-72 (R35-1, object_occupancy at 21:00): the Faring anvil lies in the castle courtyard, zone a4de440f, owner
        /// Faring, security Enclave. The runner's 'safe' grants only Orc political fame 80, which does not open an Enclave
        /// zone, so the hero standing at the anvil is an intruder (Rozhov's FirstWarning/SecondWarning are already in the
        /// r34 and r35 12:00 shots): warn 0 s / 4 s, attack at 8 s with reason Intruder against the hero only, and the
        /// fight sound of the first landed hit brings the owner's friends in with Enemy (Docs/combat.md §7). r34 used the
        /// anvil before the 8 s ran out, r35 (slower editor frames) did not. A stray bolt of the guard in one of his own
        /// people is the guard's hit, not the hero's: it calls nobody in against the hero.
        /// </summary>
        [Test]
        public void TheFaringCourtyardNeedsEnclaveFame_OrTheGuardAttacksTheIntruderAndTheTownJoins()
        {
            var f = new Fixture();
            var rozhov = new ActorId("Rozhov");
            var orc = new ActorId("Orc_Warrior_01");
            f.Facts.Add(new AttitudeProfile { Id = rozhov, Species = "Human", Humanoid = true, Alignment = "Orc", Enclave = "Faring", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            f.Facts.Add(new AttitudeProfile { Id = orc, Species = "Orc", Humanoid = true, Alignment = "Orc", Enclave = "Faring", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            f.World.Place(rozhov, 0, 1600);
            f.World.Place(orc, 0, 400);
            f.Fame.Add(FameKind.Political, "Orc", 80); // the runner's 'safe'
            var courtyard = new OwnedZone { Id = "a4de440f-110d-47c0-a4f8-05f169706b6d", Owner = "Faring", OwnerAlignment = "Orc", Security = ZoneSecurity.Enclave };
            var hero = new Percept(ActorId.Player, 1600, Sense.Seen);
            var atTheAnvil = new PlayerConduct { Zone = courtyard };
            Assert.That(f.System.ZoneWelcomes(courtyard), Is.False, "Orc political fame does not open a Faring Enclave zone");
            Assert.That(f.System.AssessPlayer(rozhov, hero, atTheAnvil, 0).Kind, Is.EqualTo(ReactionKind.Warn));
            Assert.That(f.System.AssessPlayer(rozhov, hero, atTheAnvil, 4).Kind, Is.EqualTo(ReactionKind.Warn));
            Assert.That(f.System.AssessPlayer(rozhov, hero, atTheAnvil, 8).ToString(), Is.EqualTo("Attack player (Intruder)"),
                        "G3 ZS_ReactToZone: the guard attacks only the intruder, after two warnings");

            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var boltInTheHero = new PerceptionBroadcast(BroadcastKind.FightSound, rozhov, ActorId.Player, default, AttackReason.Intruder);
            Assert.That(select.Select(boltInTheHero, f.Profile(orc), f.Profile(rozhov), f.Profile(ActorId.Player)).ToString(),
                        Is.EqualTo("Attack player (Enemy)"), "AssessFightSound: Intruder -> the owner's friends join (Enemy)");
            var witness = new ActorId("Orc_Warrior_02");
            f.Facts.Add(new AttitudeProfile { Id = witness, Species = "Orc", Humanoid = true, Alignment = "Orc", Enclave = "Faring", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            var boltInAnOrc = new PerceptionBroadcast(BroadcastKind.FightSound, rozhov, orc, default, AttackReason.None);
            var r = select.Select(boltInAnOrc, f.Profile(witness), f.Profile(rozhov), f.Profile(orc));
            Assert.That(r.Kind == ReactionKind.Attack && r.Target.IsPlayer, Is.False, "the shooter's stray bolt is never the hero's attack");

            var welcome = new Fixture();
            welcome.Facts.Add(f.Profile(rozhov));
            welcome.World.Place(rozhov, 0, 1600);
            welcome.Fame.Add(FameKind.Political, "Orc", 80);
            welcome.Fame.Add(FameKind.Enclave, "Faring", 75); // object_occupancy now grants it
            Assert.That(welcome.System.ZoneWelcomes(courtyard), Is.True);
            for (int s = 0; s <= 12; s += 4)
            {
                var kind = welcome.System.AssessPlayer(rozhov, hero, atTheAnvil, s).Kind;
                Assert.That(kind, Is.Not.EqualTo(ReactionKind.Warn).And.Not.EqualTo(ReactionKind.Attack), $"{s} s");
            }
        }

        [Test]
        public void TheGuideLeadingTheHeroNeverTurnsOnHim()
        {
            var f = new Fixture();
            var guide = new ActorId("Geldern_Slave_01");
            var orc = new ActorId("Orc_Warrior_04");
            f.Facts.Add(OrcTownSlave(guide));
            f.Facts.Add(new AttitudeProfile { Id = orc, Species = "Orc", Humanoid = true, Alignment = "Orc", Enclave = "Faring", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            var hero = f.Profile(ActorId.Player);
            hero.PartyLeader = guide; // G3 StartGuidePlayer: the guide leads the hero's party
            f.Facts.Add(hero);
            f.World.Place(guide, 0, 300);
            Assert.That(f.Attitudes.Of(guide, ActorId.Player), Is.EqualTo(Attitude.Friendly), "party bond");
            var zone = new OwnedZone { Id = "Mine", Owner = "Faring", OwnerAlignment = "Orc", Security = ZoneSecurity.Enclave };
            Assert.That(f.System.AssessPlayer(guide, new Percept(ActorId.Player, 300, Sense.Seen), new PlayerConduct { Zone = zone, WeaponDrawn = true }, 0).Kind,
                        Is.EqualTo(ReactionKind.None), "no zone or weapon warning from the guide");
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var orcAttacksHero = new PerceptionBroadcast(BroadcastKind.FightSound, orc, ActorId.Player, default, AttackReason.Intruder);
            var joined = select.Select(orcAttacksHero, f.Profile(guide), f.Profile(orc), f.Profile(ActorId.Player));
            Assert.That(joined.Kind == ReactionKind.Attack && joined.Target.IsPlayer, Is.False, "the guide never fights the hero it leads");
            var heroHitsOrc = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, orc, default, AttackReason.None);
            var other = select.Select(heroHitsOrc, f.Profile(guide), f.Profile(ActorId.Player), f.Profile(orc));
            Assert.That(other.Kind == ReactionKind.Attack && other.Target.IsPlayer, Is.False);
        }
    
        [Test]
        public void AWeaponWarningEndsWhenTheWarnerStartsToGuideTheHero()
        {
            // editor-runner-24: Mort warned the hero (weapon still drawn after a fight), then led him and attacked at 8 s
            var f = new Fixture();
            f.World.Place(Smith, 0, 3);
            var player = new Percept(ActorId.Player, 300, Sense.Seen);
            var drawn = new PlayerConduct { WeaponDrawn = true };
            var ended = new List<ActorId>();
            f.System.EndWarningFight = ended.Add;
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 0).Kind, Is.EqualTo(ReactionKind.Warn));
            var hero = f.Profile(ActorId.Player);
            hero.PartyLeader = Smith; // StartGuidePlayer
            f.Facts.Add(hero);
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 4).Kind, Is.EqualTo(ReactionKind.None));
            Assert.That(f.System.WarningOf(Smith), Is.EqualTo(WarningStep.None));
            Assert.That(f.System.AssessPlayer(Smith, player, drawn, 8).Kind, Is.EqualTo(ReactionKind.None), "no attack at 8 s");
            Assert.That(ended, Does.Contain(Smith), "a warning fight already running ends");
        }

        // ---------------------------------------------------------------- fix-129

        /// <summary>
        /// fix-129 (owner: Daryl's ambush near Montera, Daryl_OneByOneOrc): Daryl and his men are Reb but members of the orc
        /// enclave Montera (for its liberation). G3 GetPoliticalAttitude reads the NPC's own alignment, so they and the orcs
        /// the quest lures to their campfire (routine RebAmbush) are enemies on sight; the hero's hit on such an orc does not
        /// call the rebels against him. Allies of their enclave (a Hashishin of Montera) keep the enclave's view, also after
        /// the chapter-2 orc–Hashishin break.
        /// </summary>
        [Test]
        public void RebelCampOfAnOrcEnclave_IsHostileToItsOrcs_AndDoesNotDefendThem()
        {
            var f = new Fixture();
            var daryl = new ActorId("Daryl");
            var orc = new ActorId("Montera_Orc_03");
            var basir = new ActorId("Basir");
            f.Facts.Add(new AttitudeProfile { Id = daryl, Species = "Human", Humanoid = true, Alignment = "Reb", Enclave = "Montera", EnclaveAlignment = "Orc" });
            f.Facts.Add(new AttitudeProfile { Id = orc, Species = "Orc", Humanoid = true, Alignment = "Orc", Enclave = "Montera", EnclaveAlignment = "Orc", Role = NpcRole.Guard });
            f.Facts.Add(new AttitudeProfile { Id = basir, Species = "Human", Humanoid = true, Alignment = "Ass", Enclave = "Montera", EnclaveAlignment = "Orc" });
            Assert.That(f.Attitudes.Of(daryl, orc), Is.EqualTo(Attitude.Hostile), "the rebels attack the lured orc on sight");
            Assert.That(f.Attitudes.Of(orc, daryl), Is.EqualTo(Attitude.Hostile), "and the orc them");
            Assert.That(f.Attitudes.LoyalToEnclave(f.Profile(daryl)), Is.False);
            Assert.That(f.Attitudes.Of(basir, orc), Is.EqualTo(Attitude.Friendly), "a Hashishin of Montera sides with its orcs");
            f.Chapter = 2;
            Assert.That(f.Attitudes.Of(basir, orc), Is.EqualTo(Attitude.Friendly), "also after the chapter-2 break (enclave view kept)");
            f.Chapter = 1;

            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var player = f.Profile(ActorId.Player);
            foreach (var reason in new[] { AttackReason.None, AttackReason.ReactToDamage, AttackReason.Enemy })
            {
                var heroHitsOrc = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, orc, default, reason);
                Assert.That(select.Select(heroHitsOrc, f.Profile(daryl), player, f.Profile(orc)).Kind, Is.Not.EqualTo(ReactionKind.Attack),
                            $"the hero hitting the orc ({reason}) does not turn Daryl on him");
                var orcHitsHero = new PerceptionBroadcast(BroadcastKind.FightSound, orc, ActorId.Player, default, reason);
                Assert.That(select.Select(orcHitsHero, f.Profile(daryl), f.Profile(orc), player).Target, Is.Not.EqualTo(ActorId.Player));
            }
            var orcDown = new PerceptionBroadcast(BroadcastKind.Defeat, ActorId.Player, orc, default, AttackReason.Enemy);
            Assert.That(select.Select(orcDown, f.Profile(daryl), player, f.Profile(orc)).Kind, Is.Not.EqualTo(ReactionKind.Attack));
        }

        /// <summary>
        /// fix-129: CP AssessFightSound (0x1002a510) lets a friend of one fighter join only for the reasons of its switch
        /// (None, ReactToWeapon, StopFight, ReactToDamage → StopFight; theft → guards; Intruder, Enemy, Revolution, Murder →
        /// Enemy); every other reason (Angry, GateGuard, LeaveZone …) goes to the bystander branch. Duels and the arena are
        /// watched, and a knock-out in them (and in a scripted Kill fight, HasAttackReasonToWatch) is only commented on.
        /// </summary>
        [Test]
        public void FightSound_OnlyG3sJoinReasonsCallFriendsIn_DuelKnockOutsAreCommented()
        {
            var f = new Fixture();
            var select = new ReactionSelector(f.Attitudes, f.Catalogue);
            var player = f.Profile(ActorId.Player);
            foreach (var reason in new[] { AttackReason.Angry, AttackReason.GateGuard, AttackReason.LeaveZone, AttackReason.DriveAway, AttackReason.Duel, AttackReason.Kill })
            {
                var heroHitsSmith = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, default, reason);
                Assert.That(select.Select(heroHitsSmith, f.Profile(Guard), player, f.Profile(Smith)).Kind, Is.EqualTo(ReactionKind.Watch),
                            $"{reason}: the smith's friend watches");
            }
            foreach (var reason in new[] { AttackReason.ReactToWeapon, AttackReason.ReactToDamage, AttackReason.StopFight })
            {
                var heroHitsSmith = new PerceptionBroadcast(BroadcastKind.FightSound, ActorId.Player, Smith, default, reason);
                Assert.That(select.Select(heroHitsSmith, f.Profile(Guard), player, f.Profile(Smith)).ToString(), Is.EqualTo("Attack player (StopFight)"), reason.ToString());
            }
            Assert.That(ReactionSelector.GroupOf(AttackReason.Intruder), Is.EqualTo(ReactionSelector.JoinGroup.Enemy));
            Assert.That(ReactionSelector.GroupOf(AttackReason.Angry), Is.EqualTo(ReactionSelector.JoinGroup.Watch));

            foreach (var reason in new[] { AttackReason.Duel, AttackReason.Arena, AttackReason.Kill })
            {
                var smithDown = new PerceptionBroadcast(BroadcastKind.Defeat, ActorId.Player, Smith, default, reason);
                Assert.That(select.Select(smithDown, f.Profile(Guard), player, f.Profile(Smith)).Kind, Is.EqualTo(ReactionKind.Comment),
                            $"{reason} knock-out: the loser's friend only comments");
            }
            var brawl = new PerceptionBroadcast(BroadcastKind.Defeat, ActorId.Player, Smith, default, AttackReason.ReactToDamage);
            Assert.That(select.Select(brawl, f.Profile(Guard), player, f.Profile(Smith)).ToString(), Is.EqualTo("Attack player (StopFight)"),
                        "an ordinary knock-out still calls the loser's friend in (AssessDefeat)");
        }
    }
}
