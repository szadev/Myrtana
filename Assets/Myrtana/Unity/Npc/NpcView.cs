using System;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The on-screen body of one simulated NPC. It only shows the <see cref="NpcAgent"/>'s state, except when the NPC is
    /// <see cref="DetailLevel.Active"/>: then it walks the NPC to its station (<see cref="NpcWalker"/>) and reports the
    /// pose and the arrival back to the <see cref="NpcSimulation"/>. At the station it plays the station's interaction
    /// (<see cref="G3NpcAnimator.Interaction"/>). A view can be dropped at any time; a new one continues from the state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NpcView : MonoBehaviour
    {
        const float StationTurnSpeed = 240f;
        const float WalkBackArrive = 0.3f, WalkBackMax = 60f; // m: after a fight, walk back to the station within this range

        readonly NpcWalker _walker = new NpcWalker();
        readonly GroundProbe _ground = new GroundProbe();
        G3NpcAnimator _animator;
        NpcSimulation _simulation;
        Station _walkingTo;
        double _walkingSince; // the travel's start: a wanderer's next point has the same (empty) station

        /// <summary>The hero, whom party members follow and guides lead (set by <see cref="NpcViewManager"/>).</summary>
        public Transform Leader { get; set; }

        readonly FollowPlanner _follow = new FollowPlanner();
        readonly GuidePlanner _guide = new GuidePlanner();
        Vector3 _escortTarget;
        string _guideState; // last logged guide state (walks / waits / arrived)

        /// <summary>A guide that stands waiting for the hero (not walking, not at its destination); dev scenarios read it.</summary>
        public bool GuideWaits => _guideState == "waits";

        /// <summary>
        /// The way this guide took on its G3 route (Docs/navigation.md): the route's points from its start up to the leg it
        /// walks now, then its own position; false without a G3 route. Dev scenarios walk the hero along it (fix-34).
        /// </summary>
        public bool TryGuideTrail(System.Collections.Generic.List<Vector3> into)
        {
            into.Clear();
            if (!_g3Guide.HasRoute) return false;
            var pts = _g3Guide.Route.Points;
            int upTo = Math.Min(_g3Guide.Next, pts.Count);
            for (int i = 0; i < upTo; i++) into.Add(new Vector3(pts[i].X, pts[i].Y, pts[i].Z));
            into.Add(transform.position);
            return into.Count >= 2;
        }
        int _guideStuck;    // consecutive stuck periods (4 s each) of a guide that walks
        readonly GuideRoute _route = new GuideRoute(); // a guide's way to a far destination, in segments (fix-22)
        // G3 navigation graph routes (Docs/navigation.md): a guide's way, a follower's way back to the hero, a far routine walk
        readonly NavRouteFollower _g3Guide = new NavRouteFollower(), _g3Follow = new NavRouteFollower(), _g3Walk = new NavRouteFollower();
        bool _walkOnG3;
        float _buildWait;   // seconds a stuck guide waited for the NavMesh build around it
        bool _waitsForBuild;
        Vector3 _loggedLeg;
        float _repathTimer;

        public string Visual { get; private set; }

        Transform _transform;
        /// <summary>The view's transform, cached (per-frame readers of every view: perception, focus; perf-3).</summary>
        public Transform Body => _transform ??= transform;
        public NpcAgent Agent { get; private set; }
        public ActorId Actor => Agent?.Id ?? default;
        /// <summary>Combat has the body (fighting, down or dead): the routine neither moves nor animates it.</summary>
        public bool BodyTaken
        {
            get => _bodyTaken;
            set
            {
                if (_bodyTaken && !value)
                {
                    _walker.Clear(); // the body moved: plan the way anew
                    _returning = true;
                }
                _bodyTaken = value;
            }
        }

        bool _bodyTaken;
        bool _returning; // combat moved the body away from its station: walk back instead of snapping
        Transform _watched;
        float _watchUntil;

        /// <summary>Set by the conversation staging: the NPC talks from its seat (G3 ZS_Talk, Docs/dialog-presentation.md §2), or a woken sleeper still lies while it says "YouDisturbedMySlumber" (fix-247).</summary>
        public bool StaySeatedInTalk { get; set; }

        /// <summary>The NPC sits in its interaction now (stool, bench, throne, ground).</summary>
        public bool IsSeated => _animator != null && _animator.IsSeated;

        public void Init(string visual, G3NpcAnimator animator)
        {
            Visual = visual;
            _animator = animator;
            _bodies = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        }

        // ------------------------------------------------------------------------------------- update LOD (perf-8, perf-3)

        SkinnedMeshRenderer[] _bodies = System.Array.Empty<SkinnedMeshRenderer>();
        float _dt;
        StepClock _clock;
        static int s_cameraFrame = -1, s_nextPhase, s_frameTimeFrame = -1;
        readonly int _phase = s_nextPhase++ % NpcUpdateLod.MaxInterval;
        static Vector3 s_camera;
        static bool s_hasCamera;
        static float s_frameSeconds = 1f / 60f;

        /// <summary>
        /// Every NPC view steps from one batched Update (perf-3, <see cref="G3FrameBatch"/>; execution order 0 as before):
        /// a view that is not due costs a few field reads instead of a Unity callback.
        /// </summary>
        static readonly G3FrameBatch.Batch<NpcView> s_batch =
            new G3FrameBatch.Batch<NpcView>(G3FrameBatch.Slot.Update0, "NpcView", v => v.FrameUpdate());

        static readonly global::Unity.Profiling.ProfilerMarker s_tickMarker = new global::Unity.Profiling.ProfilerMarker("NpcView.Step");

        void OnEnable()
        {
            s_batch.Add(this);
            Snapshot.Add(Body, _snapshotSlot);
        }

        void OnDisable()
        {
            s_batch.Remove(this);
            Snapshot.Remove(_snapshotSlot);
        }

        // ------------------------------------------------------------------- mt-1: view snapshot (Docs/multithreading-plan.md C, D)

        /// <summary>
        /// The shown views' body transforms, read in one Burst job per reader point (mt-1): the NpcView batch start for the
        /// update-LOD distance and tier candidates (-g3mt bit 2), <see cref="PerceptionDriver"/> and the focus' nearby list
        /// (bit 1). Readers fall back to the transform when a view is no member yet.
        /// </summary>
        public static readonly G3TransformSnapshot Snapshot = new G3TransformSnapshot("NpcViews");

        readonly G3TransformSnapshot.Slot _snapshotSlot = new G3TransformSnapshot.Slot();

        /// <summary>The view's body position from <see cref="Snapshot"/> when it was read this frame, else from the transform.</summary>
        public Vector3 SnapshotPosition => Snapshot.HasThisFrame(_snapshotSlot) ? Snapshot.Position(_snapshotSlot) : Body.position;

        /// <summary>Position and rotation from the snapshot read this frame with rotations (false: read the transform).</summary>
        public bool TrySnapshotPose(out Vector3 position, out Quaternion rotation)
        {
            if (Snapshot.HasThisFrame(_snapshotSlot) && Snapshot.HasRotations)
            {
                position = Snapshot.Position(_snapshotSlot);
                rotation = Snapshot.Rotation(_snapshotSlot);
                return true;
            }
            position = default;
            rotation = default;
            return false;
        }

        static bool s_tierSnapshot; // the batch run of this frame read the tier candidates

        static NpcView()
        {
            s_batch.BeforeRun = BeforeBatch;
            s_batch.AfterRun = () => s_tierSnapshot = false;
        }

        /// <summary>D: one job reads every view's position for the tier choices of this run (a view reads only its own body there).</summary>
        static void BeforeBatch()
        {
            s_tierSnapshot = false;
            if (!G3Mt.On(MtBits.TierJob)) return;
            var options = G3RenderTuning.Options;
            if (!(options.NpcLod || options.NpcLodHidden)) return;
            Snapshot.Read(false);
            s_tierSnapshot = true;
        }

        /// <summary>
        /// The view's tier (<see cref="NpcUpdateLod.Interval"/>: 1 = every frame … 4 = out of view); the perception scan rate is
        /// scaled by it (PerceptionScanner). Its animation steps every <see cref="NpcUpdateLod.Frames"/> frames.
        /// </summary>
        public int UpdateInterval { get; private set; } = 1;

        static bool CameraPosition(out Vector3 position)
        {
            if (s_cameraFrame != Time.frameCount)
            {
                s_cameraFrame = Time.frameCount;
                var cam = Camera.main;
                s_hasCamera = cam != null;
                if (s_hasCamera) s_camera = cam.transform.position;
            }
            position = s_camera;
            return s_hasCamera;
        }

        /// <summary>The frame time the step intervals follow: smoothed real frame time (one hitch does not stretch them).</summary>
        static float FrameSeconds()
        {
            if (s_frameTimeFrame != Time.frameCount)
            {
                s_frameTimeFrame = Time.frameCount;
                float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1f / 500f, 0.1f);
                s_frameSeconds += (dt - s_frameSeconds) * 0.1f;
            }
            return s_frameSeconds;
        }

        bool Important => BodyTaken || StaySeatedInTalk || Agent.Activity == NpcActivity.Talking || Agent.Activity == NpcActivity.Escorting;

        bool AnyBodyVisible()
        {
            foreach (var b in _bodies)
                if (b != null && b.isVisible) return true;
            return false;
        }

        bool _hidden; // the last tier choice found no view rendering the body (perf-3: re-checked every 4th frame)

        /// <summary>Picks the view's tier and its animation's step interval (NpcUpdateLod).</summary>
        void ChooseTier()
        {
            int interval = 1, frames = 1;
            _hidden = false;
            var options = G3RenderTuning.Options;
            if ((options.NpcLod || options.NpcLodHidden) && CameraPosition(out var cam))
            {
                bool important = Important;
                // mt-1 D (-g3mt bit 2): the body position from the batch's snapshot job (the same value; the distance and the
                // tier stay managed code: computed in Burst they differed from Mono's in the last bit)
                float distance = 0f;
                if (!important)
                {
                    var at = s_tierSnapshot && Snapshot.HasThisFrame(_snapshotSlot) ? Snapshot.Position(_snapshotSlot) : Body.position;
                    if (s_tierSnapshot && G3Mt.Verifying) G3Mt.Check(G3Mt.Same(at, Body.position), "tier", $"{name} tier position {at:R}/{Body.position:R}");
                    distance = Vector3.Distance(cam, at);
                }
                bool visible = !important && distance >= NpcUpdateLod.NearHiddenDistance && AnyBodyVisible();
                int tier = NpcUpdateLod.Interval(true, important, distance, visible);
                _hidden = !important && distance >= NpcUpdateLod.NearHiddenDistance && !visible;
                if (options.NpcLod)
                {
                    interval = tier;
                    frames = NpcUpdateLod.PresentationFrames(tier, important, distance, visible, FrameSeconds());
                }
                // Very high object detail (NpcLod off): only what nobody sees steps less often; the tier (perception, combat
                // view) stays the full one as before perf-3
                else if (_hidden) frames = NpcUpdateLod.PresentationFrames(tier, false, distance, false, FrameSeconds());
            }
            UpdateInterval = interval;
            if (_animator != null) _animator.UpdateInterval = frames;
        }

        /// <summary>
        /// Whether the view steps this frame (NpcUpdateLod: every frame within 30 m or while fighting / talking / escorting;
        /// ~30 / 15 / 5 Hz farther or out of view). The animation selector and the motion player step on the same frames.
        /// perf-3: the tier is chosen on the view's step frames (was every frame); a view out of view (beyond 10 m) checks every
        /// 4th frame whether it shows again, and one that becomes important (<see cref="BodyTaken"/>, <see cref="StaySeatedInTalk"/>)
        /// steps from the next frame on.
        /// </summary>
        bool StepDue()
        {
            if (_animator == null)
            {
                ChooseTier();
                return NpcUpdateLod.Due(Time.frameCount, UpdateInterval, _phase);
            }
            if (!_animator.UpdateDue)
            {
                bool recheck = _hidden && ((G3FrameBatch.Frame + _phase) & 3) == 0 && AnyBodyVisible();
                if (!recheck && !Important) return false;
            }
            ChooseTier();
            return _animator.UpdateDue;
        }

        public void Bind(NpcAgent agent, NpcSimulation simulation)
        {
            Agent = agent;
            _simulation = simulation;
            name = agent.Id.Name; // dialog and focus find views by actor id
            _walker.Clear();
            _walker.OverWater = TreatsWaterAsSolid(agent.Definition);
            _swimDepth = G3.Player.SwimRules.NpcSwimDepth(SpeciesOf(agent.Definition));
            _walker.SwimDepth = _swimDepth;
            _waterCheck = 0;
            InSwimDepth = false;
            _wasActive = true; // the bind itself takes it out of deep water (below)
            _walkingTo = Station.None;
            _toolsLeft = _toolsRight = null;
            _clock.Reset(Time.timeAsDouble, Time.deltaTime, Time.frameCount, s_batch.RanThisFrame);
            _follow.Reset();
            _guide.Reset();
            _guideState = null;
            _guideStuck = 0;
            _route.Reset();
            _g3Guide.Reset();
            _g3Follow.Reset();
            _g3Walk.Reset();
            _walkOnG3 = false;
            _buildWait = 0;
            _waitsForBuild = false;
            _caveBudget.Reset();
            _caveGuard = 0f;
            _buriedGuard = 0f;
            _spin.Reset();
            _wanderChecked = null;
            var pose = simulation.PoseOf(agent);
            var p = ToUnity(pose);
            // an interpolated travel pose may be in the air (fix-60), a G3 spawn or a wander pose under a slope's surface
            // (fix-108): the column's floor nearest the pose (NpcGround)
            _ground.Reset();
            _slideSpeed = _slid = 0f;
            if (agent.Activity != NpcActivity.AtStation) p.y = NpcGround.Height(p, HalfLength, p.y, GuessReach(agent));
            else p.y = StationHeight(p); // fix-163: on the slot's floor, not at its authored height
            if (_walker.OverWater) p.y = NpcWalker.SurfaceOrGround(p);
            else if (agent.Activity != NpcActivity.AtStation || !OnFurniture()) p.y += SinkLift(p, "shown"); // fix-163 safety net
            transform.SetPositionAndRotation(p, Quaternion.Euler(0, pose.Yaw, 0));
            // spawned inside a static collider (a rock pile): out to the nearest free NavMesh point (not at a station: a
            // sleeper lies in its bed's collider)
            // (fix-133: CorrectPose, not ReportPose: a Near body follows the simulated pose, which must move with it)
            if (agent.Activity != NpcActivity.AtStation && NpcPlacement.Resolve(transform, "spawned"))
                _simulation.CorrectPose(agent.Id, FromUnity(transform));
            // fix-184 (regress-1021b sideq_ishtar_01: Karmok, carrying stones under Ishtar's palace bridge, was shown on the
            // straight travel line's pose in the channel below it and drowned at once, his quests went Obsolete): a body
            // shown in water deep enough to drown it is out of it on the nearest NavMesh floor (G3 bodies come into the
            // processing range on their walked path, which keeps out of such water)
            if (!_walker.OverWater && OutOfDeepWater("shown"))
                _simulation.CorrectPose(agent.Id, FromUnity(transform));
            if (_animator != null)
            {
                _animator.Direction = _simulation.DirectionOf(agent);
                _animator.Interaction = InteractionNow();
                _animator.Teleported();
            }
        }

        /// <summary>
        /// G3 clears PSCharacterMovement.TreatWaterAsSolid for every body but a bloodfly's (PS_Normal,
        /// OnMovementModeChanged): bloodflies hover over lakes and rivers; everyone else keeps out of deep water.
        /// </summary>
        public static bool TreatsWaterAsSolid(NpcDef def) => G3.Player.SwimRules.TreatsWaterAsSolid(SpeciesOf(def));

        /// <summary>This body treats water as solid (<see cref="TreatsWaterAsSolid(NpcDef)"/>): it moves on the surface.</summary>
        public bool OverWater => _walker.OverWater;

        static string SpeciesOf(NpcDef def) => def?.Combat?.Species ?? def?.Species;

        /// <summary>The NPC's species (combat definition first), null without an agent.</summary>
        public string Species => SpeciesOf(Agent?.Definition);

        float _swimDepth = G3.Player.SwimRules.HumanSwimDepth, _waterCheck;
        bool _wasActive;
        const float WaterCheckSeconds = 0.25f;

        /// <summary>
        /// G3 movement mode WADE for NPCs (CheckChangedMovementStates): body depth ≥ SwimDepth − 50 cm (humans 1.5 m) switches
        /// the motions to the rig's wade set; nothing else changes (no AI state). NPCs never swim (they keep out of deep
        /// water, <see cref="NpcWalker"/>); bloodflies stay on the surface.
        /// </summary>
        void UpdateWading()
        {
            if ((_waterCheck -= _dt) > 0) return;
            _waterCheck = WaterCheckSeconds;
            // fix-184: a Near body taken over by its own walker (Active) starts out of drowning water, as at a bind
            bool active = Agent != null && Agent.Detail == DetailLevel.Active;
            if (active && !_wasActive && !_walker.OverWater && OutOfDeepWater("taken over"))
                _simulation?.CorrectPose(Agent.Id, FromUnity(transform));
            _wasActive = active;
            float body = _walker.OverWater ? float.NaN : G3Water.BodyDepth(transform.position);
            bool wades = G3.Player.SwimRules.NpcWades(body, _swimDepth);
            if (_animator != null) _animator.Wading = wades;
            // Deeper than the wade depth: the column decides whether G3 would switch it to SWIM (it drowns, NpcCombatView).
            // fix-184: only a body that walks on its own (Active) gets there; a Near one stands on its simulated travel line
            InSwimDepth = wades && active && DrownsAt(transform.position, body);
        }

        bool DrownsAt(Vector3 feet, float body) =>
            G3Water.TrySurface(feet, out float surface) &&
            G3.Player.SwimRules.NpcDrowns(body, surface - G3Water.FloorUnder(feet, surface, transform), _swimDepth);

        /// <summary>How far a body shown in deep water looks for a NavMesh floor out of it (m, fix-184).</summary>
        const float DeepWaterRescueMetres = 15f;

        /// <summary>
        /// fix-184: a body put into water where it would drown (its view bound on an interpolated travel pose) goes onto the
        /// nearest NavMesh point within <see cref="DeepWaterRescueMetres"/> that is not such water; false when it is not in
        /// such water or no point was found.
        /// </summary>
        bool OutOfDeepWater(string why)
        {
            var at = transform.position;
            float body = G3Water.BodyDepth(at);
            if (!G3.Player.SwimRules.NpcWades(body, _swimDepth) || !DrownsAt(at, body)) return false;
            if (!UnityEngine.AI.NavMesh.SamplePosition(at, out var hit, DeepWaterRescueMetres, UnityEngine.AI.NavMesh.AllAreas)) return false;
            var to = hit.position;
            float toBody = G3Water.BodyDepth(to);
            if (G3.Player.SwimRules.NpcWades(toBody, _swimDepth) && DrownsAt(to, toBody)) return false;
            transform.position = to;
            Debug.Log($"[npc] fix-184: {name} {why} in deep water at {at} (body {body:0.00} m) -> NavMesh floor {to}");
            return true;
        }

        /// <summary>
        /// The body is in water where G3 would switch it to SWIM (<see cref="G3.Player.SwimRules.NpcDrowns"/>): pathing
        /// keeps NPCs out of such water (<see cref="NpcWalker"/>, <see cref="CellNavMeshes"/>); one forced in (knocked
        /// back, pushed, spawned there) dies as a ragdoll (NpcCombatView). Re-checked every 0.25 s.
        /// </summary>
        public bool InSwimDepth { get; private set; }

        /// <summary>The walker (combat chase reads its water state, fix-142; dev runner probes, fix-93).</summary>
        public NpcWalker Walker => _walker;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
        Vector3? _devWalk;
        float _devSpeed;
        /// <summary>How the last <see cref="DevWalk"/> ended (Walking while it goes on).</summary>
        public NpcWalker.Result DevWalkResult { get; private set; }
        public bool DevWalking => _devWalk.HasValue;

        /// <summary>Dev runner (fix-93 npcpass): walks the body to <paramref name="to"/> on the walker, the routine paused.</summary>
        /// <param name="from">fix-97: put the body here first, in the same frame, and report the pose to the simulation
        /// (r39 fix93_npc_evade: the runner set the transform and yielded a frame without a walk, so the routine
        /// update could put the NPC back on its simulation pose before the walk began).</param>
        public void DevWalk(Vector3 to, float speed, Vector3? from = null, Quaternion? facing = null)
        {
            if (from.HasValue)
            {
                transform.SetPositionAndRotation(from.Value, facing ?? transform.rotation);
                Physics.SyncTransforms();
                if (_animator != null) _animator.Teleported();
                if (Agent != null) _simulation?.ReportPose(Agent.Id, FromUnity(transform));
            }
            _devWalk = to;
            _devSpeed = speed;
            DevWalkResult = NpcWalker.Result.Walking;
            _walker.SetDestination(transform.position, to);
        }

        public void DevWalkStop()
        {
            _devWalk = null;
            _walker.Clear();
        }

        /// <summary>
        /// fix-151 (console <c>enemies fix</c>): a body stuck in geometry goes onto a valid floor with the game's own rescues:
        /// out of a static collider (<see cref="NpcPlacement.TryFreeSpot"/>), off a cave roof / out of the rock
        /// (<see cref="CaveGround.Rescue(Transform, string, bool)"/>), else (off the NavMesh) onto the nearest free NavMesh
        /// point within <paramref name="navReach"/> m. The simulation keeps the new pose (CorrectPose, as the cave guard).
        /// Returns what it did (null: nothing to do).
        /// </summary>
        public string DevUnstick(float navReach = 16f)
        {
            var at = transform.position;
            string how = null;
            if (NpcPlacement.TryFreeSpot(at, transform, out var free, out string blocker))
            {
                transform.position = free;
                how = "out of " + blocker;
            }
            else if (blocker == null)
            {
                var outcome = CaveGround.Rescue(transform, "dev unstick", log: false);
                if (outcome == CaveGround.Outcome.Moved) how = "onto the cave floor";
                else if (outcome == CaveGround.Outcome.NoFloor) return $"no true cave floor within 16 m of {at}";
            }
            if (how == null)
            {
                if (blocker == null && UnityEngine.AI.NavMesh.SamplePosition(at, out _, 0.6f, UnityEngine.AI.NavMesh.AllAreas)) return null; // fine
                if (!UnityEngine.AI.NavMesh.SamplePosition(at, out var hit, navReach, UnityEngine.AI.NavMesh.AllAreas) ||
                    !CaveGround.MayStand(hit.position) || !NpcPlacement.IsFree(hit.position, transform))
                    return blocker != null ? $"inside {blocker}, no free spot within reach" : $"no free NavMesh floor within {navReach:0} m of {at}";
                transform.position = hit.position;
                how = "onto the NavMesh";
            }
            Physics.SyncTransforms();
            _walker.Clear();
            _ground.Reset();
            if (_animator != null) _animator.Teleported();
            if (Agent != null) _simulation?.CorrectPose(Agent.Id, FromUnity(transform));
            _wanderChecked = null;
            var to = transform.position;
            Debug.Log($"[npc] {name} dev unstick: {how}, {at} -> {to} ({Vector3.Distance(at, to):F1} m)");
            return $"moved {Vector3.Distance(at, to):0.0} m {how} to ({to.x:0.0},{to.y:0.0},{to.z:0.0})";
        }

        bool DevWalkStep()
        {
            if (!_devWalk.HasValue) return false;
            SetInteraction(null);
            if (_animator != null && !_animator.CanWalk) return true;
            var result = _walker.Step(transform, _devSpeed, _dt);
            if (result != NpcWalker.Result.Walking)
            {
                DevWalkResult = result;
                _devWalk = null;
                _walker.Clear();
            }
            _simulation?.ReportPose(Agent.Id, FromUnity(transform));
            return true;
        }
#endif

        public void Unbind()
        {
            Agent = null;
            _fight = null;
            BodyTaken = false;
            _returning = false;
            _simulation = null;
            _walker.Clear();
        }

        void FrameUpdate()
        {
            if (Agent == null || GamePause.IsPaused) return; // paused: no routine moves (r31 D1)
            if (!StepDue()) return;
            using var tick = s_tickMarker.Auto();
            _dt = _clock.Take(G3FrameBatch.Now, G3FrameBatch.DeltaTime, G3FrameBatch.Frame);
            UpdateWading();
            CaveGuard();
            BuriedGuard();
            SpinGuard();
            if (BodyTaken) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
            if (DevWalkStep()) return;
#endif
            if (KeepWatchingFight()) return;
            if (_animator != null && Agent.Activity != NpcActivity.Travelling) _animator.CarryTools(null, null);
            switch (Agent.Activity)
            {
                case NpcActivity.Talking:
                    // Stands up where it is and ConversationStaging turns it towards the hero — unless it sits and sees him
                    // (G3 ZS_Talk): then it stays seated and only turns its head.
                    if (!StaySeatedInTalk) SetInteraction(null);
                    break;
                case NpcActivity.Travelling when Agent.Detail == DetailLevel.Active:
                    Walk();
                    break;
                case NpcActivity.AtStation:
                    StayAtStation();
                    break;
                case NpcActivity.Escorting when Agent.Detail == DetailLevel.Active:
                    Escort();
                    break;
                default:
                    FollowSimulation();
                    break;
            }
        }

        /// <summary>Active and travelling: stand up if needed, then walk the path and report back.</summary>
        void Walk()
        {
            SetInteraction(null);
            CarryToolsTo(Agent.Station);
            if (_animator != null && !_animator.CanWalk) return; // still standing up / putting the tool away
            if (!_simulation.TryGetTarget(Agent, out var target)) return;
            bool fresh = !_walkingTo.Equals(Agent.Station) || _walkingSince != Agent.TravelStart;
            // the walk's end once per travel (fix-108: a wander point's floor is a column probe, not one ray per frame)
            if (fresh || !_destFor.Equals(target))
            {
                _dest = Destination(target, out _destSlope, out bool moved);
                // fix-133: the simulation's wander point goes where the walk ends (Arrive puts the pose on it; a Near
                // body interpolates to it after the view leaves Active)
                if (moved && _simulation.CorrectWanderTarget(Agent.Id, new WorldPose(_dest.x, _dest.y, _dest.z, target.Yaw)))
                    _simulation.TryGetTarget(Agent, out target);
                _destFor = target;
            }
            var dest = _dest;
            bool wander = Agent.Station.IsNone;
            if (fresh)
            {
                _walkingTo = Agent.Station;
                _walkingSince = Agent.TravelStart;
                _g3Walk.Reset();
                // a far point: the G3 route (a near one: the NavMesh alone, as before)
                _walkOnG3 = G3NavGraphRuntime.Active != null && Flat(dest - transform.position).magnitude > RoutineRouteMetres;
                // fix-108: a wander point on floor too steep to stand on (G3 SteepGroundAngleMax: slide mode) is not walked
                // to: ZS_Ambient bodies in G3 cannot climb such faces; the creature does its ambient action where it is
                if (wander && G3.Player.NpcGroundRules.Slides(_destSlope))
                {
                    EndWander();
                    return;
                }
            }
            float speed = TravelSpeed();
            var result = _walkOnG3 ? WalkG3(dest, speed) : NpcWalker.Result.Walking;
            if (!_walkOnG3)
            {
                if (fresh || !_walker.HasDestination) _walker.SetDestination(transform.position, dest);
                result = _walker.Step(transform, speed, _dt);
            }
            if (result != NpcWalker.Result.Walking)
            {
                if (wander)
                {
                    // fix-108: a wander point reached is where the body stands (its floor, not the spawn's height the point
                    // carries); one that cannot be reached is given up where the body is, not jumped to (a cliff face)
                    if (result == NpcWalker.Result.Arrived) transform.position = dest;
                    EndWander();
                    return;
                }
                // Arriving puts the agent on the slot pose; reporting the walking pose after it would replace the slot's
                // heading with the arrival direction (sleepers then lay across or beside their beds).
                transform.position = dest;
                _simulation.ReportArrived(Agent.Id);
                return;
            }
            _simulation.ReportPose(Agent.Id, FromUnity(transform));
        }

        Vector3 _dest;
        float _destSlope;
        WorldPose _destFor;

        /// <summary>A wander walk ends where the body is: the simulation keeps that pose (its height is the floor's).</summary>
        void EndWander()
        {
            _walker.Clear();
            _simulation.ReportArrived(Agent.Id);
            _simulation.ReportPose(Agent.Id, FromUnity(transform));
            _ground.Reset();
        }

        /// <summary>
        /// G3 keeps a work task's tools in the hands between the stations of the task (ZS_Smith: sword and hammer from the
        /// forge to the anvil and back): when the next station needs the same tools as the one just left, the NPC carries
        /// them; otherwise it puts them away before walking (<see cref="G3NpcAnimator.CarryTools"/>).
        /// </summary>
        void CarryToolsTo(Station next)
        {
            if (_animator == null) return;
            ToolsOf(next, out string left, out string right);
            bool same = (left != null || right != null) && left == _toolsLeft && right == _toolsRight;
            _animator.CarryTools(same ? left : null, same ? right : null);
        }

        void ToolsOf(Station station, out string left, out string right)
        {
            left = right = null;
            if (_simulation == null || !_simulation.Points.TryGetPoint(station.Point, out var point) ||
                !G3InteractionMotions.TryGet(point.Interaction, out var pose))
                return;
            left = pose.LeftProp;
            right = pose.RightProp;
        }

        string _toolsLeft, _toolsRight; // the tools of the station the NPC is at (or just left)

        /// <summary>
        /// Where the walk ends: the station slot as placed, or a wander point (no station) put on the ground, since its height
        /// is the start position's (<see cref="AmbientRules.WanderPoint"/>).
        /// </summary>
        /// <param name="caveMoved">fix-133: the point lay in the rock / on a cave roof and was moved (to a true floor, or to
        /// where the body stands).</param>
        Vector3 Destination(WorldPose target, out float slopeDeg, out bool caveMoved)
        {
            slopeDeg = 0f;
            caveMoved = false;
            var p = ToUnity(target);
            if (!Agent.Station.IsNone) return p;
            // fix-108: the column's surface nearest the point's height up to WanderReach above it (a wander point 3-5 m from
            // the spawn carries the spawn's height: on a mountain slope its floor lies meters above or below), else the
            // floor under it however far below (fix-60: a wander point beside a ledge kept the ledge's height)
            if (NpcGround.TryFloor(p, HalfLength, out var floor, NpcWalker.FloorReach, WanderReach))
            {
                p.y = floor.Y;
                slopeDeg = floor.SlopeDeg;
            }
            else if (NpcWalker.TryFloorHeight(p, out float y, 5f)) p.y = y;
            // fix-128: a wander point in the rock behind a cave wall or on a roof top (spawn + offset at the spawn's height):
            // the nearest true cave floor to it, else no wander (stay)
            if (!CaveGround.MayStand(p))
            {
                if (CaveGround.TrySnap(p, transform, out var q)) p = q;
                else p = transform.position;
                caveMoved = true;
            }
            return p;
        }

        WorldPose? _wanderChecked;

        /// <summary>
        /// fix-133: a Near wanderer's body follows the abstract travel (<see cref="FollowSimulation"/>) straight to the wander
        /// point, which carries the spawn's height and may lie in the rock behind a cave wall (Reddock cave V07_S04_05:
        /// goblins, meatbugs, a snake rescued every 2 s and put back). Once per wander point inside a cave's bounds: the point
        /// goes onto the nearest true cave floor (or where the NPC is), as <see cref="Destination"/> does for an Active walk.
        /// </summary>
        void CheckNearWander()
        {
            if (Agent.Activity != NpcActivity.Travelling || !Agent.Station.IsNone || Agent.Detail == DetailLevel.Active) return;
            if (!_simulation.TryGetTarget(Agent, out var target) || _wanderChecked is WorldPose seen && seen.Equals(target)) return;
            _wanderChecked = target;
            if (!CaveGround.InCave(ToUnity(target))) return;
            var p = Destination(target, out _, out bool moved);
            if (!moved) return;
            if (_simulation.CorrectWanderTarget(Agent.Id, new WorldPose(p.x, p.y, p.z, target.Yaw)) &&
                _simulation.TryGetTarget(Agent, out var corrected))
                _wanderChecked = corrected;
        }

        // ------------------------------------------------------------------------- cave guard (fix-128, CaveFloorRules)

        /// <summary>How often a shown body checks that it stands on a true cave floor, s.</summary>
        const float CaveGuardSeconds = 2f;
        float _caveGuard;

        /// <summary>
        /// Every <see cref="CaveGuardSeconds"/>: a body (not at a station, not over water) on a cave roof / wall top inside the
        /// hill or in the hollow rock around a cave (a simulated pose, a combat push, a step off the NavMesh) goes onto the
        /// nearest true cave floor (<see cref="CaveGround.Rescue"/>, logged); the simulation keeps the new pose.
        /// </summary>
        /// fix-133: the new pose goes to the simulation with <see cref="NpcSimulation.CorrectPose"/> (ReportPose is ignored
        /// for Near bodies, which then went back into the rock the next frame); the log is bounded per NPC
        /// (<see cref="G3.Player.CaveGuardBudget"/>: once per minute, then off with one warning when it keeps recurring).
        void CaveGuard()
        {
            if ((_caveGuard -= _dt) > 0f) return;
            _caveGuard = CaveGuardSeconds;
            if (!_caveBudget.Active || Agent.Activity == NpcActivity.AtStation || _walker.OverWater || InSwimDepth) return;
            if (CaveGround.MayStand(transform.position)) return;
            if (!G3.Player.RelocationGate.Bodies.TryTake(Time.frameCount)) { _caveGuard = 0f; return; } // fix-191: next frame
            var at = transform.position;
            var outcome = CaveGround.Rescue(transform, "cave guard", log: false);
            if (outcome == CaveGround.Outcome.Fine) return;
            if (outcome == CaveGround.Outcome.NoFloor)
            {
                var f = _caveBudget.Failed();
                if (f == G3.Player.CaveGuardBudget.Log.Once)
                    Debug.LogWarning($"[npc] {name} cave guard: on a cave roof / in the rock at {at}, no true cave floor within 16 m");
                else if (f == G3.Player.CaveGuardBudget.Log.GiveUp)
                    Debug.LogWarning($"[npc] {name} cave guard: no true cave floor within 16 m of {at} in {_caveBudget.Failures} checks; guard off for this NPC");
                return;
            }
            _walker.Clear();
            _ground.Reset();
            if (_animator != null) _animator.Teleported();
            _simulation.CorrectPose(Agent.Id, FromUnity(transform));
            _wanderChecked = null;
            var to = transform.position;
            var r = _caveBudget.Rescued(Time.timeAsDouble);
            if (r == G3.Player.CaveGuardBudget.Log.Once)
                Debug.Log($"[npc] {name} cave guard: on a cave roof / in the rock at {at}, moved {Vector3.Distance(at, to):F1} m to the cave floor {to}");
            else if (r == G3.Player.CaveGuardBudget.Log.GiveUp)
                Debug.LogWarning($"[npc] {name} cave guard: rescued {_caveBudget.Rescues} times within {G3.Player.CaveGuardBudget.LogWindow:F0} s " +
                                 $"(last {at} -> {to}): something keeps putting it back; guard off for this NPC");
        }

        readonly G3.Player.CaveGuardBudget _caveBudget = new G3.Player.CaveGuardBudget();

        // ------------------------------------------------------------------------- buried guard (fix-181, BodySinkRules)

        float _buriedGuard;
        static int s_buriedLogCount;
        static float s_buriedLogWindow = -100f;

        /// <summary>
        /// fix-181 safety net, every <see cref="CaveGuardSeconds"/>: a shown body (not at a station, not over water, not in a
        /// fight) whose feet are inside the ground — its own floor, the column's surface nearest its height within
        /// <see cref="WanderReach"/> above (<see cref="NpcGround.TryFloor"/>: never up through a ceiling, never onto a cave
        /// roof), lies more than <see cref="G3.Player.BodySinkRules.Tolerance"/> above the feet and nothing carries them — is
        /// put onto that floor; the simulation keeps the pose (CorrectPose). G3 keeps every body on collision
        /// (PutToGround); a body inside the hill has no floor under its steps, so the walker could never bring it out.
        /// </summary>
        void BuriedGuard()
        {
            if ((_buriedGuard -= _dt) > 0f) return;
            _buriedGuard = CaveGuardSeconds;
            if (!FloorGuards || BodyTaken || Agent.Activity == NpcActivity.AtStation || _walker.OverWater || InSwimDepth) return;
            var at = transform.position;
            if (!NpcGround.TryFloor(at, HalfLength, out var floor, NpcWalker.FloorReach, WanderReach)) return;
            float lift = floor.Y - at.y;
            if (lift <= G3.Player.BodySinkRules.Tolerance) return;
            // fix-191: one body moved per frame over all NPCs (build 22c crashed natively right after several in one frame)
            if (!G3.Player.RelocationGate.Bodies.TryTake(Time.frameCount)) { _buriedGuard = 0f; return; }
            transform.position = new Vector3(at.x, floor.Y, at.z);
            Physics.SyncTransforms();
            _walker.Clear();
            _ground.Reset();
            if (_animator != null) _animator.Teleported();
            _simulation.CorrectPose(Agent.Id, FromUnity(transform));
            _wanderChecked = null;
            if (G3.Player.BodySinkRules.MayLog(Time.unscaledTime, ref s_buriedLogCount, ref s_buriedLogWindow))
                Debug.Log($"[npc] fix-181: {name} ({Agent.Activity}, {Agent.Detail}) was {lift:F2} m inside the ground at {at} " +
                          $"(floor {(floor.Collider != null ? floor.Collider.name : "?")}): put onto it");
        }

        // ------------------------------------------------------------------------- spin guard (fix-181, SpinRules)

        readonly G3.Player.SpinMonitor _spin = new G3.Player.SpinMonitor();
        static int s_spinLogCount;
        static float s_spinLogWindow = -100f;

        /// <summary>
        /// fix-181 safety net: a body that keeps turning one way on the spot (<see cref="G3.Player.SpinMonitor"/>: two full turns
        /// with under 1.5 m walked) is logged with its state; a walk of its own (not a fight's) is given up there, so nothing
        /// turns a body round and round without end.
        /// </summary>
        void SpinGuard()
        {
            var p = transform.position;
            if (!_spin.Step(transform.eulerAngles.y, p.x, p.z, _dt)) return;
            if (G3.Player.BodySinkRules.MayLog(Time.unscaledTime, ref s_spinLogCount, ref s_spinLogWindow))
                Debug.LogWarning($"[npc] fix-181: {name} turned {_spin.LastTurn:F0} deg on the spot in {_spin.LastSeconds:F1} s at {p} " +
                                 $"({Agent.Activity}, {Agent.Detail}, taken {BodyTaken}, walk {_walker.HasDestination}/{_walker.Plan}, " +
                                 $"evading {_walker.Evading}, slide {_slideSpeed:F1}, interaction {(_animator != null ? _animator.Interaction : null)})");
            if (BodyTaken || !FloorGuards) return;
            if (Agent.Activity == NpcActivity.Travelling && Agent.Detail == DetailLevel.Active && Agent.Station.IsNone) EndWander();
            else _walker.Clear();
        }

        /// <summary>At the station: stand on its slot, turn to face its way, then do the interaction.</summary>
        void StayAtStation()
        {
            ToolsOf(Agent.Station, out _toolsLeft, out _toolsRight);
            if (_returning && WalkBack()) return;
            _walker.Clear();
            var pose = _simulation.PoseOf(Agent);
            var p = ToUnity(pose);
            p.y = StationHeight(p); // fix-163
            var at = transform.position;
            if ((at - p).sqrMagnitude > 4f && _animator != null) _animator.Teleported(); // jumped (load, abstract arrival)
            // Write only on change: a transform write (even of the same value) re-syncs the body's trigger colliders to
            // PhysX and marks its renderers moved, every frame for ~150 NPCs standing at their stations (perf-8).
            if (at != p) transform.position = p;
            if (FaceWatched()) return;
            // ZS_Smalltalk: the two talkers face each other instead of the spots' headings.
            var partner = _simulation.SmalltalkPartnerOf(Agent);
            float yaw = partner != null ? pose.YawTowards(_simulation.PoseOf(partner)) : pose.Yaw;
            var facing = Quaternion.Euler(0, yaw, 0);
            var current = transform.rotation;
            var turned = Quaternion.RotateTowards(current, facing, StationTurnSpeed * _dt);
            if (turned != current) transform.rotation = turned;
            SetInteraction(Quaternion.Angle(turned, facing) < 5f ? _simulation.InteractionOf(Agent, partner) : null); // the partner found above (perf-3)
        }

        /// <summary>
        /// Keep an eye on <paramref name="target"/> for <paramref name="seconds"/> (G3 ZS_ObserveSuspect, a warning): at its
        /// station the NPC leaves its interaction and turns to face the target <b>[interp: G3 lets seated NPCs keep
        /// sitting]</b>.
        /// </summary>
        public void Watch(Transform target, float seconds, bool cheer = false)
        {
            _watched = target;
            _watchUntil = Time.time + seconds;
            _cheer = cheer;
        }

        /// <summary>Keeps an eye on someone now (<see cref="Watch"/>): G3 AI mode Reaction, so not unaware of the hero (fix-28).</summary>
        public bool Watching => (_watched != null && Time.time < _watchUntil) || _fight != null;

        // ------------------------------------------------------------------------- watching a fight (ZS_WatchFight)

        FightWatcher _fight;
        Func<ActorId, Transform> _fighterBody;
        Func<ActorId, bool> _fighterDown;
        float _fightRepath;
        Vector3? _spectatorSpot;

        /// <summary>The fight of <paramref name="a"/> and <paramref name="b"/> the NPC watches now (null = none).</summary>
        public FightWatcher WatchedFight => _fight;

        /// <summary>
        /// ZS_WatchFight (<see cref="FightWatcher"/>): an active NPC keeps 5.5–6.5 m from the nearer fighter, follows a fight
        /// that moves off, cheers while it stands, applauds the winner. A further hit of the same fight renews the watch.
        /// An arena fight is watched as a spectator (<paramref name="spectator"/>, ZS_WatchFight_Spectator, fix-85): walk to
        /// the free spectator point <paramref name="spot"/> if there is one, else stay, and only turn to the fighters.
        /// </summary>
        public void WatchFight(ActorId a, ActorId b, Func<ActorId, Transform> body, Func<ActorId, bool> down,
                               bool spectator = false, Vector3? spot = null)
        {
            if (_fight != null && ((_fight.A == a && _fight.B == b) || (_fight.A == b && _fight.B == a)))
            {
                _fight.Renew();
                return;
            }
            _fight = new FightWatcher(a, b, spectator, toSpot: spectator && spot.HasValue);
            _spectatorSpot = spectator ? spot : null;
            _fighterBody = body;
            _fighterDown = down;
            _fightRepath = 0;
        }

        /// <summary>ZS_Applaud: face <paramref name="target"/> and applaud, then watch it (ZS_ObserveSuspect).</summary>
        public void Applaud(Transform target, float seconds = FightWatcher.ApplaudSeconds)
        {
            _fight = null;
            Watch(target, seconds, cheer: true); // [interp] the Applaud overlay (action 0x82) is shown with the cheer motion
        }

        /// <summary>One frame of a fight watch; false when there is none (or the NPC is busy otherwise).</summary>
        bool KeepWatchingFight()
        {
            if (_fight == null) return false;
            if (Agent.Detail != DetailLevel.Active || Agent.Activity == NpcActivity.Talking || Agent.Activity == NpcActivity.Escorting)
            {
                _fight = null;
                return false;
            }
            var here = transform.position;
            var ta = _fighterBody?.Invoke(_fight.A);
            var tb = _fighterBody?.Invoke(_fight.B);
            float da = ta != null ? Flat(ta.position - here).magnitude : float.NaN;
            float db = tb != null ? Flat(tb.position - here).magnitude : float.NaN;
            var step = _fight.Update(_dt, da, db, _fighterDown?.Invoke(_fight.A) ?? false, _fighterDown?.Invoke(_fight.B) ?? false);
            var focus = _fight.Focus == _fight.A ? ta : _fight.Focus == _fight.B ? tb : null;
            switch (step)
            {
                case WatchStep.Done:
                    _fight = null;
                    _walker.Clear();
                    return false;
                case WatchStep.Applaud:
                    _walker.Clear();
                    if (focus != null) Applaud(focus);
                    return false;
                case WatchStep.GoToSpot:
                {
                    SetInteraction(null);
                    _returning = true; // back to the station afterwards (WalkBack)
                    if (_animator != null && !_animator.CanWalk) return true;
                    var spot = _spectatorSpot ?? here;
                    if (!_walker.HasDestination) _walker.SetDestination(here, spot, keepProgress: true);
                    float walk = _animator != null ? _animator.WalkSpeed : 1.6f;
                    if (_walker.Step(transform, walk, _dt) != NpcWalker.Result.Walking)
                    {
                        _walker.Clear();
                        _fight.AtSpot(); // there, or the way is blocked: watch from here
                    }
                    _simulation.ReportPose(Agent.Id, FromUnity(transform));
                    return true;
                }
                case WatchStep.Approach:
                case WatchStep.StepBack:
                    if (focus == null) return false;
                    SetInteraction(null);
                    _returning = true; // back to the station afterwards (WalkBack)
                    if (_animator != null && !_animator.CanWalk) return true;
                    var away = Flat(here - focus.position);
                    if (away.sqrMagnitude < 0.01f) away = -transform.forward;
                    var dest = step == WatchStep.Approach ? focus.position + away.normalized * (FightWatcher.StopAt - 0.3f)
                                                          : focus.position + away.normalized * (FightWatcher.StopAt + 0.3f);
                    if ((_fightRepath -= _dt) <= 0 || !_walker.HasDestination)
                    {
                        _walker.SetDestination(here, dest, keepProgress: true);
                        _fightRepath = RepathSeconds;
                    }
                    float speed = _animator != null ? _animator.WalkSpeed : 1.6f;
                    if (_walker.Step(transform, speed, _dt, focus) != NpcWalker.Result.Walking) _walker.Clear();
                    _simulation.ReportPose(Agent.Id, FromUnity(transform));
                    return true;
                default:
                    _walker.Clear();
                    if (focus != null) Face(focus.position);
                    var to = focus != null ? Flat(focus.position - here) : Vector3.zero;
                    bool facing = to.sqrMagnitude <= 0.01f || Vector3.Angle(transform.forward, to) < 10f;
                    SetInteraction(step == WatchStep.Stand && facing ? "Spectator" : null);
                    return true;
            }
        }

        bool _cheer; // watching a fight: cheer (ZS_WatchFight's "Spectator" motion) instead of standing

        bool FaceWatched()
        {
            if (_watched == null || Time.time >= _watchUntil)
            {
                _watched = null;
                return false;
            }
            var to = Vector3.ProjectOnPlane(_watched.position - transform.position, Vector3.up);
            bool facing = to.sqrMagnitude <= 0.01f || Vector3.Angle(transform.forward, to) < 10f;
            SetInteraction(_cheer && facing ? "Spectator" : null);
            if (to.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to), StationTurnSpeed * _dt);
            return true;
        }

        /// <summary>
        /// After a fight the body stands where the fight left it (combat reported that pose): walk back to the station
        /// slot (G3 NPCs return to their routine on foot) instead of staying or snapping. Once there, or when too far to
        /// walk, the agent is put on the slot pose again. False when the station shows normally.
        /// </summary>
        bool WalkBack()
        {
            if (!_simulation.TryGetTarget(Agent, out var slotPose))
            {
                _returning = false;
                return false;
            }
            var slot = ToUnity(slotPose);
            var off = slot - transform.position;
            off.y = 0;
            if (off.sqrMagnitude < WalkBackArrive * WalkBackArrive || off.sqrMagnitude > WalkBackMax * WalkBackMax)
            {
                Returned(slotPose);
                return false;
            }
            SetInteraction(null);
            if (_animator != null && !_animator.CanWalk) return true; // standing up first
            if (!_walker.HasDestination) _walker.SetDestination(transform.position, slot);
            float speed = TravelSpeed();
            if (_walker.Step(transform, speed, _dt) == NpcWalker.Result.Walking)
            {
                _simulation.ReportPose(Agent.Id, FromUnity(transform));
                return true;
            }
            Returned(slotPose);
            return false;
        }

        void Returned(WorldPose slot)
        {
            _walker.Clear();
            _returning = false;
            _simulation.ReportPose(Agent.Id, slot);
        }

        /// <summary>
        /// G3 GetTravellingWalkMode: wolves, scavengers, bisons, rhinos, snappers and minecrawlers run to their points
        /// (the locomotion blend picks the Run clip from the speed); everybody else walks.
        /// </summary>
        float TravelSpeed()
        {
            if (_animator == null) return 1.6f;
            return _simulation != null && _simulation.Runs(Agent) ? _animator.GaitSpeed("Run", 4f) : _animator.WalkSpeed;
        }

        /// <summary>Near (or idle): show the simulated pose on the ground; an idle wanderer plays its ambient action.</summary>
        void FollowSimulation()
        {
            CheckNearWander();
            var pose = _simulation.PoseOf(Agent);
            var p = ToUnity(pose);
            float half = HalfLength;
            p.y = _ground.HeightAt(p, p.y, half, GuessReach(Agent));
            if (!_walker.OverWater && Agent.Detail == DetailLevel.Active) SlideOffSteep(ref p, half);
            float ground = p.y;
            if (_walker.OverWater) p.y = NpcWalker.SurfaceOrGround(p);
            // G3 ZS_Ambient / ZS_Monster: a bloodfly over water plays no ambient idles (it only holds its fists).
            bool overWater = p.y > ground + 0.01f;
            SetInteraction(Agent.Activity == NpcActivity.Idle && !overWater ? _simulation.InteractionOf(Agent) : null);
            transform.GetPositionAndRotation(out var at, out var current);
            var turned = Quaternion.RotateTowards(current, Quaternion.Euler(0, pose.Yaw, 0), StationTurnSpeed * _dt);
            if (at != p || turned != current) transform.SetPositionAndRotation(p, turned); // no write when standing (see StayAtStation)
        }

        // ------------------------------------------------------------------------- floor (fix-108, NpcGroundRules)

        /// <summary>Highest a wander point's floor may lie above the spawn height it carries (m; 3-5 m away on up to ~60°).</summary>
        const float WanderReach = 8f;

        /// <summary>
        /// fix-181: how far above its simulated height a shown body's floor may be looked for. A body without a station
        /// (a wanderer, an NPC idling where it stopped) shows a pose whose height is only a guess: G3 ZS_Ambient's wander point
        /// carries the spawn's height (<see cref="AmbientRules.WanderPoint"/>), a Near body interpolates between such heights.
        /// On a mountain slope the floor 3-5 m from the spawn lies metres above that height; the slope reach (at most 3 m)
        /// missed it, the column ray from inside the hill found nothing, and the body was drawn at the guessed height inside
        /// the ground (owner, build 21a Nordmar: snow wolves walking into the slope; an Active body there could not step out,
        /// no floor under its steps, and only turned to each new wander point). The Active walk's end already uses this reach
        /// (<see cref="Destination"/>).
        /// </summary>
        static float GuessReach(NpcAgent agent) => FloorGuards && agent != null && agent.Station.IsNone ? WanderReach : -1f;

        /// <summary>
        /// fix-181: the guessed-height reach and the buried / spin guards are on (default). Dev runners switch them off to
        /// compare in one Play session (the spin guard then only logs).
        /// </summary>
        public static bool FloorGuards = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetFloorGuards() => FloorGuards = true;

        /// <summary>fix-181: deepest a slide tick of <paramref name="d"/> m on a <paramref name="slopeDeg"/> face may go down, m.</summary>
        static float SlideDropMax(float d, float slopeDeg) => 0.5f + d * Mathf.Tan(Mathf.Clamp(slopeDeg, 0f, 85f) * Mathf.Deg2Rad) * 1.5f;
        /// <summary>A face at least this steep (deg) is a rim the sliding body drops off when the air below is free (fix-184).</summary>
        const float SlideRimDeg = 80f;

        /// <summary>Nothing solid (characters aside) in the column at <paramref name="q"/> from <paramref name="fromY"/> down to just over q's floor.</summary>
        bool OpenBelow(Vector3 q, float fromY)
        {
            var top = new Vector3(q.x, fromY + 0.3f, q.z);
            float length = top.y - (q.y + 0.3f);
            if (length <= 0f) return true;
            int n = Physics.RaycastNonAlloc(top, Vector3.down, _openHits, length, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _openHits[i].collider;
                if (c is CharacterController || c.attachedRigidbody != null || c.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }

        static readonly RaycastHit[] _openHits = new RaycastHit[8];

        /// <summary>Longest slide of one episode (m): a body never slides on and on along a broken collision face.</summary>
        const float SlideMax = 40f;

        float _slideSpeed, _slid;
        CombatBody _combatBody;

        /// <summary>Half the body's length (long animals, CombatBody) or its radius, m: the floor's slope reach.</summary>
        float HalfLength
        {
            get
            {
                if (_combatBody == null) _combatBody = GetComponentInChildren<CombatBody>();
                return _combatBody != null ? _combatBody.HalfLength : 0.35f;
            }
        }

        int _bodyLookedFrame = -1000;

        /// <summary>
        /// Top of the body above the feet, m (CombatBody.Top; 0 while the view has no body yet): sizes the perception's sight rays
        /// (fix-204). A missing body is looked for again at most every 60 frames (read every frame by the perception driver).
        /// </summary>
        public float BodyTop
        {
            get
            {
                if (_combatBody == null)
                {
                    if (Time.frameCount - _bodyLookedFrame < 60) return 0f;
                    _bodyLookedFrame = Time.frameCount;
                    _combatBody = GetComponentInChildren<CombatBody>();
                }
                return _combatBody != null ? _combatBody.Top : 0f;
            }
        }

        /// <summary>Speed of the current slide down a steep face (m/s; 0 = standing on walkable floor).</summary>
        public float SlideSpeed => _slideSpeed;

        /// <summary>
        /// G3 gravity on steep ground (gCCharacterMovement_PS slide mode: floor steeper than SteepGroundAngleMax 49°): an
        /// idle body standing on such a face slides down it (g·sin, at most SlideSpeedMax) until it stands on walkable
        /// floor; the simulation keeps the new pose. Before fix-108 creatures spawned or wandered onto 60-70° mountain faces
        /// (G3 spawn points on such faces; our walker had no slope limit) and stood there as if without gravity [interp:
        /// no slide motion is played].
        /// </summary>
        void SlideOffSteep(ref Vector3 p, float half)
        {
            float slope = _ground.SlopeDeg;
            if (!G3.Player.NpcGroundRules.Slides(slope) || _slid >= SlideMax)
            {
                if (!G3.Player.NpcGroundRules.Slides(slope)) _slid = 0f;
                _slideSpeed = 0f;
                return;
            }
            var n = _ground.Normal;
            var down = new Vector3(n.x, 0f, n.z);
            if (down.sqrMagnitude < 1e-6f) return;
            down.Normalize();
            float d = G3.Player.NpcGroundRules.SlideStep(ref _slideSpeed, slope, _dt);
            if (d <= 0f) return;
            var q = p + down * d;
            q.y = _ground.HeightAt(q, p.y, half);
            if (q.y > p.y + 0.05f) { _slideSpeed = 0f; return; } // a slide never goes up (a fold, a rock in the way)
            // fix-181: nor through the face to a floor far below it (a cave, the other side of a broken face): one tick moves
            // a few cm, so the face under it can only be that much lower
            // fix-184 (regress-1021b fix108_slope_ground: the deer slid 3.2 m down the 65 deg face and stopped on its
            // 89.9 deg rim, standing on the cliff): on a vertical rim with open air down to the floor below it falls
            if (q.y < p.y - SlideDropMax(d, slope) && !(slope >= SlideRimDeg && OpenBelow(q, p.y))) { _slideSpeed = 0f; return; }
            _slid += d;
            p = q;
            _simulation.ReportPose(Agent.Id, new WorldPose(p.x, p.y, p.z, transform.eulerAngles.y));
        }

        // ------------------------------------------------------------------------------------------- party (Docs/party.md)

        const float RepathSeconds = 0.5f, RepathMoved = 1.5f;
        const float HeroViewCos = 0.5f; // the guide sees the hero within 60 degrees of its forward [interp: G3 FOV check]

        /// <summary>
        /// Active and escorting the hero: a follower keeps its follow distance (<see cref="FollowPlanner"/>), a waiting
        /// member stands and faces the hero, a guide walks to the destination and waits for him (<see cref="GuidePlanner"/>).
        /// The pose is reported every frame so the simulation, the save and the story's position tests see it.
        /// </summary>
        void Escort()
        {
            SetInteraction(null);
            _returning = false;
            _walkingTo = Station.None;
            if (_simulation.TryGetNpcLeader(Agent.Id, out var npcLeader))
            {
                FollowNpcLeader(npcLeader);
                return;
            }
            var escorts = _simulation.Escorts;
            var mode = _simulation.EscortOf(Agent.Id);
            if (escorts == null || Leader == null || mode == EscortMode.None) return;
            if (_animator != null && !_animator.CanWalk) return; // standing up first
            var leaderGait = LeaderGait();
            switch (mode)
            {
                case EscortMode.Follow:
                    Follow(escorts.FollowDistance(Agent.Id), leaderGait);
                    break;
                case EscortMode.Guide:
                    Lead(escorts, leaderGait);
                    break;
                default:
                    _walker.Clear();
                    Face(Leader.position);
                    break;
            }
            _simulation.ReportPose(Agent.Id, FromUnity(transform));
        }

        float _npcFollowCheck;
        bool _npcFollowGoing;

        /// <summary>
        /// ZS_FollowNPC: every 1 s, farther than 5 m from the NPC leader, walk to it (OnFollowNPC stops the walk once
        /// within 5 m); the walk mode of the goto is not decoded: the follower runs [interp].
        /// </summary>
        void FollowNpcLeader(NpcAgent leader)
        {
            if (_animator != null && !_animator.CanWalk) return;
            var to = ToUnity(_simulation.PoseOf(leader));
            float distance = Flat(to - transform.position).magnitude;
            if (distance <= NpcParties.FollowDistance) _npcFollowGoing = false;
            else if ((_npcFollowCheck -= _dt) <= 0)
            {
                _npcFollowCheck = NpcParties.RecheckSeconds;
                _npcFollowGoing = true;
            }
            if (!_npcFollowGoing)
            {
                _walker.Clear();
                Face(to);
            }
            else if (WalkTowards(to, EscortGait.Run) != NpcWalker.Result.Walking) _walker.Clear();
            _simulation.ReportPose(Agent.Id, FromUnity(transform));
        }

        void Follow(float followDistance, EscortGait leaderGait)
        {
            var hero = Leader.position;
            float distance = Flat(hero - transform.position).magnitude;
            // The hero swims: wait on the shore (G3 ZS_WaitForPlayer; Docs/party.md section 3), no catching up into the water.
            bool heroSwims = Leader.TryGetComponent<G3Player>(out var player) && player.Swimming;
            if (FollowPlanner.NeedsCatchUp(distance) && !heroSwims && !_follow.WaitingForSwimmer)
            {
                CatchUp(followDistance);
                return;
            }
            var gait = _follow.Update(_dt, distance, followDistance, leaderGait, heroSwims);
            if (gait == EscortGait.Stand)
            {
                _walker.Clear();
                Face(hero);
                return;
            }
            // Fallen behind (out of the follow range): the G3 route to the hero, so the follower comes round the terrace
            // instead of being put next to him; stuck on it until it gives up, or no route: caught up as before.
            var to = hero;
            bool ribbon = false;
            bool onG3 = distance > FollowRouteMetres && G3Leg(_g3Follow, transform.position, hero, "follow", out to, out ribbon);
            // the follower walks to the hero: G3 does not evade the entity it walks to (fix-93); it stops at contact
            var result = WalkTowards(onG3 ? to : hero, gait, onG3 && ribbon, Leader);
            if (result == NpcWalker.Result.Arrived && onG3) _g3Follow.LegReached();
            if (result != NpcWalker.Result.Stuck) return;
            _walker.Clear();
            if (onG3 && G3Stuck(_g3Follow, "follow")) return;
            CatchUp(followDistance);
        }

        /// <summary>A routine walk longer than this (flat m) goes on the G3 route.</summary>
        const float RoutineRouteMetres = 40f;
        /// <summary>A follower farther than this from the hero (flat m) goes on the G3 route.</summary>
        const float FollowRouteMetres = 15f;

        /// <summary>
        /// The G3 graph's leg for a walk from <paramref name="self"/> to <paramref name="goal"/>; false without a route (no
        /// graph, switched off, start or goal in no zone / path, no connection). <paramref name="ribbon"/>: walk it straight.
        /// </summary>
        bool G3Leg(NavRouteFollower route, Vector3 self, Vector3 goal, string who, out Vector3 walkTo, out bool ribbon)
        {
            walkTo = goal;
            ribbon = false;
            var graph = G3NavGraphRuntime.Active;
            if (graph == null) return false;
            bool found = route.Update(graph, Time.timeAsDouble, self.x, self.y, self.z, goal.x, goal.y, goal.z, out var leg, out bool planned);
            if (planned && who != null) Debug.Log($"[nav] {who} {Agent.Id} at {self}: {route.Note}");
            if (!found) return false;
            walkTo = new Vector3(leg.X, leg.Y, leg.Z);
            ribbon = route.Ribbon;
            return true;
        }

        /// <summary>A stuck period on a G3 leg: true while the route goes on (along the ribbon / to the next point).</summary>
        bool G3Stuck(NavRouteFollower route, string who)
        {
            bool giveUp = route.Stuck(Time.timeAsDouble);
            if (who != null)
                Debug.Log($"[nav] {who} {Agent.Id} stuck on its G3 route at {transform.position}: " +
                          (giveUp ? route.Note : $"{(route.Ribbon ? "along the ribbon" : "on")} to point {route.Next}"));
            return !giveUp;
        }

        /// <summary>A routine walk to a far point along the G3 route; Arrived only at the destination.</summary>
        NpcWalker.Result WalkG3(Vector3 destination, float speed)
        {
            var self = transform.position;
            if (!G3Leg(_g3Walk, self, destination, null, out var to, out bool ribbon))
            {
                // no route (yet, or given up): the NavMesh way, as for a near point
                _walkOnG3 = false;
                _walker.SetDestination(self, destination);
                return NpcWalker.Result.Walking;
            }
            if (!_walker.HasDestination || _walker.Direct != ribbon || (to - _escortTarget).sqrMagnitude > 0.01f)
            {
                if (ribbon) _walker.SetDirect(self, to);
                else _walker.SetDestination(self, to);
                _escortTarget = to;
            }
            var result = _walker.Step(transform, speed, _dt);
            if (result == NpcWalker.Result.Arrived && !_g3Walk.AtLastPoint)
            {
                _g3Walk.LegReached();
                return NpcWalker.Result.Walking;
            }
            if (result == NpcWalker.Result.Stuck)
            {
                _walker.Clear();
                if (!_g3Walk.AtLastPoint && G3Stuck(_g3Walk, null)) return NpcWalker.Result.Walking;
            }
            return result;
        }

        void Lead(IEscorts escorts, EscortGait heroGait)
        {
            if (!escorts.TryGetDestination(Agent.Id, out var destinationPose))
            {
                _walker.Clear();
                Face(Leader.position);
                return;
            }
            var destination = ToUnity(destinationPose);
            var self = transform.position;
            var toHero = Flat(Leader.position - self);
            bool heroInView = toHero.sqrMagnitude < 0.01f || Vector3.Dot(transform.forward, toHero.normalized) >= HeroViewCos;
            float toDestination = Flat(destination - self).magnitude;
            // The CP view test compares distances to the point the guide walks to now (its current path corner), not to the
            // far destination: on a winding path a hero behind the guide can be nearer the destination as the crow flies.
            var point = _walker.HasDestination ? _walker.CurrentPoint : destination;
            var gait = _guide.Update(toHero.magnitude, heroInView, Flat(point - Leader.position).magnitude, Flat(point - self).magnitude,
                                     Flat(destination - Leader.position).magnitude, toDestination, heroGait);
            string state = gait != EscortGait.Stand ? "walks" : toDestination <= GuidePlanner.ArriveMetres ? "arrived" : "waits";
            if (state != _guideState)
            {
                _guideState = state;
                Debug.Log($"[party] guide {Agent.Id} {state} toDest={toDestination:F1} toHero={toHero.magnitude:F1} heroInView={heroInView}");
            }
            if (gait == EscortGait.Stand)
            {
                _walker.Clear();
                _guideStuck = 0;
                Face(Leader.position);
                return;
            }
            // The G3 navigation graph's route (Docs/navigation.md), each leg on the NavMesh or along the ribbon; without one,
            // a far destination without a complete NavMesh path: walk the planned route in legs (GuideRoute).
            var walkTo = destination;
            bool onG3 = G3Leg(_g3Guide, self, destination, "guide", out var g3To, out bool ribbon);
            bool onLeg = false, planned = false;
            var leg = destination;
            if (onG3) walkTo = g3To;
            else onLeg = _route.TryLeg(self, destination, out leg, out planned);
            if (onLeg)
            {
                walkTo = leg;
                if (planned && (leg - _loggedLeg).sqrMagnitude > 25f)
                {
                    _loggedLeg = leg;
                    Debug.Log($"[party] guide {Agent.Id} leg to {leg} toDest={toDestination:F1}: {_route.Note}");
                }
            }
            var result = WalkTowards(walkTo, gait, onG3 && ribbon);
            if (result == NpcWalker.Result.Walking)
            {
                if (_waitsForBuild && _walker.PathComplete) _waitsForBuild = false;
                return;
            }
            if (result == NpcWalker.Result.Arrived)
            {
                _guideStuck = 0;
                _buildWait = 0;
                if (onG3) _g3Guide.LegReached();
                else if (onLeg) _route.LegDone(); // the next segment
                return;
            }
            // No progress for 4 s on a G3 leg: along the ribbon, then the next leg; only when the follower gives the route
            // up do the NavMesh rules below count (build wait, stuck periods, the teleport fallback).
            if (onG3 && G3Stuck(_g3Guide, "guide"))
            {
                _walker.Clear();
                return;
            }
            // No progress for 4 s. While the NavMesh around the guide or its leg is still being built (after the start or a
            // teleport the cells come one by one), that is waiting, not being stuck (editor-runner-22: Geldern_Slave_01's
            // partial path was the build in progress); at most 90 s of it, then the stuck rule counts again.
            _walker.Clear();
            if (_buildWait < 90f && (CellNavMeshes.IsBuildingNear(self, 40f) || CellNavMeshes.IsBuildingNear(walkTo, 40f)))
            {
                _buildWait += 4f;
                if (!_waitsForBuild) Debug.Log($"[party] guide {Agent.Id} waits for the NavMesh build at {transform.position} toDest={toDestination:F1}: {_walker.Plan}");
                _waitsForBuild = true;
                return;
            }
            _waitsForBuild = false;
            // standing inside a static collider (its cell loaded after the spawn; a rock pile): out to a free spot, walk on
            // (counts as a stuck period all the same: a NavMesh through the rock would lead it back in)
            if (NpcPlacement.Resolve(transform, "stuck") && _animator != null) _animator.Teleported();
            // Its plan already goes as far towards the target as the connected NavMesh and the drop links allow. A few more
            // periods on fresh plans (more for a longer way), then, as a routine NPC that cannot reach its point, the guide
            // is put there [interp: G3's _AI_Goto_Ext walks G3's own paths, which our NavMesh may not connect]. Rare: logged
            // as a warning.
            int periods = G3.Player.NavRoutePlanner.StuckPeriods(_route.WayMetres > 0 ? _route.WayMetres : toDestination);
            _guideStuck++;
            Debug.Log($"[party] guide {Agent.Id} stuck ({_guideStuck}/{periods}) at {transform.position} toDest={toDestination:F1}: {_walker.Plan}; route: {_route.Note}");
            _route.LegDone();
            if (_guideStuck < periods) return;
            _guideStuck = 0;
            _buildWait = 0;
            Debug.LogWarning($"[party] GUIDE TELEPORT FALLBACK: guide {Agent.Id} cannot reach {destination} from {transform.position} " +
                             $"({toDestination:F0} m, {periods} stuck periods; {_walker.Plan}; route: {_route.Note}): put at the destination");
            transform.position = new Vector3(destination.x, NpcWalker.FloorHeight(destination, destination.y), destination.z);
            NpcPlacement.Resolve(transform, "put at the destination");
            if (_animator != null) _animator.Teleported();
            _route.Reset();
            _g3Guide.Reset();
        }

        /// <summary>Walks along a (re)planned path; re-plans when the target moved or every half second.</summary>
        NpcWalker.Result WalkTowards(Vector3 target, EscortGait gait, bool ribbon = false, Transform walksTo = null)
        {
            _repathTimer -= _dt;
            if (!_walker.HasDestination || _walker.Direct != ribbon || _repathTimer <= 0 ||
                (target - _escortTarget).sqrMagnitude > RepathMoved * RepathMoved)
            {
                if (ribbon) _walker.SetDirect(transform.position, target, keepProgress: true);
                else _walker.SetDestination(transform.position, target, keepProgress: true);
                _escortTarget = target;
                _repathTimer = RepathSeconds;
            }
            return _walker.Step(transform, SpeedOf(gait), _dt, walksTo);
        }

        /// <summary>Too far or stuck: the follower appears at its follow distance behind the hero (Docs/party.md section 3) [interp].</summary>
        void CatchUp(float followDistance)
        {
            _walker.Clear();
            _follow.Reset();
            var hero = Leader.position;
            Debug.Log($"[party] follow {Agent.Id} catches up from {Flat(hero - transform.position).magnitude:F1} m");
            var pose = AbstractEscort.Behind(FromUnity(Leader), FromUnity(transform), followDistance);
            var p = new Vector3(pose.X, hero.y, pose.Z);
            if (UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 3f, UnityEngine.AI.NavMesh.AllAreas)) p = hit.position;
            p.y = NpcWalker.FloorHeight(new Vector3(p.x, Mathf.Max(p.y, hero.y), p.z), hero.y); // behind a hero on a wall walk: the floor, not the air (fix-60)
            transform.SetPositionAndRotation(p, Quaternion.Euler(0, pose.Yaw, 0));
            NpcPlacement.Resolve(transform, "caught up");
            if (_animator != null) _animator.Teleported();
        }

        float SpeedOf(EscortGait gait)
        {
            switch (gait)
            {
                case EscortGait.Walk: return _animator != null ? _animator.WalkSpeed : 1.6f;
                case EscortGait.Sprint: return _animator != null ? _animator.GaitSpeed("Sprint", _animator.GaitSpeed("Run", 4f) * 1.4f) : 5.6f;
                default: return _animator != null ? _animator.GaitSpeed("Run", 4f) : 4f;
            }
        }

        /// <summary>The hero's gait: his move mode while he moves (sneaking counts as walking), standing otherwise.</summary>
        EscortGait LeaderGait()
        {
            if (Leader == null || !Leader.TryGetComponent<G3Player>(out var hero)) return EscortGait.Run;
            if (Flat(hero.Velocity).sqrMagnitude < 0.3f * 0.3f) return EscortGait.Stand;
            switch (hero.MoveMode)
            {
                case G3MoveMode.Sprint: return EscortGait.Sprint;
                case G3MoveMode.Run: return EscortGait.Run;
                default: return EscortGait.Walk;
            }
        }

        void Face(Vector3 point)
        {
            var to = Flat(point - transform.position);
            if (to.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to), StationTurnSpeed * _dt);
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);

        string InteractionNow() => _simulation.InteractionOf(Agent);

        // ------------------------------------------------------------------------- sunk bodies (fix-163, BodySinkRules)

        Vector3 _stationSlot = new Vector3(float.NaN, 0f, 0f);
        float _stationY;
        static int s_sinkLogCount;
        static float s_sinkLogWindow = -100f;

        /// <summary>The station's interaction puts the body on furniture (bed, bench, stool, throne): its slot height is kept.</summary>
        bool OnFurniture() => G3.Player.BodySinkRules.OnFurniture(InteractionNow());

        /// <summary>
        /// The height a station NPC stands at: G3 puts it on the floor under its slot (PutToGround), ours placed it at the
        /// slot's authored height (owner, Vengard: a slot authored under a yard's floor showed the body sunk). Furniture
        /// keeps the slot's height. Looked up once per slot (the station pose changes rarely), so a standing NPC costs nothing.
        /// </summary>
        float StationHeight(Vector3 slot)
        {
            if (slot == _stationSlot) return _stationY;
            _stationSlot = slot;
            _stationY = slot.y;
            if (Agent != null && !OnFurniture() && NpcGround.TryStationFloor(slot, HalfLength, out float y)) _stationY = y;
            return _stationY;
        }

        /// <summary>fix-163 safety net: the lift for a body put at <paramref name="feet"/> whose feet are in the floor; logged, rate-limited.</summary>
        float SinkLift(Vector3 feet, string when)
        {
            float lift = NpcGround.SinkLift(feet, out string floor);
            if (lift > 0f && G3.Player.BodySinkRules.MayLog(Time.unscaledTime, ref s_sinkLogCount, ref s_sinkLogWindow))
                Debug.Log($"[npc] fix-163: {name} {when} {lift:F2} m in the floor ({floor}) at {feet}: lifted");
            return lift;
        }

        void SetInteraction(string interaction)
        {
            if (_animator == null) return;
            if (interaction != null) _animator.Direction = _simulation.DirectionOf(Agent);
            _animator.Interaction = interaction;
        }

        static Vector3 ToUnity(WorldPose p) => new Vector3(p.X, p.Y, p.Z);

        static WorldPose FromUnity(Transform t) =>
            new WorldPose(t.position.x, t.position.y, t.position.z, t.eulerAngles.y);
    }
}
