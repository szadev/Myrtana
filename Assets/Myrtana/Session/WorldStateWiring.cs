using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// The story's world state (story-2, Docs/story-coverage.md): switchable world sectors, the quest counters that need
    /// places (EnterArea, Arena) or enclaves (FreeEnclave), enclave liberation (Attack → Flee when fewer than one in five
    /// loyal members still stand) and CP's Ardea staging after the opening fight. <see cref="Tick"/> runs every frame and
    /// does its checks once a second.
    /// </summary>
    public sealed class WorldStateServices : IDisposable
    {
        /// <summary>Real seconds between the area, arena and liberation checks.</summary>
        public const float CheckSeconds = 1f;

        readonly List<IDisposable> _owned = new List<IDisposable>();
        float _untilCheck;

        internal WorldStateServices(WorldSectors sectors, SessionAreaLocator areas, StoryWorldDirector director, EnclaveLiberation liberation,
                                    QuestAreaCounter areaQuests, QuestArenaCounter arenaQuests, QuestEnclaveCounter enclaveQuests,
                                    GateGuards gateGuards, RevolutionFights revolution)
        {
            GateGuards = gateGuards;
            Revolution = revolution;
            Sectors = sectors;
            Areas = areas;
            Director = director;
            Liberation = liberation;
            AreaQuests = areaQuests;
            ArenaQuests = arenaQuests;
            EnclaveQuests = enclaveQuests;
            _owned.Add(arenaQuests);
            _owned.Add(enclaveQuests);
            _owned.Add(liberation);
        }

        /// <summary>Gate guards: status, warnings, the gate-guard perception step (Docs/perception-crime.md §4.7).</summary>
        public GateGuards GateGuards { get; }
        /// <summary>Members of enclaves in Attack fight those their revolution attitude makes hostile (ZS_RevolutionAttack).</summary>
        public RevolutionFights Revolution { get; }
        public WorldSectors Sectors { get; }
        public SessionAreaLocator Areas { get; }
        public StoryWorldDirector Director { get; }
        public EnclaveLiberation Liberation { get; }
        public QuestAreaCounter AreaQuests { get; }
        public QuestArenaCounter ArenaQuests { get; }
        public QuestEnclaveCounter EnclaveQuests { get; }
        /// <summary>Script-side story changes (CP Script_Game callbacks: items received/used, areas entered); set by the session.</summary>
        public StoryTriggers Triggers { get; internal set; }

        /// <summary>One frame; the checks run every <see cref="CheckSeconds"/> of real time.</summary>
        public void Tick(float realDeltaSeconds)
        {
            if (realDeltaSeconds <= 0) return;
            _untilCheck -= realDeltaSeconds;
            if (_untilCheck > 0) return;
            _untilCheck = CheckSeconds;
            CheckNow();
        }

        /// <summary>The once-a-second checks (tests call it directly).</summary>
        public void CheckNow()
        {
            AreaQuests.Check();
            ArenaQuests.Check();
            Liberation.Check();
            Revolution.Check();
            Triggers?.Check();
        }

        public void Dispose()
        {
            foreach (var d in _owned) d?.Dispose();
            _owned.Clear();
            Triggers?.Dispose();
        }
    }

    public static class WorldStateWiring
    {
        /// <param name="interaction">The interactive objects (doors), created after the story services.</param>
        public static WorldStateServices Create(IContentDatabase content, IEventBus events, IGameLog log, QuestService quests,
                                                ActorRegistry actors, NpcSimulation npcs, CombatService combat, NpcCombatSystem npcCombat,
                                                CrimeServices crime, EnclaveStates enclaves, PartyServices party, ItemServices items,
                                                Func<InteractiveObjects> interaction)
        {
            var sectors = new WorldSectors(content.All<WorldSectorDef>(), events);
            npcs.SectorEnabled = sectors.IsEnabled;
            // World items (and, through InteractionWiring, interactive objects) of story sectors follow SetSectorStatus.
            items.World.SectorEnabled = sectors.IsEnabled;
            sectors.Changed += items.World.OnSectorChanged;
            var facts = crime.Attitudes.Facts as SessionAttitudeFacts;
            if (facts != null) facts.EnclaveAlignmentOverride = enclaves.AlignmentOverride;
            var areas = new SessionAreaLocator(content.All<AiZoneDef>(), party.Positions);
            var members = content.All<NpcDef>().Where(n => n.Combat?.Enclave != null)
                                 .GroupBy(n => n.Combat.Enclave, StringComparer.Ordinal)
                                 .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var doors = content.All<InteractiveObjectDef>().Where(o => o.Door != null && !string.IsNullOrEmpty(o.Name))
                               .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList(), StringComparer.OrdinalIgnoreCase);
            var gateGuards = new GateGuards(content.All<GateGuardDef>());
            crime.Crime.GateGuards = gateGuards;
            crime.Crime.Enclaves = enclaves; // every alarm puts the enclave into Attack (G3 NotifyEnclave)
            // G3 NotifyEnclave: a boss's death alarms the enclave until the hero sided with Beliar.
            crime.Crime.BossKillAlarms = () => quests.StatusOf(new Id<QuestDef>("Beliar_Contact")) != QuestStatus.Success;
            var director = new StoryWorldDirector(sectors, enclaves, crime, members, actors, interaction, doors, log, gateGuards, party.Positions)
            {
                Combat = npcCombat,
                IsItem = id => content.TryGet(id, out ItemDef _),
            };
            content.TryGet(GameStartDef.DefinitionId, out GameStartDef start);
            var liberation = new EnclaveLiberation(enclaves, members, sectors, crime, combat.Vitals, npcs, quests, party, items, events,
                                                   start, log)
            {
                Director = director,
                EnclavePositions = content.All<EnclaveDef>().Where(e => e.Position != null)
                                          .ToDictionary(e => e.Id, e => new WorldPose(e.Position[0], e.Position[1], e.Position[2], 0f), StringComparer.Ordinal),
            };
            var defs = content.All<QuestDef>();
            var areaQuests = new QuestAreaCounter(quests, defs, areas);
            var arenaQuests = new QuestArenaCounter(quests, defs, events, areas, opponent =>
            {
                var brain = npcCombat.Get(opponent);
                return brain != null && brain.IsFighting && brain.State.Target.IsPlayer && brain.State.Reason == AttackReason.Arena;
            }, opponent => npcCombat.Get(opponent)?.Disengage());
            // G3 SendToArena / ZS_Arena: the opponents of a running arena fight walk into the ring and wait there
            // (editor-runner-26: Irhabar, Anktos and the Paladin stayed on the stands 4-6 m above the arena floor).
            npcs.ArenaPointOf = id => arenaQuests.ArenaOf(id) is string arena && areas.TryGetCentre(arena, out var centre)
                ? centre : (WorldPose?)null;
            var enclaveQuests = new QuestEnclaveCounter(quests, defs, events);
            var revolution = new RevolutionFights(enclaves, crime, npcCombat);
            return new WorldStateServices(sectors, areas, director, liberation, areaQuests, arenaQuests, enclaveQuests, gateGuards, revolution);
        }
    }

    /// <summary>Named areas (the imported AI/nav zones by name) and where actors are (the party's positions port).</summary>
    public sealed class SessionAreaLocator : IAreaLocator
    {
        readonly Dictionary<string, List<AiZoneDef>> _byName = new Dictionary<string, List<AiZoneDef>>(StringComparer.OrdinalIgnoreCase);
        readonly SessionPositions _positions;

        public SessionAreaLocator(IEnumerable<AiZoneDef> zones, SessionPositions positions)
        {
            _positions = positions;
            foreach (var z in zones ?? Array.Empty<AiZoneDef>())
            {
                if (string.IsNullOrEmpty(z.Name) || z.Outline == null || z.Outline.Length < 6) continue;
                if (!_byName.TryGetValue(z.Name, out var list)) _byName[z.Name] = list = new List<AiZoneDef>();
                list.Add(z);
            }
        }

        public bool HasArea(string area) => area != null && _byName.ContainsKey(area);

        /// <summary>The pose is inside one of the zones with that name (outline in x/z, floor − below … floor + above).</summary>
        public bool Contains(string area, WorldPose pose)
        {
            if (area == null || !_byName.TryGetValue(area, out var zones)) return false;
            foreach (var z in zones)
                if (pose.Y >= z.Floor - z.Below && pose.Y <= z.Floor + z.Above && ZoneMap.Contains(z.Outline, pose.X, pose.Z)) return true;
            return false;
        }

        /// <summary>
        /// The middle of the area on its floor: the centroid of the first zone's outline at the zone entity's height. Used
        /// as the arena point [interp: G3 sends the arena fighters to the arena entity itself; its x/z origin is not
        /// imported, the outline's centroid lies on the ring's floor].
        /// </summary>
        public bool TryGetCentre(string area, out WorldPose centre)
        {
            centre = default;
            if (area == null || !_byName.TryGetValue(area, out var zones) || zones.Count == 0) return false;
            var o = zones[0].Outline;
            int n = o.Length / 2;
            double a = 0, cx = 0, cz = 0, mx = 0, mz = 0;
            for (int i = 0; i < n; i++)
            {
                double x0 = o[2 * i], z0 = o[2 * i + 1], x1 = o[2 * ((i + 1) % n)], z1 = o[2 * ((i + 1) % n) + 1];
                double cross = x0 * z1 - x1 * z0;
                a += cross;
                cx += (x0 + x1) * cross;
                cz += (z0 + z1) * cross;
                mx += x0;
                mz += z0;
            }
            if (Math.Abs(a) > 1e-6) { cx /= 3 * a; cz /= 3 * a; }
            else { cx = mx / n; cz = mz / n; }
            centre = new WorldPose((float)cx, zones[0].Floor, (float)cz, 0f);
            return true;
        }

        public bool TryIsInside(ActorId actor, string area, out bool inside)
        {
            inside = false;
            if (!HasArea(area) || _positions == null || actor.IsEmpty || !_positions.TryGetPose(actor.Name, out var pose)) return false;
            inside = Contains(area, pose);
            return true;
        }
    }

    /// <summary>
    /// The session side of <see cref="IWorldDirector"/>: world sectors (with G3's two hard-wired town switches), doors,
    /// enclave flags and revolutions, NPC classes and gate guards.
    /// </summary>
    public sealed class StoryWorldDirector : IWorldDirector, INpcPartyDirector
    {
        /// <summary>CP JoinNPC (set by the session to <see cref="NpcLifeServices.JoinNpc"/>; null = ignored).</summary>
        public Func<ActorId, ActorId, bool> JoinNpcHandler { get; set; }

        public bool JoinNpc(ActorId member, ActorId leader) => JoinNpcHandler?.Invoke(member, leader) ?? false;

        /// <summary>G3 SetSectorStatus special cases: these sectors also switch their town to the rebels.</summary>
        public const string GothaRebelSector = "G3_Myrtana_01_Gotha_NPC_02", ArdeaRebelSector = "G3_Myrtana_01_Ardea_NPC_02";
        const string Rebels = "Reb";

        readonly WorldSectors _sectors;
        readonly EnclaveStates _enclaves;
        readonly CrimeServices _crime;
        readonly Dictionary<string, List<NpcDef>> _members;
        readonly ActorRegistry _actors;
        readonly Func<InteractiveObjects> _objects;
        readonly Dictionary<string, List<string>> _doors;
        readonly IGameLog _log;
        readonly GateGuards _gateGuards;
        readonly SessionPositions _positions;

        internal StoryWorldDirector(WorldSectors sectors, EnclaveStates enclaves, CrimeServices crime, Dictionary<string, List<NpcDef>> members,
                                    ActorRegistry actors, Func<InteractiveObjects> objects, Dictionary<string, List<string>> doors, IGameLog log,
                                    GateGuards gateGuards = null, SessionPositions positions = null)
        {
            _gateGuards = gateGuards ?? new GateGuards(null);
            _positions = positions;
            _doors = doors ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _sectors = sectors;
            _enclaves = enclaves;
            _crime = crime;
            _members = members;
            _actors = actors;
            _objects = objects ?? (() => null);
            _log = log ?? NullGameLog.Instance;
        }

        public void SetSectorEnabled(string sector, bool enabled)
        {
            _sectors.Set(sector, enabled);
            if (string.Equals(sector, GothaRebelSector, StringComparison.OrdinalIgnoreCase))
            {
                // G3: Gotha → Reb, Switched, known crime, crime counts and suspect comments cleared, status Routine.
                _enclaves.SetAlignment("Gotha", Rebels);
                _enclaves.MarkSwitched("Gotha");
                _crime.Ledger.Amnesty("Gotha", MembersOf("Gotha"), null, null);
                ChangeToRoutine("Gotha");
            }
            else if (string.Equals(sector, ArdeaRebelSector, StringComparison.OrdinalIgnoreCase))
            {
                // G3: Ardea → Reb, status Routine (CP: the companions are staged by EnclaveLiberation).
                _enclaves.SetAlignment("Ardea", Rebels);
                ChangeToRoutine("Ardea");
            }
        }

        /// <summary>
        /// G3 ChangeEnclaveStatus(Routine): fame 100, crimes forgiven, Raid and Revolution off, members take the enclave's
        /// alignment again (not outlaws) and forget their fights. fix-144: the fame was never set, so a liberated Ardea
        /// (Ardea_NPC_02 on) showed 0 in the quest log [ChangeEnclaveStatus @ 100602d0, PlayerFame = 100].
        /// </summary>
        public void ChangeToRoutine(string enclave)
        {
            if (_enclaves.Of(enclave).Status == EnclaveStatus.Routine) return; // G3: only a change of status acts
            _crime.Ledger.Amnesty(enclave, MembersOf(enclave), _crime.Fame, RoutineFame);
            string alignment = _enclaves.AlignmentOverride(enclave);
            if (alignment != null)
                foreach (var id in MembersOf(enclave))
                    if (_actors.TryGet(id, out var a) && a.AlignmentOr(a.Definition.Combat?.Faction) != "Out")
                        a.ChangeAlignment(alignment == EnclaveStates.NoAlignment ? null : alignment);
            _enclaves.Set(enclave, new EnclaveState(EnclaveStatus.Routine, false, false));
        }

        /// <summary>G3 ChangeEnclaveStatus(Routine): the player's fame with the enclave afterwards.</summary>
        public const int RoutineFame = FameLedger.Maximum;

        /// <summary>
        /// fix-144 migration for saves whose fame section predates the Routine fame: every enclave that already went
        /// through <see cref="ChangeToRoutine"/> by a liberation (Gotha's or Ardea's rebel sector on, a switched town of
        /// <see cref="EnclaveSectorSwitches.All"/> with Routine) and is quiet now gets the fame G3 gave it then.
        /// Returns the enclaves changed.
        /// </summary>
        internal IReadOnlyList<string> RestoreRoutineFame()
        {
            var changed = new List<string>();
            if (_crime.Fame == null) return changed;
            var candidates = new List<string>();
            if (_sectors.IsKnown(GothaRebelSector) && _sectors.IsEnabled(GothaRebelSector)) candidates.Add("Gotha");
            if (_sectors.IsKnown(ArdeaRebelSector) && _sectors.IsEnabled(ArdeaRebelSector)) candidates.Add("Ardea");
            foreach (var town in EnclaveSectorSwitches.All)
                if (town.Routine && _enclaves.IsSwitched(town.Enclave)) candidates.Add(town.Enclave);
            foreach (var enclave in candidates)
            {
                int fame = _crime.Fame.Of(FameKind.Enclave, enclave);
                if (_enclaves.Of(enclave).Status != EnclaveStatus.Routine || fame >= RoutineFame) continue;
                _crime.Fame.Add(FameKind.Enclave, enclave, RoutineFame - fame);
                changed.Add(enclave);
            }
            return changed;
        }

        IEnumerable<ActorId> MembersOf(string enclave) =>
            _members.TryGetValue(enclave, out var list) ? list.Select(n => n.Id) : Enumerable.Empty<ActorId>();

        /// <summary>
        /// fix-261 [owner]: the talking object at a temple door (<c>MoraSul_TempleDoor_Tester</c>, <c>AlShedim_TempleDoor_Tester</c>)
        /// has done its work once its door (<c>&lt;name&gt;_01</c>) stands open; only its "nothing to say" line was left, and it
        /// kept the focus in the open doorway.
        /// </summary>
        public bool IsDoneDoorTester(string speaker)
        {
            if (string.IsNullOrEmpty(speaker) || !_doors.TryGetValue(speaker + "_01", out var ids)) return false;
            var objects = _objects();
            if (objects == null) return false;
            foreach (var id in ids)
                if (objects.IsOpen(id)) return true;
            return false;
        }

        public bool OpenDoor(string door)
        {
            var objects = _objects();
            if (objects == null || string.IsNullOrEmpty(door) || !_doors.TryGetValue(door, out var ids)) return false;
            bool opened = false;
            foreach (var id in ids)
                opened |= objects.IsOpen(id) || objects.ToggleDoor(id); // G3 PSDoor::Open [interp: a locked door stays shut]
            return opened;
        }

        public void SetEnclaveFlag(string enclaveOrMember, EnclaveFlag flag, bool value)
        {
            string enclave = EnclaveOf(enclaveOrMember);
            if (enclave == null)
            {
                _log.Info($"{flag}: '{enclaveOrMember}' is neither an enclave nor a member of one; skipped.");
                return;
            }
            var s = _enclaves.Of(enclave);
            _enclaves.Set(enclave, flag == EnclaveFlag.Raid
                ? new EnclaveState(s.Status, value, s.Revolution)
                : new EnclaveState(s.Status, s.Raid, value));
        }

        public void StartRevolution(ActorId member)
        {
            string enclave = EnclaveOf(member.Name);
            if (enclave == null) return;
            var s = _enclaves.Of(enclave);
            _enclaves.Set(enclave, new EnclaveState(EnclaveStatus.Attack, true, s.Revolution));
        }

        /// <summary>An enclave name, or the enclave of the named NPC; null when neither.</summary>
        public string EnclaveOf(string enclaveOrMember)
        {
            if (string.IsNullOrEmpty(enclaveOrMember)) return null;
            if (_members.ContainsKey(enclaveOrMember)) return enclaveOrMember;
            foreach (var key in _members.Keys)
                if (string.Equals(key, enclaveOrMember, StringComparison.OrdinalIgnoreCase)) return key;
            return _actors.TryGet(new ActorId(enclaveOrMember), out var a) ? a.Definition.Combat?.Enclave : null;
        }

        /// <summary>G3 SetClass: the NPC fights (and is judged) as a Mage, Paladin, Warrior, Ranger or nothing from now on.</summary>
        public void SetClass(ActorId npc, string npcClass)
        {
            if (npc.IsEmpty || npc.IsPlayer || !_actors.TryGet(npc, out var state)) return;
            Enum.TryParse(ClassName(npcClass), out NpcClass c);
            state.ChangeClass(c);
        }

        /// <summary>The class the story gave the NPC (G3 SetClass: Mage, Paladin, Warrior, Ranger, else None); false if none.</summary>
        public bool TryGetClass(ActorId npc, out NpcClass npcClass)
        {
            npcClass = NpcClass.None;
            if (!_actors.TryGet(npc, out var state) || !state.ClassChanged) return false;
            npcClass = state.Class;
            return true;
        }

        static string ClassName(string id)
        {
            foreach (var c in new[] { "Mage", "Paladin", "Warrior", "Ranger" })
                if (string.Equals(c, id?.Trim(), StringComparison.Ordinal)) return c;
            return "None";
        }

        /// <summary>The NPC combat AI (Wear refreshes a fighting brain's weapons); null in tools.</summary>
        public NpcCombatSystem Combat { get; set; }

        /// <summary>Whether a template is an item of the content (Wear); null = every template counts.</summary>
        public Func<string, bool> IsItem { get; set; }

        /// <summary>CP Humanize: the NPC's Immortal flag from now on (brains created later fight with it).</summary>
        public void SetImmortal(ActorId npc, bool immortal)
        {
            if (npc.IsEmpty || npc.IsPlayer || !_actors.TryGet(npc, out var state)) return;
            state.ImmortalOverride = immortal;
        }

        /// <summary>G3 Wear: the NPC equips the item (a weapon replaces its own of that kind); false for a non-item template.</summary>
        public bool Wear(ActorId npc, string item)
        {
            if (npc.IsEmpty || npc.IsPlayer || string.IsNullOrEmpty(item)) return false;
            bool isItem = IsItem?.Invoke(item) ?? true;
            if (!_actors.TryGet(npc, out var state)) return isItem;
            if (isItem)
            {
                state.Wear(item);
                Combat?.RefreshLoadout(npc);
            }
            else _log.Info($"{npc} wears {item}: not an item (a body/armour template); not shown.");
            return isItem;
        }

        public void SetGuardActive(ActorId guard, bool active) => _gateGuards.SetActive(guard, active);

        /// <summary>The story made this gate guard inactive (G3 GuardStatus Inactive).</summary>
        public bool IsGuardInactive(ActorId guard) => _gateGuards.StatusOf(guard) == GateGuardStatus.Inactive;

        public bool GateGuardMayWarn(ActorId guard, bool second)
        {
            if (!second) return _gateGuards.MayWarnFirst(guard);
            return TryGetPlayerPose(out var player) && _gateGuards.MayWarnSecond(guard, player);
        }

        public void GateGuardWarned(ActorId guard, bool second)
        {
            if (TryGetPlayerPose(out var player)) _gateGuards.Warned(guard, second, player);
        }

        bool TryGetPlayerPose(out WorldPose pose)
        {
            pose = default;
            return _positions != null && _positions.TryGetPose(ActorId.Player.Name, out pose);
        }

        static readonly Func<ActorState, bool> ClassChanged = a => a.ClassChanged;
        static readonly Func<ActorState, bool> FlagsOrWornChanged = a => a.ImmortalOverride != null || a.Worn.Count > 0;

        /// <summary>The actors whose class the story changed, by id (fix-157: no LINQ, the snapshot runs every 2 min).</summary>
        internal void CollectClassChanged(List<ActorState> into) => _actors.CollectSorted(ClassChanged, into);

        /// <summary>The actors with an Immortal override or worn items, by id.</summary>
        internal void CollectChanged(List<ActorState> into) => _actors.CollectSorted(FlagsOrWornChanged, into);

        internal bool TryGetActor(ActorId id, out ActorState state) => _actors.TryGet(id, out state);

        internal void Restore(IEnumerable<KeyValuePair<ActorId, string>> classes, IEnumerable<ActorId> inactiveGuards)
        {
            foreach (var kv in classes) SetClass(kv.Key, kv.Value);
            foreach (var g in inactiveGuards) _gateGuards.SetActive(g, false);
        }
    }

    /// <summary>
    /// Save section "story.actors": NPC classes (SetClass). Version 1 also listed inactive gate guards; since version 2
    /// they live in "world.gateGuards" and the list is written empty. Version 3 adds the Immortal flags (Humanize) and
    /// the worn items (Wear).
    /// </summary>
    public sealed class StoryWorldDirectorSaveSection : ISaveParticipant
    {
        readonly StoryWorldDirector _director;
        readonly List<ActorState> _scratch = new List<ActorState>();

        public StoryWorldDirectorSaveSection(StoryWorldDirector director) => _director = director ?? throw new ArgumentNullException(nameof(director));

        public string Section => "story.actors";
        public int Version => 3;

        public void Write(ISaveWriter w)
        {
            _director.CollectClassChanged(_scratch);
            w.WriteInt32(_scratch.Count);
            foreach (var a in _scratch)
            {
                w.WriteString(a.Id.Name);
                w.WriteString(a.Class.ToString());
            }
            w.WriteInt32(0); // gate guards: section world.gateGuards
            _director.CollectChanged(_scratch);
            w.WriteInt32(_scratch.Count);
            foreach (var a in _scratch)
            {
                w.WriteString(a.Id.Name);
                w.WriteInt32(a.ImmortalOverride == null ? -1 : a.ImmortalOverride.Value ? 1 : 0);
                w.WriteInt32(a.Worn.Count);
                foreach (var item in a.Worn) w.WriteString(item);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int n = r.ReadInt32();
            var classes = new List<KeyValuePair<ActorId, string>>(n);
            for (int i = 0; i < n; i++)
            {
                var id = new ActorId(r.ReadString());
                classes.Add(new KeyValuePair<ActorId, string>(id, r.ReadString()));
            }
            int g = r.ReadInt32();
            var guards = new List<ActorId>(g);
            for (int i = 0; i < g; i++) guards.Add(new ActorId(r.ReadString()));
            _director.Restore(classes, guards);
            if (version < 3) return;
            int c = r.ReadInt32();
            for (int i = 0; i < c; i++)
            {
                var id = new ActorId(r.ReadString());
                int immortal = r.ReadInt32();
                int worn = r.ReadInt32();
                var items = new List<string>(worn);
                for (int k = 0; k < worn; k++) items.Add(r.ReadString());
                if (!_director.TryGetActor(id, out var state)) continue;
                if (immortal >= 0) state.ImmortalOverride = immortal == 1;
                foreach (var item in items) state.Wear(item);
            }
        }
    }

    /// <summary>
    /// G3 NotifyEnclave's liberation test and CP's Ardea staging (OnEnclaveSectorRefresh), checked once a second:
    /// <list type="bullet">
    /// <item>An enclave in Attack gives up (Flee = liberation) when fewer than one in five of its politically loyal members
    /// still stand (not down, dead or talking): FreeEnclave quests count it (<see cref="EnclaveLiberated"/>) and, except for
    /// Ardea, the player's political crimes against its alignment grow by one (ChangeEnclaveStatus).</item>
    /// <item>Ardea, liberated (Orc and Flee) or handed to the rebels (Reb) and not yet switched: once the hero is more than
    /// 420 m away the companions of the opening go their ways (Diego → Braga, Gorn → GothaPrison, Milten → Monastery with a
    /// fire mage cup, Lester → Al Shedim with the temple key) and Ardea counts as switched.</item>
    /// </list>
    /// [interp] G3 runs the test inside each alarm (NotifyEnclave: the hero's attacks). Polled here, the test is made only
    /// for an enclave the hero or his party struck since the last check (a hit, knock-out or kill of one of its members),
    /// or one the story set rising (Revolution: Ardea, the towns' revolutions). fix-261 [owner]: after the hero's brawl with
    /// the Mora Sul temple orcs left the temple enclave in Attack, the zombies opening the temple lets in killed the orcs
    /// and the poll freed the enclave without the hero: a political crime against Ass, every Hashishin hostile.
    /// </summary>
    public sealed class EnclaveLiberation : IDisposable
    {
        public const string Ardea = "Ardea";
        /// <summary>CP OnEnclaveSectorRefresh: the hero is farther than this from Ardea (metres).</summary>
        public const float ArdeaStagingMetres = 420f;

        readonly EnclaveStates _enclaves;
        readonly Dictionary<string, List<NpcDef>> _members;
        readonly WorldSectors _sectors;
        readonly CrimeServices _crime;
        readonly ActorVitals _vitals;
        readonly NpcSimulation _npcs;
        readonly QuestService _quests;
        readonly PartyServices _party;
        readonly ItemServices _items;
        readonly IEventBus _events;
        readonly GameStartDef _start;
        readonly IGameLog _log;
        /// <summary>Each enclave member's enclave (the members the alarm test counts).</summary>
        readonly Dictionary<ActorId, string> _enclaveOf = new Dictionary<ActorId, string>();
        /// <summary>Enclaves the hero's side struck since the last check (G3: an alarm, which runs the give-up test).</summary>
        readonly HashSet<string> _struck = new HashSet<string>(StringComparer.Ordinal);
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        internal EnclaveLiberation(EnclaveStates enclaves, Dictionary<string, List<NpcDef>> members, WorldSectors sectors, CrimeServices crime,
                                   ActorVitals vitals, NpcSimulation npcs, QuestService quests, PartyServices party, ItemServices items,
                                   IEventBus events, GameStartDef start, IGameLog log)
        {
            _enclaves = enclaves;
            _members = members;
            _sectors = sectors;
            _crime = crime;
            _vitals = vitals;
            _npcs = npcs;
            _quests = quests;
            _party = party;
            _items = items;
            _events = events;
            _start = start;
            _log = log ?? NullGameLog.Instance;
            foreach (var kv in members)
                foreach (var npc in kv.Value)
                    _enclaveOf[npc.Id] = kv.Key;
            if (events != null)
            {
                _subscriptions.Add(events.Subscribe<ActorDefeated>(e => { if (e.ByPlayerParty || e.HurtByPlayer) Struck(e.Victim); }));
                _subscriptions.Add(events.Subscribe<HitResolved>(e =>
                {
                    if (e.Outcome.Result != HitResult.Ignored && e.Attacker.IsPlayer) Struck(e.Victim);
                }));
            }
        }

        void Struck(ActorId victim)
        {
            if (_enclaveOf.TryGetValue(victim, out var enclave)) _struck.Add(enclave);
        }

        public void Check()
        {
            foreach (var kv in _enclaves.NotQuiet)
                if (kv.Value.Status == EnclaveStatus.Attack && (kv.Value.Revolution || _struck.Contains(kv.Key)) && GivesUp(kv.Key))
                    Liberate(kv.Key);
            _struck.Clear();
            StageArdea();
            SwitchTowns();
        }

        public void Dispose()
        {
            foreach (var d in _subscriptions) d.Dispose();
            _subscriptions.Clear();
        }

        /// <summary>The director (sector switches with their G3 side effects, ChangeEnclaveStatus Routine, Wear); null in tests without one.</summary>
        internal StoryWorldDirector Director { get; set; }

        /// <summary>Where the enclave entities stand (content <see cref="EnclaveDef.Position"/>).</summary>
        internal Dictionary<string, WorldPose> EnclavePositions { get; set; } = new Dictionary<string, WorldPose>(StringComparer.Ordinal);

        /// <summary>
        /// CP OnEnclaveSectorRefresh for the towns of <see cref="EnclaveSectorSwitches.All"/> and Nordmar's big mine: a
        /// fled (liberated), not yet switched enclave whose entity is more than 200 m from the hero is exchanged.
        /// </summary>
        public void SwitchTowns()
        {
            if (!_party.Positions.TryGetPose(ActorId.Player.Name, out var hero)) return;
            foreach (var town in EnclaveSectorSwitches.All)
                if (ReadyToSwitch(town.Enclave, hero)) Switch(town);
            if (ReadyToSwitch(EnclaveSectorSwitches.BigMine, hero))
            {
                foreach (var name in EnclaveSectorSwitches.BigMineCompanions)
                {
                    var id = new ActorId(name);
                    if (!_party.Members.TryGet(id, out _)) continue; // G3: only those the hero leads
                    _party.Members.Dismiss(id);
                    _npcs.SetRoutine(id, EnclaveSectorSwitches.BigMineRoutine);
                }
                _enclaves.MarkSwitched(EnclaveSectorSwitches.BigMine);
                _log.Info("Nrd_BigMine switched: the hero's companions went to BigMine_Success");
            }
        }

        bool ReadyToSwitch(string enclave, WorldPose hero) =>
            !_enclaves.IsSwitched(enclave) && _enclaves.Of(enclave).Status == EnclaveStatus.Flee &&
            EnclavePositions.TryGetValue(enclave, out var at) && hero.DistanceTo(at) > EnclaveSectorSwitches.Metres;

        void Switch(EnclaveSectorSwitch town)
        {
            if (town.Alignment != null) _enclaves.SetAlignment(town.Enclave, town.Alignment);
            if (town.SectorOff != null) SetSector(town.SectorOff, false);
            if (town.SectorOn != null) SetSector(town.SectorOn, true);
            _enclaves.MarkSwitched(town.Enclave);
            if (town.Routine) Director?.ChangeToRoutine(town.Enclave);
            foreach (var name in town.Freed)
            {
                var id = new ActorId(name);
                if (_vitals.IsDead(id)) continue;
                _npcs.SetRoutine(id, town.FreedRoutine);
                if (name != "Lukar") continue;
                // CP: Lukar turns nomad outlaw (armour, alignment). The body is a template, not an item: Wear logs it.
                Director?.Wear(id, EnclaveSectorSwitches.LukarBody);
                if (Director != null && Director.TryGetActor(id, out var lukar)) lukar.ChangeAlignment("Nom");
            }
            _log.Info($"enclave {town.Enclave} switched: {town.SectorOff ?? "-"} off, {town.SectorOn ?? "-"} on, alignment {town.Alignment ?? "kept"}");
        }

        void SetSector(string sector, bool on)
        {
            if (Director != null) Director.SetSectorEnabled(sector, on);
            else _sectors.Set(sector, on);
        }

        /// <summary>Fewer than one in five of the enclave's loyal members stand (crime.json enclaveAlarm).</summary>
        public bool GivesUp(string enclave)
        {
            if (!_members.TryGetValue(enclave, out var members)) return false;
            var defenders = new List<Defender>();
            foreach (var npc in members)
            {
                if (!_sectors.IsEnabled(npc.Sector)) continue;
                if (!_crime.Attitudes.Facts.TryGetProfile(npc.Id, out var p) || !_crime.Attitudes.LoyalToEnclave(p)) continue;
                bool talking = _npcs.TryGet(npc.Id, out var agent) && agent.Activity == NpcActivity.Talking;
                defenders.Add(new Defender(npc.Id, 0, _vitals.StateOf(npc.Id) == VitalState.Alive && !talking));
            }
            return EnclaveAlarm.GivesUp(defenders, _crime.Rules);
        }

        /// <summary>G3 ChangeEnclaveStatus(Flee) (CP NotifyEnclave's liberation of the members: <see cref="NpcLifeServices.Liberated"/>).</summary>
        public void Liberate(string enclave)
        {
            if (_enclaves.Of(enclave).Status == EnclaveStatus.Flee) return;
            _enclaves.SetStatus(enclave, EnclaveStatus.Flee);
            _log.Info($"enclave {enclave} gives up and flees (liberated)");
            if (!string.Equals(enclave, Ardea, StringComparison.Ordinal))
            {
                string alignment = AlignmentOf(enclave);
                if (alignment != null) _crime.Ledger.AddPoliticalCrime(alignment);
            }
            _events.Publish(new EnclaveLiberated(enclave));
        }

        /// <summary>
        /// fix-198 save migration: an outlaw enclave liberated before the fix (Flee, still outlaw, not Nrd_BigMine) turns
        /// Orc as <see cref="NpcLifeServices.Liberated"/> does now. Returns the migrated enclaves.
        /// </summary>
        internal List<string> MigrateLiberatedOutlawEnclaves()
        {
            var migrated = new List<string>();
            foreach (var kv in _enclaves.NotQuiet)
                if (kv.Value.Status == EnclaveStatus.Flee && NpcLifeServices.TurnsOrcWhenLiberated(kv.Key, AlignmentOf(kv.Key)))
                    migrated.Add(kv.Key);
            foreach (var enclave in migrated)
            {
                _enclaves.SetAlignment(enclave, NpcLifeServices.OrcAlignment);
                _log.Info($"save migration (fix-198): liberated outlaw enclave {enclave} -> Orc (CP NotifyEnclave)");
            }
            return migrated;
        }

        string AlignmentOf(string enclave)
        {
            var facts = _crime.Attitudes.Facts as SessionAttitudeFacts;
            if (facts != null) return facts.EnclaveAlignmentOf(enclave);
            string story = _enclaves.AlignmentOverride(enclave);
            return story == EnclaveStates.NoAlignment ? null : story;
        }

        void StageArdea()
        {
            if (_enclaves.IsSwitched(Ardea) || _start == null) return;
            string alignment = AlignmentOf(Ardea);
            bool rebels = alignment == "Reb";
            bool liberated = alignment == "Orc" && _enclaves.Of(Ardea).Status == EnclaveStatus.Flee;
            if (!rebels && !liberated) return;
            if (!_party.Positions.TryGetPose(ActorId.Player.Name, out var hero) || hero.DistanceTo(_start.HeroPose) <= ArdeaStagingMetres) return;
            Send("Diego", "Braga");
            if (_quests.StatusOf(new Id<QuestDef>("Gorn_ShowReddock")) == QuestStatus.Running)
            {
                _quests.TrySetStatus(new Id<QuestDef>("Gorn_ShowReddock"), QuestStatus.Cancelled);
                _party.Members.Dismiss(new ActorId("Gorn"));
            }
            Send("Gorn", "GothaPrison");
            Assure("Milten", "It_FiremageCup");
            Send("Milten", "Monastery");
            if (_quests.StatusOf(new Id<QuestDef>("PC_Hero_AlShedim_TempleKey_02")) != QuestStatus.Success)
                Assure("Lester", "It_AlShedim_TempleKey_02");
            Send("Lester", "AlShedim");
            _enclaves.MarkSwitched(Ardea);
            _log.Info("Ardea staged: the companions of the opening went their ways");
        }

        void Send(string npc, string routine)
        {
            var id = new ActorId(npc);
            if (_vitals.IsDead(id) || _party.Members.TryGet(id, out _)) return;
            if (_npcs.SetRoutine(id, routine)) _npcs.ArriveNow(id);
        }

        void Assure(string npc, string item)
        {
            var id = new ActorId(npc);
            if (!_items.Inventories.Of(id).Has(item)) _items.Inventories.Give(id, item, 1);
        }
    }

    /// <summary>
    /// The revolution (G3 ContinueRoutine → ZS_RevolutionAttack, then AssessActivePerception's hostile step): while
    /// an enclave is in Attack (StartRevolution, a raid alarm), each of its members the player can meet (Active detail, in
    /// its routine) attacks the nearest NPC it notices that its revolution attitude makes Hostile (loyal members the
    /// oppressed and their friends, the oppressed the loyal ones), reason Enemy. Their hostility to the player is the
    /// player perception's (CrimeSystem.AssessPlayer). Checked once a second [interp: G3 scans every second per NPC].
    /// </summary>
    public sealed class RevolutionFights
    {
        readonly EnclaveStates _enclaves;
        readonly CrimeServices _crime;
        readonly NpcCombatSystem _combat;
        readonly List<BroadcastReceiver> _near = new List<BroadcastReceiver>();
        readonly List<PerceptionCandidate> _candidates = new List<PerceptionCandidate>();

        internal RevolutionFights(EnclaveStates enclaves, CrimeServices crime, NpcCombatSystem combat)
        {
            _enclaves = enclaves;
            _crime = crime;
            _combat = combat;
        }

        /// <summary>One round; returns the fights started.</summary>
        public int Check()
        {
            HashSet<string> attacking = null;
            foreach (var kv in _enclaves.NotQuiet)
                if (kv.Value.Status == EnclaveStatus.Attack) (attacking ?? (attacking = new HashSet<string>(StringComparer.Ordinal))).Add(kv.Key);
            if (attacking == null) return 0;
            _near.Clear();
            foreach (var r in _crime.World.Receivers())
                if (r.Detail == DetailLevel.Active && r.Mode != ReceiverMode.Dead && r.Mode != ReceiverMode.Down) _near.Add(r);
            int started = 0;
            foreach (var self in _near)
            {
                if (self.Mode != ReceiverMode.Routine) continue;
                if (!_crime.Attitudes.Facts.TryGetProfile(self.Id, out var profile) || profile.Enclave == null || !attacking.Contains(profile.Enclave)) continue;
                if (profile.CannotFight || profile.InPlayerParty) continue;
                _candidates.Clear();
                foreach (var other in _near)
                {
                    if (other.Id == self.Id) continue;
                    var attitude = _crime.Attitudes.Of(self.Id, other.Id);
                    if (attitude != Attitude.Hostile) continue;
                    _candidates.Add(new PerceptionCandidate
                    {
                        Target = new SenseTarget { Id = other.Id, Pose = other.Pose, Moving = true, IndoorZone = other.IndoorZone },
                        Attitude = attitude,
                    });
                }
                if (_candidates.Count == 0) continue;
                var observer = new SenseObserver { Id = self.Id, Pose = self.Pose, Humanoid = profile.Humanoid, IndoorZone = self.IndoorZone };
                var snapshot = PerceptionScan.Scan(observer, _candidates, _crime.Perception, _crime.World);
                if (snapshot.TryGet(PerceptionSlot.Hostile, out var enemy) && _combat.Engage(self.Id, enemy.Actor, AttackReason.Enemy)) started++;
            }
            return started;
        }
    }
}
