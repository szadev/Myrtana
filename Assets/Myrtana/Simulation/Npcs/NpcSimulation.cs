using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>Tuning of <see cref="NpcSimulation"/>.</summary>
    public sealed class NpcSimulationSettings
    {
        /// <summary>Speed of abstract travel, m/s of real time (G3 walk clips: 1.6 m/s).</summary>
        public float TravelSpeed { get; set; } = 1.6f;
        /// <summary>Speed of abstract travel for species that run to their points (<see cref="AmbientRules.RunsWhenTravelling"/>), m/s [interp].</summary>
        public float RunTravelSpeed { get; set; } = 4f;
        /// <summary>Straight-line distance × this = assumed path length.</summary>
        public float PathFactor { get; set; } = 1.3f;
        /// <summary>Abstract NPCs processed per tick (round-robin; G3 processes one off-screen NPC per frame).</summary>
        public int AbstractBudget { get; set; } = 64;
        /// <summary>Real seconds before an NPC that found nothing free tries again.</summary>
        public float RetryRealSeconds { get; set; } = 1f;
    }

    /// <summary>
    /// The daily routines of all NPCs (gameplay-architecture.md §6, gameplay-logic.md §5.5). It owns every
    /// <see cref="NpcAgent"/>; views only show them. Each tick it re-plans NPCs whose day phase changed or who stayed long
    /// enough at a roam/local station, claims stations and moves NPCs:
    /// <list type="bullet">
    /// <item><b>Active</b> NPCs are walked by their body in the presentation layer, which reports the pose and the arrival;</item>
    /// <item><b>Near</b> and <b>Abstract</b> NPCs travel as a timed straight line (path length ≈ distance × 1.3);</item>
    /// <item>Active and Near NPCs are processed every tick, Abstract ones round-robin within a budget.</item>
    /// </list>
    /// </summary>
    public sealed partial class NpcSimulation : IActorStore
    {
        readonly List<NpcAgent> _agents = new List<NpcAgent>();
        readonly Dictionary<ActorId, NpcAgent> _byId = new Dictionary<ActorId, NpcAgent>();
        readonly HashSet<NpcAgent> _engaged = new HashSet<NpcAgent>();
        readonly List<NpcAgent> _engagedScratch = new List<NpcAgent>();
        readonly List<NpcAgent> _changed = new List<NpcAgent>();
        readonly Action<NpcAgent> _onFirstChange;
        bool _changedSorted = true;
        int _nextOrder;
        static readonly Comparison<NpcAgent> ByOrder = (x, y) => x.Order.CompareTo(y.Order);
        readonly RoutineSchedule _schedule;
        readonly StationClaims _claims;
        readonly DestinationPlanner _planner;
        readonly IGameClock _clock;
        readonly IRandom _random;
        int _cursor;

        public NpcSimulation(IEnumerable<NpcDef> npcs, RoutinePointCatalog points, RoutineSchedule schedule, IGameClock clock,
                             IRandom random, NpcSimulationSettings settings = null)
        {
            Points = points;
            _schedule = schedule;
            _clock = clock;
            _random = random;
            Settings = settings ?? new NpcSimulationSettings();
            // an NPC of a switched-off sector has no body: its claim (from the routine start or a save) holds nothing (fix-67)
            _claims = new StationClaims(points) { UserAbsent = a => _byId.TryGetValue(a, out var user) && !IsInWorld(user) };
            _planner = new DestinationPlanner(points, _claims, random);
            _onFirstChange = OnFirstChange;
            foreach (var def in npcs)
            {
                if (def.Spawn == null || def.Id.IsPlayer || _byId.ContainsKey(def.Id)) continue;
                var agent = new NpcAgent(def, def.Spawn.Value) { FirstChange = _onFirstChange, Order = _nextOrder++, HasSpawnState = true };
                _agents.Add(agent);
                _byId[def.Id] = agent;
            }
        }

        public RoutinePointCatalog Points { get; }
        public NpcSimulationSettings Settings { get; }
        public IReadOnlyList<NpcAgent> Agents => _agents;
        public StationClaims Claims => _claims;

        public bool TryGet(ActorId id, out NpcAgent agent) => _byId.TryGetValue(id, out agent);

        /// <summary>
        /// fix-157: the NPCs whose saved state was set since their spawn (<see cref="NpcAgent.ChangedSinceSpawn"/>), in
        /// agent order (equal states give equal saves); every other NPC is in its spawn state. The save walks only these.
        /// </summary>
        internal IReadOnlyList<NpcAgent> ChangedAgents
        {
            get
            {
                if (!_changedSorted)
                {
                    _changed.Sort(ByOrder);
                    _changedSorted = true;
                }
                return _changed;
            }
        }

        void OnFirstChange(NpcAgent agent)
        {
            if (_changed.Count > 0 && _changed[_changed.Count - 1].Order > agent.Order) _changedSorted = false;
            _changed.Add(agent);
        }

        /// <summary>
        /// After loading: NPCs <paramref name="atSpawn"/> finds in their spawn state lose their change mark, and the list of
        /// changed NPCs is built again in agent order.
        /// </summary>
        internal void RebuildChanged(Func<NpcAgent, bool> atSpawn)
        {
            _changed.Clear();
            _changedSorted = false; // the agent list's order can differ from the creation order (removed, re-added)
            foreach (var agent in _agents)
            {
                if (agent.ChangedSinceSpawn && agent.HasSpawnState && atSpawn(agent)) agent.MarkUnchanged();
                if (agent.ChangedSinceSpawn) _changed.Add(agent);
            }
        }

        /// <summary>An NPC that appears during play (a summoned creature) at <paramref name="pose"/>; null when the id is taken.</summary>
        public NpcAgent Add(NpcDef def, WorldPose pose)
        {
            if (def == null || def.Id.IsPlayer || _byId.ContainsKey(def.Id)) return null;
            var agent = new NpcAgent(def, pose) { FirstChange = _onFirstChange, Order = _nextOrder++ };
            agent.Activity = NpcActivity.Idle; // marks it changed: it has no spawn state to come back to
            _agents.Add(agent);
            _byId[def.Id] = agent;
            return agent;
        }

        /// <summary>Removes an NPC that appeared during play (its view goes with it).</summary>
        public bool Remove(ActorId id)
        {
            if (!_byId.TryGetValue(id, out var agent)) return false;
            _claims.Release(agent.Station, agent.Id);
            _byId.Remove(id);
            _agents.Remove(agent);
            _changed.Remove(agent);
            _engaged.Remove(agent);
            _sermons.Remove(id);
            if (_cursor >= _agents.Count) _cursor = 0;
            return true;
        }

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its agent goes.</summary>
        void IActorStore.Forget(ActorId actor) => Remove(actor);

        bool IActorStore.Holds(ActorId actor) => _byId.ContainsKey(actor);

        /// <summary>
        /// The hero's party and guide (set by the session; null = nobody escorts). Escorting NPCs leave their routine
        /// station; off screen they keep up with the hero (<see cref="AbstractEscort"/>), on screen their body moves them.
        /// </summary>
        public IEscorts Escorts { get; set; }

        /// <summary>The escort mode of an NPC now (None without a party).</summary>
        public EscortMode EscortOf(ActorId id) => Escorts?.ModeOf(id) ?? EscortMode.None;

        /// <summary>
        /// Whether a world sector is switched on (<see cref="WorldSectors"/>; null = all are). An NPC of a switched-off
        /// sector (<see cref="NpcDef.Sector"/>) is not in the world: it is not simulated and not shown.
        /// </summary>
        public Func<string, bool> SectorEnabled { get; set; }

        /// <summary>The NPC is in the world (its sector, if any, is switched on).</summary>
        public bool IsInWorld(NpcAgent agent) => agent == null || agent.Definition.Sector == null || SectorEnabled == null || SectorEnabled(agent.Definition.Sector);

        /// <summary>
        /// New game: every NPC starts at the station of the current phase, as G3's "Start" routine does (NPCs out of view
        /// are put there at once). NPCs without a free station keep their spawn pose. An NPC of a switched-off sector is not
        /// in the world and takes no station (fix-67 R33-3: the Faring rebel smith took Zakosh's anvil); it picks one when
        /// its sector comes on.
        /// </summary>
        public void PlaceAtRoutineStart()
        {
            double now = _clock.GameSeconds;
            foreach (var agent in _agents)
            {
                if (!IsInWorld(agent)) continue;
                agent.Phase = PhaseOf(agent);
                var station = _planner.Select(agent.Id, agent.Routine, agent.Phase, Station.None, false);
                if (station.IsNone || !Points.TryGetPose(station, out var pose)) continue;
                _claims.Claim(station, agent.Id);
                agent.Station = station;
                agent.Pose = pose;
                agent.Activity = NpcActivity.AtStation;
                agent.ArrivedAt = now;
            }
        }

        public void Tick()
        {
            double now = _clock.GameSeconds;
            _engagedScratch.Clear();
            _engagedScratch.AddRange(_engaged);
            foreach (var agent in _engagedScratch)
                if (IsInWorld(agent)) Process(agent, now);

            int budget = Math.Min(Settings.AbstractBudget, _agents.Count);
            for (int scanned = 0; budget > 0 && scanned < _agents.Count; scanned++)
            {
                _cursor = (_cursor + 1) % _agents.Count;
                var agent = _agents[_cursor];
                if (agent.Detail != DetailLevel.Abstract || !IsInWorld(agent)) continue;
                Process(agent, now);
                budget--;
            }
        }

        /// <summary>Where the NPC is now (abstract travel is interpolated).</summary>
        public WorldPose PoseOf(NpcAgent agent)
        {
            if (agent.Activity != NpcActivity.Travelling || agent.Detail == DetailLevel.Active || !TryGetTarget(agent, out var target))
                return agent.Pose;
            double span = agent.TravelEnd - agent.TravelStart;
            float t = span <= 0 ? 1 : (float)((_clock.GameSeconds - agent.TravelStart) / span);
            return WorldPose.Along(agent.TravelFrom, target, t);
        }

        /// <summary>Pose of the agent's station slot (false without a station).</summary>
        public bool TryGetTarget(NpcAgent agent, out WorldPose pose)
        {
            if (agent.Station.IsNone && agent.WanderTarget is WorldPose wander)
            {
                pose = wander;
                return true;
            }
            return Points.TryGetPose(agent.Station, out pose);
        }

        /// <summary>
        /// The NPC sleeps: it is at a bed or sleeps on the ground (G3 SelectRoutineTask picks ZS_Sleep by the use type of the
        /// routine point, ZS_Ambient_Sleep for a wanderer), whatever the day phase. An NPC whose sleep-phase station is a
        /// campfire, a stool or a trader's spot is awake (fix-7: Kent at his campfire, Abe at night never talked first).
        /// </summary>
        public bool Sleeps(NpcAgent agent) => IsSleepInteraction(InteractionOf(agent));

        /// <summary>The interaction is sleeping ("Bed", "SleepGround").</summary>
        public static bool IsSleepInteraction(string interaction) =>
            interaction == "Bed" || interaction == AmbientRules.SleepGround;

        /// <summary>
        /// The interaction of the station the NPC is at ("Anvil", "Bed" …), or a wanderer's ambient action while it stands
        /// ("EatGround" …); null otherwise.
        /// </summary>
        public string InteractionOf(NpcAgent agent) => InteractionOf(agent, null, partnerKnown: false);

        /// <summary>
        /// <see cref="InteractionOf(NpcAgent)"/> with the smalltalk partner the caller just asked for
        /// (<see cref="SmalltalkPartnerOf"/>, nothing changed since): the view at its station needs both, and the partner search
        /// walks every engaged NPC (perf-3). Same result.
        /// </summary>
        public string InteractionOf(NpcAgent agent, NpcAgent smalltalkPartner) => InteractionOf(agent, smalltalkPartner, partnerKnown: true);

        string InteractionOf(NpcAgent agent, NpcAgent knownPartner, bool partnerKnown)
        {
            if (agent.Activity == NpcActivity.Idle) return agent.AmbientAction;
            if (agent.Activity != NpcActivity.AtStation || !Points.TryGetPoint(agent.Station.Point, out var p)) return null;
            if (!Smalltalk.IsSmalltalk(p.Interaction)) return p.Interaction;
            var partner = partnerKnown ? knownPartner : SmalltalkPartnerOf(agent);
            if (partner == null) return Smalltalk.Alone;
            double since = Math.Max(agent.ArrivedAt, partner.ArrivedAt);
            double realSeconds = (_clock.GameSeconds - since) / Math.Max(1f, _clock.TimeScale);
            return Smalltalk.RoleOf(Smalltalk.IsLeader(agent.ArrivedAt, agent.Id.Name, partner.ArrivedAt, partner.Id.Name), realSeconds);
        }

        /// <summary>
        /// ZS_Smalltalk: the NPC at the nearest other smalltalk spot (within <see cref="Smalltalk.PairDistance"/>) who is there
        /// too; null when the NPC talks alone or is not at a smalltalk spot. Only NPCs with a view are paired.
        /// </summary>
        public NpcAgent SmalltalkPartnerOf(NpcAgent agent)
        {
            if (agent.Activity != NpcActivity.AtStation || agent.Detail == DetailLevel.Abstract ||
                !Points.TryGetPoint(agent.Station.Point, out var p) || !Smalltalk.IsSmalltalk(p.Interaction))
                return null;
            NpcAgent best = null;
            float bestDistance = Smalltalk.PairDistance;
            foreach (var other in _engaged)
            {
                if (other == agent || other.Activity != NpcActivity.AtStation) continue;
                // Distance first: the view calls this every frame per smalltalk NPC over all engaged NPCs, and the
                // point lookup + interaction test are the expensive part (perf-7). Same result.
                float d = agent.Pose.HorizontalDistanceTo(other.Pose);
                if (d >= bestDistance ||
                    !Points.TryGetPoint(other.Station.Point, out var q) || !Smalltalk.IsSmalltalk(q.Interaction))
                    continue;
                best = other;
                bestDistance = d;
            }
            return best;
        }

        /// <summary>
        /// G3 GetTravellingWalkMode: the NPC runs (not walks) to its routine point; wander steps are walked. ZS_Arena's
        /// goto runs whatever the species (Script_Game 0x10011aec: _AI_Goto_Ext walk mode 2 = Run; fix-84).
        /// </summary>
        public bool Runs(NpcAgent agent) =>
            agent.WanderTarget == null ? AmbientRules.RunsWhenTravelling(agent.Definition.Species) : GoesToArena(agent);

        bool GoesToArena(NpcAgent agent) =>
            agent.WanderTarget is WorldPose to && ArenaPointOf?.Invoke(agent.Id) is WorldPose arena && to.DistanceTo(arena) < ArenaReach;

        /// <summary>The side the station's interaction is done from ("Fwd", "Left" …).</summary>
        public string DirectionOf(NpcAgent agent) =>
            Points.TryGetPoint(agent.Station.Point, out var p) ? p.DirectionOf(agent.Station.Slot) : "Fwd";

        public void SetDetail(ActorId id, DetailLevel detail)
        {
            if (!_byId.TryGetValue(id, out var agent) || agent.Detail == detail) return;
            double now = _clock.GameSeconds;
            if (agent.Activity == NpcActivity.Travelling)
            {
                if (detail == DetailLevel.Active) agent.Pose = PoseOf(agent);       // the body continues from here
                else if (agent.Detail == DetailLevel.Active) StartTravel(agent, now); // the rest of the way is abstract
            }
            bool entering = agent.Detail == DetailLevel.Abstract;
            agent.Detail = detail;
            if (detail == DetailLevel.Abstract) _engaged.Remove(agent);
            else _engaged.Add(agent);
            if (entering) EnteredRange?.Invoke(agent);
        }

        /// <summary>
        /// The NPC came out of the abstract simulation (G3 OnEnterProcessingRange: hit points refreshed, a corpse judged by
        /// Respawn). Raised inside <see cref="SetDetail"/>: handlers must not add or remove agents there, only note them.
        /// </summary>
        public event Action<NpcAgent> EnteredRange;

        /// <summary>The active body moved the NPC (ignored for NPCs that are not Active).</summary>
        public void ReportPose(ActorId id, WorldPose pose)
        {
            if (_byId.TryGetValue(id, out var agent) && agent.Detail == DetailLevel.Active) agent.Pose = pose;
        }

        /// <summary>
        /// fix-133: the world put the body elsewhere (a cave rescue out of the rock, a spawn moved out of a collider): the
        /// simulation takes that pose whatever the detail level. <see cref="ReportPose"/> is ignored for Near NPCs, whose
        /// view only follows <see cref="PoseOf"/>, so a Near body rescued every 2 s was put back into the rock the next
        /// frame (Goblin#97dd86cd, Reddock cave Dungeon_V07_S04_05). A wander travel ends at the new pose (its point lay in
        /// the rock); a travel to a station starts again from it. False for an unknown NPC.
        /// </summary>
        public bool CorrectPose(ActorId id, WorldPose pose)
        {
            if (!_byId.TryGetValue(id, out var agent)) return false;
            agent.Pose = pose;
            if (agent.Activity != NpcActivity.Travelling) return true;
            if (agent.Station.IsNone)
            {
                if (agent.WanderTarget != null) agent.WanderTarget = pose;
                agent.TravelFrom = pose;
            }
            else StartTravel(agent, _clock.GameSeconds);
            return true;
        }

        /// <summary>
        /// fix-133: the view moved the current wander point (spawn + offset at the spawn's height) onto a floor the body may
        /// stand on (a true cave floor, or where it stands): the travel goes there instead, timed from where the NPC is.
        /// False when the NPC is not on a wander travel.
        /// </summary>
        public bool CorrectWanderTarget(ActorId id, WorldPose target)
        {
            if (!_byId.TryGetValue(id, out var agent) || agent.Activity != NpcActivity.Travelling || !agent.Station.IsNone ||
                agent.WanderTarget == null)
                return false;
            if (agent.Detail != DetailLevel.Active) agent.Pose = PoseOf(agent);
            agent.WanderTarget = target;
            StartTravel(agent, _clock.GameSeconds);
            return true;
        }

        /// <summary>The active body reached the station.</summary>
        public void ReportArrived(ActorId id)
        {
            if (_byId.TryGetValue(id, out var agent) && agent.Activity == NpcActivity.Travelling) Arrive(agent, _clock.GameSeconds);
        }

        /// <summary>A conversation with the NPC started or ended: it stops where it is and resumes afterwards.</summary>
        public void SetTalking(ActorId id, bool talking)
        {
            if (!_byId.TryGetValue(id, out var agent)) return;
            if (talking && agent.Activity != NpcActivity.Talking)
            {
                agent.Pose = PoseOf(agent);
                agent.ResumeActivity = agent.Activity;
                agent.Activity = NpcActivity.Talking;
            }
            else if (!talking && agent.Activity == NpcActivity.Talking)
            {
                agent.Activity = agent.ResumeActivity;
                if (agent.Activity == NpcActivity.Travelling) StartTravel(agent, _clock.GameSeconds);
            }
        }

        /// <summary>
        /// The story switches the NPC to another routine (G3 SetRoutine). "Start" (G3's first routine name) is its own
        /// routine. A name the NPC has no points for leaves it without points, as G3's routine change does (the points of
        /// an unknown name are cleared, CP 1.75 gCNavigation_PS property handler): it stays and idles where it stands.
        /// The NPC heads for the new station at once, or after a conversation it is in. False for an unknown NPC.
        /// </summary>
        /// <summary>G3's name of the routine an NPC starts with.</summary>
        public const string StartRoutine = "Start";

        /// <summary>
        /// G3 FullStop: the NPC drops what it is doing right now (a walk is cut where it stands) and goes on with its
        /// routine from there. False for an unknown NPC. A talking NPC is left alone (the conversation holds it).
        /// </summary>
        public bool FullStop(ActorId id)
        {
            if (!_byId.TryGetValue(id, out var agent)) return false;
            if (agent.Activity == NpcActivity.Talking || agent.Activity == NpcActivity.Escorting) return true;
            ClearTasks(agent);
            if (agent.Activity == NpcActivity.Travelling)
            {
                var here = PoseOf(agent);
                _claims.Release(agent.Station, agent.Id);
                agent.Station = Station.None;
                agent.Pose = here;
                agent.WanderTarget = null;
                agent.Activity = NpcActivity.Idle;
                agent.RetryAt = 0;
            }
            return true;
        }

        public bool SetRoutine(ActorId id, string routine)
        {
            if (!_byId.TryGetValue(id, out var agent)) return false;
            agent.RoutineName = string.IsNullOrEmpty(routine) || string.Equals(routine, StartRoutine, StringComparison.OrdinalIgnoreCase) &&
                                agent.Definition.RoutineNamed(routine) == null
                ? null : routine;
            agent.RetryAt = 0;
            if (agent.Activity == NpcActivity.Talking) agent.ResumeActivity = agent.ResumeActivity == NpcActivity.Escorting ? NpcActivity.Escorting : NpcActivity.Idle; // replans when the talk ends
            else if (agent.Activity != NpcActivity.Escorting) Replan(agent, PhaseOf(agent), _clock.GameSeconds, moveOn: false); // an escort takes the routine up when it leaves the party
            return true;
        }

        /// <summary>
        /// An NPC out of view that is on its way to a station is put there at once (G3 Entity::MoveTo to the routine's
        /// working point, e.g. the companions after Ardea's liberation). False when it is on screen or not travelling.
        /// </summary>
        public bool ArriveNow(ActorId id)
        {
            if (!_byId.TryGetValue(id, out var agent) || agent.Detail == DetailLevel.Active || agent.Activity != NpcActivity.Travelling) return false;
            Arrive(agent, _clock.GameSeconds);
            return true;
        }

        DayPhase PhaseOf(NpcAgent agent) => _schedule.PhaseOf(agent.Definition.Species, _clock.HourOfDay);

        /// <summary>
        /// NPCs held where they are (dev tools: scenario 'freeze'): no planning, travel or wandering while it says true; a
        /// walk in progress is cut with <see cref="FullStop"/> by the caller. Null = nobody.
        /// </summary>
        public Func<ActorId, bool> Held { get; set; }

        /// <summary>
        /// Dead NPCs (set by the session): no routine any more (G3 AIMode Dead) — a corpse out of view must not walk to its
        /// next station and turn up there when it is shown again or a game is loaded (fix-80). Null = nobody.
        /// </summary>
        public Func<ActorId, bool> Lifeless { get; set; }

        /// <summary>
        /// The arena point of an NPC whose arena fight is running (null = none; set by the session). G3: an Arena/Sparring
        /// quest that starts sends its opponents there (gCInfo_PS::OnEndInfo → SendToArena: the quest's destination entity
        /// becomes the current destination point), and while the arena runs the routine keeps it (SelectRoutineDestination
        /// step 1) and the task is ZS_Arena: goto that point, refill, face the player, then wait for him in the ring.
        /// </summary>
        public Func<ActorId, WorldPose?> ArenaPointOf { get; set; }

        /// <summary>An NPC this close (m) to its arena point stands in the arena.</summary>
        public const float ArenaReach = 1.5f;

        /// <summary>
        /// The NPC waits at its arena point (G3: ZS_Arena reached the point and set its OnArena callback, so the fight can
        /// start); false while it walks there; null when it has no running arena fight (or is unknown).
        /// </summary>
        public bool? InArena(ActorId id)
        {
            if (!(ArenaPointOf?.Invoke(id) is WorldPose point) || !_byId.TryGetValue(id, out var agent)) return null;
            return agent.Activity != NpcActivity.Travelling && agent.Station.IsNone && PoseOf(agent).DistanceTo(point) < ArenaReach;
        }

        void Process(NpcAgent agent, double now)
        {
            if (agent.Activity == NpcActivity.Talking) return;
            if (Held != null && Held(agent.Id)) return;
            if (Lifeless != null && Lifeless(agent.Id)) return;
            var escort = EscortOf(agent.Id);
            if (escort != EscortMode.None)
            {
                Escort(agent, escort, now);
                return;
            }
            if (StepParty(agent, now)) return;
            if (agent.Activity == NpcActivity.Escorting)
            {
                // Left the party or stopped guiding: back to the routine (G3 ContinueRoutine) from where it stands.
                agent.Activity = NpcActivity.Idle;
                agent.RetryAt = 0;
            }
            if (ArenaPointOf?.Invoke(agent.Id) is WorldPose arena)
            {
                GoToArena(agent, arena, now);
                return;
            }
            if (agent.Activity == NpcActivity.Travelling && agent.Detail != DetailLevel.Active && now >= agent.TravelEnd)
                Arrive(agent, now);
            if (agent.Routine == null)
            {
                Wander(agent, now);
                return;
            }
            if (now < agent.RetryAt) return;

            var phase = PhaseOf(agent);
            bool phaseChanged = phase != agent.Phase || agent.Activity == NpcActivity.Idle;
            if (!phaseChanged && StepAtStation(agent, now)) return;
            bool stayedLong = agent.Activity == NpcActivity.AtStation && now - agent.ArrivedAt >= _schedule.StationStaySeconds &&
                              HasOtherStations(agent.Station);
            if (phaseChanged || stayedLong) Replan(agent, phase, now, moveOn: !phaseChanged);
        }

        void Replan(NpcAgent agent, DayPhase phase, double now, bool moveOn)
        {
            ClearTasks(agent);
            agent.Phase = phase;
            var next = _planner.Select(agent.Id, agent.Routine, phase, agent.Station, moveOn);
            if (next.Equals(agent.Station) && !next.IsNone)
            {
                if (moveOn) agent.ArrivedAt = now; // nothing else free: stay another period
                return;
            }
            var here = PoseOf(agent);
            _claims.Release(agent.Station, agent.Id);
            agent.Pose = here;
            if (next.IsNone)
            {
                agent.Station = Station.None;
                agent.Activity = NpcActivity.Idle;
                agent.RetryAt = now + Settings.RetryRealSeconds * Math.Max(1f, _clock.TimeScale);
                return;
            }
            _claims.Claim(next, agent.Id);
            agent.Station = next;
            agent.WanderTarget = null;
            agent.AmbientAction = null;
            StartTravel(agent, now);
        }

        /// <summary>
        /// ZS_Arena's goto: the NPC leaves its station for the arena point and waits there (no ambient action, no routine)
        /// until the arena stops running; then the routine plans again from where it stands (Activity Idle).
        /// </summary>
        void GoToArena(NpcAgent agent, WorldPose point, double now)
        {
            if (agent.Activity == NpcActivity.Travelling && agent.Station.IsNone && agent.WanderTarget is WorldPose to &&
                to.DistanceTo(point) < ArenaReach)
            {
                if (agent.Detail != DetailLevel.Active && now >= agent.TravelEnd) Arrive(agent, now);
                return; // on its way (an active body reports its arrival)
            }
            if (agent.Activity != NpcActivity.Travelling && agent.Station.IsNone && PoseOf(agent).DistanceTo(point) < ArenaReach)
            {
                agent.AmbientAction = null;
                return; // in the ring: waits for the player
            }
            var here = PoseOf(agent);
            _claims.Release(agent.Station, agent.Id);
            agent.Pose = here;
            agent.Station = Station.None;
            agent.AmbientAction = null;
            agent.WanderTarget = point;
            StartTravel(agent, now);
        }

        /// <summary>Abstract travel from the current pose to the station; the duration follows the clock's time scale.</summary>
        void StartTravel(NpcAgent agent, double now)
        {
            agent.Activity = NpcActivity.Travelling;
            agent.TravelFrom = agent.Pose;
            agent.TravelStart = now;
            double realSeconds = TryGetTarget(agent, out var target)
                ? agent.Pose.DistanceTo(target) * Settings.PathFactor / Math.Max(0.1f, TravelSpeedOf(agent))
                : 0;
            agent.TravelEnd = now + realSeconds * Math.Max(1f, _clock.TimeScale);
        }

        float TravelSpeedOf(NpcAgent agent) => Runs(agent) ? Settings.RunTravelSpeed : Settings.TravelSpeed;

        void Arrive(NpcAgent agent, double now)
        {
            if (agent.Station.IsNone)
            {
                // A wander point (or a travel whose target was lost, e.g. after loading): stand and do the ambient action.
                if (agent.WanderTarget is WorldPose point) agent.Pose = point;
                agent.WanderTarget = null;
                agent.Activity = NpcActivity.Idle;
                if (!AmbientRules.Wanders(agent.Definition, agent.Routine))
                {
                    agent.AmbientAction = null;
                    agent.RetryAt = 0; // plan again at once
                    return;
                }
                agent.AmbientAction = AmbientRules.ActionOf(agent.Definition.Species, _random);
                agent.RetryAt = now + AmbientRules.ActionSeconds(_random) * Math.Max(1f, _clock.TimeScale);
                return;
            }
            if (TryGetTarget(agent, out var target)) agent.Pose = target;
            agent.Activity = NpcActivity.AtStation;
            agent.ArrivedAt = now;
            agent.SmalltalkTurn = -1;
        }

        /// <summary>
        /// ZS_Ambient for a creature without routine points (<see cref="AmbientRules"/>): walk to a random point around the
        /// start position, play one ambient action, walk on; sleep in the sleep phase. Only NPCs with a view wander; off
        /// screen they stay where they are (nobody sees them, and thousands of animals cost nothing that way).
        /// </summary>
        void Wander(NpcAgent agent, double now)
        {
            if (agent.Activity == NpcActivity.Travelling && agent.Station.IsNone && agent.WanderTarget == null) Arrive(agent, now);
            if (agent.Activity != NpcActivity.Idle || now < agent.RetryAt) return;
            if (agent.Detail == DetailLevel.Abstract || !AmbientRules.Wanders(agent.Definition, agent.Routine)) return;
            float scale = Math.Max(1f, _clock.TimeScale);
            if (PhaseOf(agent) == DayPhase.Sleep)
            {
                agent.AmbientAction = AmbientRules.SleepGround;
                agent.RetryAt = now + AmbientRules.SleepCheckSeconds;
                return;
            }
            string species = agent.Definition.Species;
            if (AmbientRules.StaysPut(species))
            {
                agent.AmbientAction = AmbientRules.ActionOf(species, _random);
                agent.RetryAt = now + AmbientRules.ActionSeconds(_random) * scale;
                return;
            }
            var home = agent.Definition.Spawn ?? agent.Pose;
            float radius = AmbientRules.WanderRadius(agent.Definition.Combat?.AmbientCreature ?? false, _random);
            agent.AmbientAction = null;
            agent.WanderTarget = AmbientRules.WanderPoint(home, agent.Pose, radius, _random);
            StartTravel(agent, now);
        }

        /// <summary>
        /// An escort leaves its routine station; off screen it keeps up with the hero (followers) or stays (waiting members,
        /// guides wait for the hero who is far away anyway) <b>[interp]</b>. Active escorts are moved by their body.
        /// </summary>
        void Escort(NpcAgent agent, EscortMode mode, double now)
        {
            if (agent.Activity != NpcActivity.Escorting)
            {
                agent.Pose = PoseOf(agent);
                _claims.Release(agent.Station, agent.Id);
                agent.Station = Station.None;
                agent.Activity = NpcActivity.Escorting;
                agent.EscortedAt = now;
            }
            float realSeconds = (float)((now - agent.EscortedAt) / Math.Max(1f, _clock.TimeScale));
            agent.EscortedAt = now;
            if (agent.Detail == DetailLevel.Active || mode != EscortMode.Follow) return;
            if (Escorts.TryGetLeader(out var leader))
                agent.Pose = AbstractEscort.Follow(agent.Pose, leader, Escorts.FollowDistance(agent.Id), agent.Detail, realSeconds);
        }

        bool HasOtherStations(Station station) =>
            Points.TryGetPoint(station.Point, out var p) && !p.Anchor.IsEmpty && Points.Members(p.Anchor).Count > 1;

        /// <summary>Re-derives the station claims from the agents (after loading a save).</summary>
        internal void RebuildClaims()
        {
            _claims.Clear();
            _engaged.Clear();
            foreach (var a in _agents)
            {
                _claims.Claim(a.Station, a.Id);
                a.Detail = DetailLevel.Abstract;
            }
        }
    }
}
