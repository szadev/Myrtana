using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// Perception, attitudes and crime of a session (Docs/perception-crime.md §7): the rules from content (neutral with
    /// a warning when missing), the ledger, the owned zones, the attitude service over the actors, the systems that turn
    /// combat and the player's deeds into witnesses' reactions, and the crime side of dialogs.
    /// </summary>
    public sealed class CrimeServices : IDisposable
    {
        readonly List<IDisposable> _subscriptions;

        internal CrimeServices(PerceptionRules perception, CrimeRules rules, CrimeLedger ledger, AttitudeService attitudes, ZoneMap zones,
                               SessionPerceptionWorld world, CrimeSystem crime, WitnessSystem witnesses, CombatBroadcasts combat,
                               CrimeTalk talk, IReputation reputation, List<IDisposable> subscriptions, FameLedger fame = null)
        {
            Fame = fame;
            Perception = perception;
            Rules = rules;
            Ledger = ledger;
            Attitudes = attitudes;
            Zones = zones;
            World = world;
            Crime = crime;
            Witnesses = witnesses;
            CombatBroadcasts = combat;
            Talk = talk;
            Reputation = reputation;
            _subscriptions = subscriptions;
        }

        public PerceptionRules Perception { get; }
        public CrimeRules Rules { get; }
        public CrimeLedger Ledger { get; }
        public AttitudeService Attitudes { get; }
        /// <summary>The owned AI zones of the world.</summary>
        public ZoneMap Zones { get; }
        /// <summary>Where actors are; the presentation reports the player's pose and line of sight here.</summary>
        public SessionPerceptionWorld World { get; }
        public CrimeSystem Crime { get; }
        public WitnessSystem Witnesses { get; }
        public CombatBroadcasts CombatBroadcasts { get; }
        /// <summary>Guard news, bribes and excuses for the dialogs.</summary>
        public ICrimeTalk Talk { get; }
        /// <summary>Enclave fame of NPCs for the dialogs' fame gates.</summary>
        public IReputation Reputation { get; }
        /// <summary>The player's fame (G3 ChangeEnclaveStatus Routine sets the enclave's to 100); null in bare tests.</summary>
        public FameLedger Fame { get; }

        /// <summary>The owned zone the player stands in (default = none), as of the last <see cref="ReportPlayer"/>.</summary>
        public OwnedZone PlayerZone { get; private set; }

        /// <summary>
        /// Every frame from the player controller: the player's feet pose. Entering an owned zone is announced to the NPCs
        /// who sleep there (G3 AssessPlayerEnterZone).
        /// </summary>
        public void ReportPlayer(WorldPose pose)
        {
            var zone = Zones.ZoneAt(pose);
            World.ReportPlayer(pose, zone.Indoor ? zone.Id : null);
            if (zone.Id == PlayerZone.Id) return;
            PlayerZone = zone;
            Crime.PlayerEnteredZone(zone, pose);
        }

        /// <summary>
        /// The player picked up <paramref name="stacks"/> stacks of <paramref name="item"/> lying at <paramref name="where"/>
        /// (in the world or a container): a theft when the item is not free (Docs/perception-crime.md §3.2).
        /// </summary>
        public void PlayerTookItem(string item, WorldPose where, int stacks = 1, bool dropped = false, bool lockedChestWithOwnKey = false) =>
            Crime.PlayerTookItem(item, World.TryGetPose(ActorId.Player, out var thief) ? thief : where, Zones.ZoneAt(where), stacks,
                                 dropped: dropped, lockedChestWithOwnKey: lockedChestWithOwnKey);

        /// <summary>The player used an interactive object (opened a chest or door, lay in a bed) at <paramref name="where"/>.</summary>
        public void PlayerUsedObject(string objectId, WorldPose where, bool lockedChestWithOwnKey = false) =>
            Crime.PlayerUsedObject(objectId, World.TryGetPose(ActorId.Player, out var user) ? user : where, Zones.ZoneAt(where), lockedChestWithOwnKey);

        /// <summary>Once per frame, after the NPC brains ran.</summary>
        public void Tick() => CombatBroadcasts.Tick();

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            Witnesses.Dispose();
            CombatBroadcasts.Dispose();
        }
    }

    public static class CrimeWiring
    {
        public static CrimeServices Create(IContentDatabase content, ActorRegistry actors, Hero hero, FameLedger fame, StoryChapter chapter,
                                           ItemServices items, NpcSimulation npcs, CombatService combat, NpcCombatSystem npcCombat,
                                           RuleOptions options, IRandom random, IEventBus events, Func<double> gameHours, IGameLog log,
                                           PlayerParty party = null, IGameEventFlags flags = null)
        {
            var perception = Get(content, PerceptionRules.DefinitionId, log, "perception rules", NeutralPerception);
            var attitudeRules = Get(content, AttitudeRules.DefinitionId, log, "attitude rules", AttitudeRules.Neutral);
            var crimeRules = Get(content, CrimeRules.DefinitionId, log, "crime rules", CrimeRules.Neutral);
            var enclaves = content.All<EnclaveDef>().ToList();
            var zones = new ZoneMap(content.All<AiZoneDef>(), enclaves);
            if (zones.Count == 0) log.Warning("The content has no AI zones; nothing is owned.");
            var members = EnclaveMembers(content);

            var ledger = new CrimeLedger();
            var facts = new SessionAttitudeFacts(actors, npcCombat, enclaves, () => ArmourAlignment(content, items)) { Party = party };
            var attitudes = new AttitudeService(attitudeRules, npcCombat.Rules, facts, ledger, fame, () => chapter.Current, gameHours);
            var catalogue = new CrimeCatalogue(crimeRules, attitudes);
            var world = new SessionPerceptionWorld(npcs, combat.Vitals, npcCombat, zones);
            EngageFight engage = npcCombat.Engage;
            var crime = new CrimeSystem(perception, crimeRules, attitudes, catalogue, ledger, world, engage, events, options, hero.Knows, gameHours)
            {
                LineOfSight = world, // the host's raycasts once it sets world.LineOfSight
                StoryFlags = flags != null ? flags.IsSet : (Func<string, bool>)null, // fix-218: Tjalf_Room after his key info
            };
            var witnesses = new WitnessSystem(perception, attitudes, catalogue, ledger, world, world, engage, events);
            var broadcasts = new CombatBroadcasts(npcCombat, attitudes, catalogue, ledger, world, events, gameHours,
                                                  (enclave, attacker) =>
                                                  {
                                                      // CP NotifyEnclave: the hero's companions from that enclave leave him.
                                                      if (attacker.IsPlayer)
                                                          party?.EnclaveTurnedAgainstPlayer(enclave, id => actors.TryGet(id, out var a) ? CombatWiring.CombatDefOf(a.Definition).Enclave : null);
                                                      crime.RaiseEnclaveAlarm(enclave, attacker);
                                                  });
            broadcasts.KillAlarm = (enclave, killer, victim, boss) =>
            {
                if (killer.IsPlayer)
                    party?.EnclaveTurnedAgainstPlayer(enclave, id => actors.TryGet(id, out var a) ? CombatWiring.CombatDefOf(a.Definition).Enclave : null);
                crime.RaiseEnclaveAlarm(enclave, killer, murder: true, bossKilled: boss, victim: victim);
            };
            var talk = new CrimeTalk(crimeRules, ledger, attitudes, crime, engage,
                                     npc => facts.TryGetProfile(npc, out var p) && p.Enclave != null && members.TryGetValue(p.Enclave, out var m)
                                         ? m : Enumerable.Empty<ActorId>(),
                                     npc => StopFightingPlayer(npcCombat, npc), hero, random);
            var reputation = new EnclaveReputation(fame, facts);

            var subscriptions = new List<IDisposable>
            {
                // A fight against the player that ended without a result leaves a grudge (§6.3).
                events.Subscribe<FightGivenUp>(e =>
                {
                    if (e.Target.IsPlayer) crime.FightCancelled(e.Npc, e.Reason);
                }),
                // Picking up a world item (plants by their use type are free; §3.2).
                events.Subscribe<WorldItemTaken>(e =>
                {
                    if (!content.TryGet(e.WorldItem, out WorldItemDef w)) return;
                    var where = PoseAt(w.Position);
                    string useType = content.TryGet(w.Item, out ItemDef item) ? item.Kind : null;
                    crime.PlayerTookItem(w.Item, Thief(world, where), zones.ZoneAt(where), useType: useType);
                }),
                // Chests (interactive objects): opening is UseInteractObject, every stack taken a theft (§3.1); both the loot page
                // and quick loot go through InteractiveObjects. fix-218: a chest whose key the player carries is free (G3
                // IsBotheredByPlayerTheftOf), e.g. Tjalf's chest after he gave the hero It_Key_Tjalf.
                events.Subscribe<ContainerOpened>(e =>
                    crime.PlayerUsedObject(e.ObjectId, Thief(world, PoseAt(e.Position)), zones.ZoneAt(PoseAt(e.Position)),
                                           OwnKey(content, items, e.ObjectId))),
                events.Subscribe<ContainerItemTaken>(e =>
                    crime.PlayerTookItem(e.Item, Thief(world, PoseAt(e.Position)), zones.ZoneAt(PoseAt(e.Position)),
                                         lockedChestWithOwnKey: OwnKey(content, items, e.ObjectId))),
            };
            return new CrimeServices(perception, crimeRules, ledger, attitudes, zones, world, crime, witnesses, broadcasts, talk, reputation,
                                     subscriptions, fame);
        }

        /// <summary>Whether the player carries the key of chest <paramref name="objectId"/> (<see cref="CrimeCatalogue.ChestKeyCarried"/>).</summary>
        static bool OwnKey(IContentDatabase content, ItemServices items, string objectId) =>
            objectId != null && content.TryGet(objectId, out InteractiveObjectDef chest) &&
            CrimeCatalogue.ChestKeyCarried(chest.Lock, key => items.Inventories.Of(ActorId.Player).Has(key));

        /// <summary>
        /// Where a theft is seen from: the thief, who stands at the item (G3 sends the perception from the player); the
        /// item's own spot can lie inside a shelf or chest that blocks every line of sight <b>[interp]</b>.
        /// </summary>
        static WorldPose Thief(SessionPerceptionWorld world, WorldPose item) =>
            world.TryGetPose(ActorId.Player, out var player) ? player : item;

        static WorldPose PoseAt(float[] position) =>
            position != null && position.Length >= 3 ? new WorldPose(position[0], position[1], position[2], 0) : default;

        static void StopFightingPlayer(NpcCombatSystem combat, ActorId npc)
        {
            var brain = combat.Get(npc);
            if (brain != null && brain.IsFighting && brain.State.Target.IsPlayer) brain.Disengage();
        }

        static Dictionary<string, List<ActorId>> EnclaveMembers(IContentDatabase content) =>
            content.All<NpcDef>().Where(n => n.Combat?.Enclave != null)
                   .GroupBy(n => n.Combat.Enclave, StringComparer.Ordinal)
                   .ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList(), StringComparer.Ordinal);

        /// <summary>
        /// The political alignment of the worn armour (CP: the Body item's quality read as gEPoliticalAlignment) <b></b>.
        /// Every armour of CP 1.75 has quality 0, so the player has no alignment in practice <b>[interp]</b>.
        /// </summary>
        static string ArmourAlignment(IContentDatabase content, ItemServices items)
        {
            var armour = items.Equipment.In(WornSlot.Armor);
            if (armour == null || !content.TryGet(armour, out ItemDef def)) return null;
            return def.Quality > 0 && def.Quality < Alignments.Length ? Alignments[def.Quality] : null;
        }

        /// <summary>gEPoliticalAlignment 0..9 as the attitude rules name them.</summary>
        static readonly string[] Alignments = { null, "Orc", "Nrd", "Reb", "Mid", "Ass", "Nom", "Out", "Slave", "Pirate" };

        static T Get<T>(IContentDatabase content, string id, IGameLog log, string what, Func<T> neutral) where T : class, IDefinition
        {
            if (content.TryGet(id, out T value)) return value;
            log.Warning($"The content has no {what}; using neutral ones.");
            return neutral();
        }

        /// <summary>NPCs notice nothing and no broadcast reaches anyone (a pack without perception rules).</summary>
        static PerceptionRules NeutralPerception() =>
            new PerceptionRules(PerceptionRules.DefinitionId, 1, 1, 180, 0, 0, 0, null, 0, null, new WarningTimings(4, 8, 0),
                                new WarningTimings(4, 8, 0), 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>The player's fame with the enclave an NPC belongs to (dialog fame gates).</summary>
    public sealed class EnclaveReputation : IReputation
    {
        readonly FameLedger _fame;
        readonly IAttitudeFacts _facts;

        public EnclaveReputation(FameLedger fame, IAttitudeFacts facts)
        {
            _fame = fame ?? throw new ArgumentNullException(nameof(fame));
            _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        }

        public int EnclaveFameOf(ActorId member) =>
            _facts.TryGetProfile(member, out var p) && p.Enclave != null ? _fame.Of(FameKind.Enclave, p.Enclave) : 0;

        public int PoliticalFameOf(ActorId member) =>
            _facts.TryGetProfile(member, out var p) && p.Alignment != null ? _fame.Of(FameKind.Political, p.Alignment) : 0;
    }

    /// <summary>
    /// Attitude profiles from the NPCs' content (species, alignment, enclave and its alignment) and their momentary state;
    /// the player's alignment is that of the worn armour; the hero's party members are led by the player. Transformations are
    /// not known yet <b>[interp]</b>.
    /// </summary>
    public sealed class SessionAttitudeFacts : IAttitudeFacts
    {
        readonly ActorRegistry _actors;
        readonly NpcCombatSystem _combat;
        readonly Dictionary<string, string> _enclaveAlignments;
        readonly Func<string> _playerAlignment;

        public SessionAttitudeFacts(ActorRegistry actors, NpcCombatSystem combat, IEnumerable<EnclaveDef> enclaves = null,
                                    Func<string> playerAlignment = null)
        {
            _actors = actors ?? throw new ArgumentNullException(nameof(actors));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _enclaveAlignments = (enclaves ?? Array.Empty<EnclaveDef>()).ToDictionary(e => e.Id, e => e.Alignment, StringComparer.Ordinal);
            _playerAlignment = playerAlignment ?? (() => null);
        }

        /// <summary>The hero's party (members are led by the player; null = no party).</summary>
        public PlayerParty Party { get; set; }

        /// <summary>
        /// The NPC that summoned a creature (empty = none; the player's summons are in <see cref="Party"/>): the creature is
        /// a Summoned member of its summoner's party (G3 SummonPartyMonster), so the two are friends and, in CP, it is
        /// hostile to the player.
        /// </summary>
        public Func<ActorId, ActorId> SummonerOf { get; set; }

        /// <summary>The species of the hero's animal form (null: himself); NPCs see the animal (Docs/combat.md §12.5).</summary>
        public Func<string> PlayerForm { get; set; }

        /// <summary>The story's alignment of an enclave (G3 SetSectorStatus *_NPC_02, liberation); null = the content's.</summary>
        public Func<string, string> EnclaveAlignmentOverride { get; set; }

        /// <summary>The enclave's alignment now: the story's override (<see cref="EnclaveStates.NoAlignment"/> = none), else the content's.</summary>
        public string EnclaveAlignmentOf(string enclave)
        {
            if (enclave == null) return null;
            string story = EnclaveAlignmentOverride?.Invoke(enclave);
            if (story == EnclaveStates.NoAlignment) return null;
            return story ?? ContentAlignmentOf(enclave);
        }

        /// <summary>The content's alignment of an enclave (null = unknown).</summary>
        public string ContentAlignmentOf(string enclave) =>
            enclave != null && _enclaveAlignments.TryGetValue(enclave, out var a) ? a : null;

        static PartyRole RoleOf(PartyMemberType type)
        {
            switch (type)
            {
                case PartyMemberType.Party: return PartyRole.Member;
                case PartyMemberType.Mob: return PartyRole.Mob;
                case PartyMemberType.Slave: return PartyRole.Slave;
                case PartyMemberType.Controlled: return PartyRole.Controlled;
                case PartyMemberType.Summoned: return PartyRole.Summoned;
                default: return PartyRole.None;
            }
        }

        public bool TryGetProfile(ActorId actor, out AttitudeProfile profile)
        {
            if (actor.IsPlayer)
            {
                string form = PlayerForm?.Invoke();
                profile = form == null
                    ? new AttitudeProfile { Id = actor, IsPlayer = true, Species = "Human", Humanoid = true, Alignment = _playerAlignment() }
                    : new AttitudeProfile { Id = actor, IsPlayer = true, Species = form, Transformed = true }; // G3 GetAttitudeToPlayer: 0 when transformed
                // G3 StartGuidePlayer makes the guide the hero's party leader: the two are Friendly (party bond), so the guide
                // never joins a fight against the hero it leads, nor warns him off a zone.
                if (Party?.Guide != null) profile.PartyLeader = Party.Guide.Npc;
                return true;
            }
            if (actor.IsEmpty || !_actors.TryGet(actor, out var state))
            {
                profile = default;
                return false;
            }
            var def = CombatWiring.CombatDefOf(state);
            var brain = _combat.Get(actor);
            bool fleeing = brain != null && brain.State.Phase == CombatPhase.Flee;
            profile = new AttitudeProfile
            {
                Id = actor,
                Species = def.Species,
                Humanoid = def.Humanoid,
                Evil = def.Evil,
                AmbientCreature = def.AmbientCreature,
                CannotFight = _combat.Rules.NeverFights(def),
                Alignment = state.AlignmentOr(def.Faction),
                AlignmentFromStory = state.AlignmentChanged,
                Enclave = def.Enclave,
                EnclaveAlignment = EnclaveAlignmentOf(def.Enclave),
                Role = def.Role,
                Boss = def.Boss,
                FriendType = def.FriendType,
                StoredAttitudeToPlayer = def.AttitudeToPlayer,
                Fleeing = fleeing,
                FleeingFrom = fleeing ? brain.State.Target : default,
            };
            if (Party != null && Party.TryGet(actor, out var member))
            {
                profile.PartyLeader = ActorId.Player;
                profile.PartyRole = RoleOf(member.Type);
                profile.PartyWaiting = member.Waiting;
            }
            else if (SummonerOf != null)
            {
                var summoner = SummonerOf(actor);
                if (!summoner.IsEmpty && !summoner.IsPlayer)
                {
                    profile.PartyLeader = summoner;
                    profile.PartyRole = PartyRole.Summoned;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// The perception world over the NPC simulation: poses of Near and Active NPCs, their modes from vitals, routine
    /// and combat, their indoor zones from the zone map. The presentation reports the player's pose and supplies line of
    /// sight (<see cref="LineOfSight"/>).
    /// </summary>
    public sealed class SessionPerceptionWorld : IPerceptionWorld, ILineOfSight
    {
        readonly NpcSimulation _npcs;
        readonly ActorVitals _vitals;
        readonly NpcCombatSystem _combat;
        readonly ZoneMap _zones;
        WorldPose? _playerPose;
        string _playerIndoorZone;

        public SessionPerceptionWorld(NpcSimulation npcs, ActorVitals vitals, NpcCombatSystem combat, ZoneMap zones = null)
        {
            _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _zones = zones ?? ZoneMap.Empty;
        }

        /// <summary>Raycasts of the presentation; every line is free until it sets one.</summary>
        public ILineOfSight LineOfSight { get; set; } = OpenLineOfSight.Instance;

        public bool IsClear(WorldPose from, WorldPose to) => LineOfSight.IsClear(from, to);

        public void ReportPlayer(WorldPose pose, string indoorZone)
        {
            _playerPose = pose;
            _playerIndoorZone = indoorZone;
        }

        public bool TryGetPose(ActorId actor, out WorldPose pose)
        {
            if (actor.IsPlayer)
            {
                pose = _playerPose ?? default;
                return _playerPose.HasValue;
            }
            if (_npcs.TryGet(actor, out var agent))
            {
                pose = _npcs.PoseOf(agent);
                return true;
            }
            pose = default;
            return false;
        }

        public string IndoorZoneOf(ActorId actor)
        {
            if (actor.IsPlayer) return _playerIndoorZone;
            return _npcs.TryGet(actor, out var agent) ? _zones.IndoorZoneAt(_npcs.PoseOf(agent)) : null;
        }

        public IEnumerable<BroadcastReceiver> Receivers() =>
            _npcs.Agents.Where(a => PerceptionRules.ReceivesBroadcasts(a.Detail)).Select(a =>
            {
                var pose = _npcs.PoseOf(a);
                return new BroadcastReceiver
                {
                    Id = a.Id,
                    Pose = pose,
                    Detail = a.Detail,
                    Mode = ModeOf(a),
                    IndoorZone = _zones.IndoorZoneAt(pose),
                    CombatTarget = _combat.Get(a.Id) is NpcCombatBrain b && b.IsFighting ? b.State.Target : default,
                };
            });

        public float DistanceToWorkplace(ActorId npc, WorldPose point) =>
            _npcs.TryGet(npc, out var agent) && _npcs.TryGetTarget(agent, out var work) ? Senses.Distance(work, point) : float.MaxValue;

        /// <summary>What the NPC is busy with, as far as perception cares (also read by the presentation's perception driver).</summary>
        public ReceiverMode ModeOf(ActorId npc) => _npcs.TryGet(npc, out var agent) ? ModeOf(agent) : ReceiverMode.Dead;

        /// <summary>Held by a spell (magic sleep, mist): perceives nothing, like a knocked-out NPC (Docs/combat.md §12.5).</summary>
        public Func<ActorId, bool> Paralysed { get; set; } = _ => false;

        ReceiverMode ModeOf(NpcAgent agent)
        {
            var state = _vitals.StateOf(agent.Id);
            if (state == VitalState.Dead) return ReceiverMode.Dead;
            if (state == VitalState.Down || Paralysed(agent.Id)) return ReceiverMode.Down;
            if (agent.Activity == NpcActivity.Talking) return ReceiverMode.Talking;
            var brain = _combat.Get(agent.Id);
            if (brain != null && brain.State.Phase == CombatPhase.Flee) return ReceiverMode.Flee;
            if (brain != null && brain.IsFighting) return ReceiverMode.Combat;
            if (_npcs.Sleeps(agent)) return ReceiverMode.Asleep; // in bed / on the ground, not any sleep-phase station
            return ReceiverMode.Routine;
        }
    }
}
