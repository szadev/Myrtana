using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The active perception of the NPCs the player can meet (Docs/perception-crime.md §2.2–§2.4, §4.5): reports the
    /// player's pose every frame (zone entry), supplies raycast line of sight, and hands the poses of the shown NPCs to the
    /// engine-free <see cref="PerceptionScanner"/>, which lets every Active NPC scan at the G3 rate (1 s; gate guards 0.1 s)
    /// for the player and the NPCs around it and runs the steps that follow (crime, fights, flight, sleepers, eaters).
    /// <para>Performance (perf-9): one pass over the shown views per frame (one GetPositionAndRotation each, no iterator, no
    /// GetComponent); the scanner's own budget and update-LOD rules are in <see cref="PerceptionScanner"/>. Decides nothing
    /// itself.</para>
    /// </summary>
    public sealed class PerceptionDriver : MonoBehaviour, ISessionView
    {
        /// <summary>Moving faster than this (m/s) makes noise.</summary>
        const float MovingSpeed = 0.5f;
        /// <summary>The sneaking hero's top above his feet, m (the sight rays aim at his head and chest below it).</summary>
        const float SneakingTop = 1.25f;
        /// <summary>Most NPC scans per frame (the rest wait a frame; nearest first) [perf].</summary>
        public const int ScansPerFrame = PerceptionScanner.ScansPerFrame;
        /// <summary>Real seconds a renewed observation keeps the woken sleeper facing the player.</summary>
        const float WakerWatchSeconds = 5f;
        public const string SneakPerk2 = PerceptionScanner.SneakPerk2;

        readonly RaycastLineOfSight _lineOfSight = new RaycastLineOfSight();
        readonly List<PerceptionScanner.ViewInput> _inputs = new List<PerceptionScanner.ViewInput>();
        NpcViewManager _npcs;
        G3Player _player;
        PlayerCombat _combat;
        GameSession _session;
        PerceptionScanner _scanner;

        public void Init(NpcViewManager npcs, G3Player player, PlayerCombat combat)
        {
            _npcs = npcs;
            _player = player;
            _combat = combat;
        }

        public void Bind(GameSession session)
        {
            _session = session;
            _scanner = new PerceptionScanner(session, () => UnityEngine.Random.value)
            {
                LineOfSight = _lineOfSight,
                WatchPlayer = WatchPlayer,
            };
            session.Crime.World.LineOfSight = _lineOfSight;
        }

        void Update()
        {
            if (_session == null || _player == null || !_player.IsActive || LoadingHold.IsHeld || GamePause.IsPaused) return; // nobody sees behind a loading screen or a pause
            var t = _player.transform;
            var pose = new WorldPose(t.position.x, t.position.y, t.position.z, t.eulerAngles.y);
            var crime = _session.Crime;
            bool ghost = _session.SpellEffects.Hero.IsGhost; // soul travel: NPCs never perceive the ghost; the body's pose stays (magic-3)
            if (!ghost) crime.ReportPlayer(pose);
            if (_session.Dialog.IsActive) return;
            bool rangedState = _combat != null && _combat.Fighter != null && _combat.Fighter.RangedDrawn;
            var player = new PerceptionScanner.PlayerInput
            {
                Target = new SenseTarget
                {
                    Id = ActorId.Player,
                    Pose = pose,
                    Moving = _player.Velocity.sqrMagnitude > MovingSpeed * MovingSpeed,
                    Sneaking = _player.Sneaking,
                    Parade = _combat != null && _combat.Fighter != null && _combat.Fighter.IsParrying,
                    // CP: with Perk_Sneak_2 animals do not notice the hero beyond 1000 while he is in a ranged state.
                    HiddenFromAnimals = rangedState && _session.Hero.Knows(SneakPerk2),
                    Undetectable = _player.Swimming,
                    IndoorZone = crime.PlayerZone.Indoor ? crime.PlayerZone.Id : null,
                    Height = _player.Sneaking ? SneakingTop : SightLines.HumanTop, // fix-204: a sneaking hero ducks behind low cover
                },
                Perceivable = !ghost && _session.Combat.Vitals.StateOf(ActorId.Player) == VitalState.Alive,
                RangedState = rangedState,
                Conduct = new PlayerConduct
                {
                    WeaponDrawn = WeaponDrawn(), SpellDrawn = _combat != null && _combat.Fighter != null && _combat.Fighter.SpellDrawn,
                    Sneaking = _player.Sneaking, Zone = crime.PlayerZone,
                    Falling = !_player.IsGrounded && _player.FallTime > 0, Swimming = _player.Swimming,
                },
            };
            using (s_collectMarker.Auto()) CollectViews();
            using (s_frameMarker.Auto()) _scanner.Frame(player, _inputs, _session.PlaySeconds);
        }

        static readonly global::Unity.Profiling.ProfilerMarker s_collectMarker = new global::Unity.Profiling.ProfilerMarker("Perception.Collect"),
            s_frameMarker = new global::Unity.Profiling.ProfilerMarker("Perception.Frame");

        /// <summary>The shown NPCs' feet poses (yaw = the body's eulerAngles.y) and update LOD, in the view table's order.</summary>
        void CollectViews()
        {
            _inputs.Clear();
            // mt-1 C (-g3mt bit 1): every shown body's pose in one Burst job at this point of the frame (same values)
            bool snapshot = G3Mt.On(MtBits.BodySnapshot);
            if (snapshot) NpcView.Snapshot.Read(true);
            bool verify = snapshot && G3Mt.Verifying;
            foreach (var kv in _npcs.ViewComponents)
            {
                var view = kv.Value;
                if (view == null) continue;
                Vector3 p;
                Quaternion q;
                if (!snapshot || !view.TrySnapshotPose(out p, out q)) view.Body.GetPositionAndRotation(out p, out q);
                else if (verify)
                {
                    view.Body.GetPositionAndRotation(out var lp, out var lq);
                    G3Mt.Check(G3Mt.Same(p, lp) && G3Mt.Same(q, lq), "perception", $"{kv.Key} perception pose {p:R}/{lp:R} {q:R}/{lq:R}");
                }
                _inputs.Add(new PerceptionScanner.ViewInput(kv.Key, new WorldPose(p.x, p.y, p.z, q.eulerAngles.y), view.UpdateInterval, view.BodyTaken,
                                                         view.BodyTop));
            }
        }

        /// <summary>A woken sleeper keeps observing: its view faces the player.</summary>
        void WatchPlayer(ActorId npc)
        {
            if (_npcs.TryGetViewComponent(npc, out var view)) view.Watch(_player.transform, WakerWatchSeconds);
        }

        /// <summary>
        /// A dangerous weapon in the hand (G3 HasDangerousWeapon): a melee weapon other than the fists, a ranged weapon, or a
        /// spell of a deity that is not one of the harmless ones (light, cures, animal form).
        /// </summary>
        bool WeaponDrawn()
        {
            var fighter = _combat != null ? _combat.Fighter : null;
            if (fighter == null || fighter.Animator == null || !fighter.Animator.Drawn) return false;
            if (fighter.SpellDrawn)
                return _session.Magic.TryGetSpell(_combat.ReadiedSpell, out var spell) && CrimeSystem.IsDangerousSpell(spell);
            return fighter.WeaponClass != WeaponClass.Fist || fighter.RangedDrawn;
        }
    }
}
