using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>What a fight watcher does in one step of <see cref="FightWatcher"/>.</summary>
    public enum WatchStep
    {
        /// <summary>Stands 5.5–6.5 m from the nearer fighter and faces it.</summary>
        Stand,
        /// <summary>Walks towards the nearer fighter (ZS_WatchFight_Goto) until within 6 m.</summary>
        Approach,
        /// <summary>Steps away from the nearer fighter (ZS_WatchFight_GoBack) until 6 m away.</summary>
        StepBack,
        /// <summary>The fighters ran off (the follow time ran out): stands and waits whether they come back (ZS_WatchFight_WaitForFlee).</summary>
        WaitForFlee,
        /// <summary>A fighter is down or dead: applauds the winner (ZS_Applaud), then watches him (ZS_ObserveSuspect).</summary>
        Applaud,
        /// <summary>The watch is over (fight gone farther than 20 m, or ended without a loser): back to the routine.</summary>
        Done,
        /// <summary>
        /// An arena fight's spectator walks to its free spectator point (ZS_WatchFight_Spectator: _AI_Goto_Ext to an
        /// interact point of use type Spectator), then stands there (<see cref="Stand"/>).
        /// </summary>
        GoToSpot,
    }

    /// <summary>
    /// G3 ZS_WatchFight for one bystander (CP 1.75 Script_Game, AI_AI_Tasks_Reaction_Tasks): every 0.1 s the watcher
    /// measures the nearer of the two fighters; closer than 550 → GoBack, farther than 650 → Goto (both stop at 600). The
    /// Goto gives up after the follow time (GetFollowTime, 5 s for humans) and waits (WaitForFlee): every 1 s, beyond
    /// 2000 the watch ends, a fighter that came closer again resumes the watch. OnWatchFight: a fighter down (AIMode Down)
    /// or dead → the watcher targets the winner and applauds (ZS_Applaud: overlay "Applaud" at 0.9–1.1 speed when the
    /// hands are empty, then ZS_ObserveSuspect on him). Metres here. Pure; the presentation walks the body.
    /// <para>
    /// An arena fight (either fighter in AIMode Arena or fighting with the reason Arena) is watched as a spectator
    /// instead (ZS_WatchFight → ZS_WatchFight_Spectator, fix-85): the watcher claims a free spectator point and walks to
    /// it, or without one stays where it is; ZS_WatchFight_Spectator_Loop then only turns to the nearer fighter every
    /// 0.1 s. It never closes in nor backs off and never waits for a fled fight, so nobody walks into the ring.
    /// </para>
    /// </summary>
    public sealed class FightWatcher
    {
        public const float KeepMin = 5.5f, KeepMax = 6.5f, StopAt = 6f, GiveUpBeyond = 20f;
        /// <summary>GetFollowTime for a human watcher (s).</summary>
        public const float FollowSeconds = 5f;
        /// <summary>Loop and wait cadences (s).</summary>
        public const float LoopSeconds = 0.1f, WaitSeconds = 1f;
        /// <summary>
        /// A fight without a further hit for this long counts as over (s) [interp: G3 keeps watching until a fighter goes
        /// down or flees; the hit broadcasts renew the watch here].
        /// </summary>
        public const float QuietSeconds = 30f;
        /// <summary>The applause and the observing after it (s) [interp: ZS_ObserveSuspect's task time 4 s].</summary>
        public const float ApplaudSeconds = 5f;

        float _stateTime, _quiet, _lastDistance = float.MaxValue, _applaudLeft;

        public FightWatcher(ActorId a, ActorId b, bool spectator = false, bool toSpot = false)
        {
            A = a;
            B = b;
            Spectator = spectator;
            Step = spectator && toSpot ? WatchStep.GoToSpot : WatchStep.Stand;
        }

        public ActorId A { get; }
        public ActorId B { get; }
        /// <summary>An arena fight: ZS_WatchFight_Spectator (no distance keeping).</summary>
        public bool Spectator { get; }

        /// <summary>The spectator reached its point (or cannot reach it): it watches from there.</summary>
        public void AtSpot()
        {
            if (Step == WatchStep.GoToSpot) Enter(WatchStep.Stand);
        }
        public WatchStep Step { get; private set; }
        /// <summary>The fighter the watcher faces or walks to (the nearer one), or the winner while applauding.</summary>
        public ActorId Focus { get; private set; }

        /// <summary>Another hit of this fight was heard: the watch goes on.</summary>
        public void Renew() => _quiet = 0;

        /// <summary>
        /// One frame. <paramref name="toA"/>/<paramref name="toB"/>: distances (m) to the fighters, NaN when one is gone;
        /// <paramref name="aDown"/>/<paramref name="bDown"/>: knocked out or dead.
        /// </summary>
        public WatchStep Update(float dt, float toA, float toB, bool aDown, bool bDown)
        {
            if (Step == WatchStep.Done) return Step;
            if (Step == WatchStep.Applaud)
            {
                if ((_applaudLeft -= dt) <= 0) Step = WatchStep.Done;
                return Step;
            }
            if (aDown != bDown)
            {
                Focus = aDown ? B : A;
                Step = WatchStep.Applaud;
                _applaudLeft = ApplaudSeconds;
                return Step;
            }
            if ((_quiet += dt) > QuietSeconds || (aDown && bDown) || (float.IsNaN(toA) && float.IsNaN(toB)))
            {
                Step = WatchStep.Done;
                return Step;
            }
            bool aNearer = float.IsNaN(toB) || (!float.IsNaN(toA) && toA <= toB);
            float d = aNearer ? toA : toB;
            Focus = aNearer ? A : B;
            _stateTime += dt;
            if (Spectator) return Step; // ZS_WatchFight_Spectator(_Loop): GoToSpot until AtSpot, then Stand facing Focus
            switch (Step)
            {
                case WatchStep.Approach:
                    if (d < StopAt) Enter(WatchStep.Stand);
                    else if (_stateTime > FollowSeconds) { Enter(WatchStep.WaitForFlee); _lastDistance = d; }
                    break;
                case WatchStep.StepBack:
                    if (d >= StopAt) Enter(WatchStep.Stand);
                    break;
                case WatchStep.WaitForFlee:
                    if (_stateTime <= WaitSeconds) break;
                    _stateTime = 0;
                    if (d > GiveUpBeyond) Step = WatchStep.Done;
                    else if (d < _lastDistance) Enter(WatchStep.Stand);
                    else _lastDistance = d;
                    break;
                default:
                    if (_stateTime <= LoopSeconds) break;
                    if (d < KeepMin) Enter(WatchStep.StepBack);
                    else if (d > KeepMax) Enter(WatchStep.Approach);
                    else _stateTime = 0;
                    break;
            }
            return Step;
        }

        void Enter(WatchStep step)
        {
            Step = step;
            _stateTime = 0;
        }
    }

    /// <summary>Where an arena fight's watcher goes (ZS_WatchFight / ZS_WatchFight_Spectator, CP 1.75 Script_Game).</summary>
    public static class SpectatorRules
    {
        /// <summary>The use type of spectator points (gEUseType 93).</summary>
        public const string SpectatorInteraction = "Spectator";

        /// <summary>
        /// Range of the search for a watcher without a routine anchor (G3 takes the interact points of its zone) [interp].
        /// </summary>
        public const float SearchRadius = 20f;

        /// <summary>
        /// ZS_WatchFight: either fighter is in AIMode Arena (waiting in the ring, ZS_Arena) or fights with AttackReason
        /// Arena (0x12) → ZS_WatchFight_Spectator, else ZS_WatchFight_Loop.
        /// </summary>
        public static bool IsArenaFight(bool aInArena, AttackReason? aReason, bool bInArena, AttackReason? bReason) =>
            aInArena || bInArena || aReason == AttackReason.Arena || bReason == AttackReason.Arena;

        /// <summary>
        /// The free spectator point of <paramref name="watcher"/> (FUN_10008190(93) → FUN_10007c90: the interact points
        /// of the anchor of its routine point, or, without one, those around it; IsFreeDestination): the nearest free slot
        /// of a "Spectator" point, or <see cref="Station.None"/> (CP 1.75 places a single Spectator point, at no arena, so
        /// arena watchers normally stay where they are).
        /// </summary>
        public static Station FindPoint(RoutinePointCatalog points, StationClaims claims, ActorId watcher, Station current, WorldPose here)
        {
            Id<AnchorDef> anchor = points.TryGetPoint(current.Point, out var cur) ? cur.Anchor : default;
            var best = Station.None;
            float bestDistance = float.MaxValue;
            IReadOnlyList<RoutinePointDef> candidates = points.PointsWith(SpectatorInteraction);
            for (int i = 0; i < candidates.Count; i++)
            {
                var p = candidates[i];
                if (!anchor.IsEmpty && p.Anchor != anchor) continue;
                for (int slot = 0; slot < p.Slots.Count; slot++)
                {
                    float d = p.Slots[slot].DistanceTo(here);
                    if (anchor.IsEmpty && d > SearchRadius) continue;
                    var station = new Station(p.Id, slot);
                    if (claims != null && !claims.IsFreeFor(station, watcher, current)) continue;
                    if (d < bestDistance) { bestDistance = d; best = station; }
                }
            }
            return best;
        }
    }

    /// <summary>
    /// G3 CheerFight (called from SendPerceptionFightSound for every hit, CP 1.75): a humanoid watcher (AIMode
    /// Reaction, with the "Watchfight" SVM block) whose current or alternative target is the victim comments the hit,
    /// behind the global say gate (0.0018 game h since the last line). A global counter runs 0, 1, 2: a SitKnockDown or
    /// PierceStumble gives "HeavyHit" at counter 0 and 1; a parade stumble, heavy parade stumble, flinch/quick stumble or
    /// stumble gives "Ooh_0n" when the watcher is a friend of the victim and not of the attacker, else "Cheer_0n";
    /// other reactions (and a heavy hit at counter 2) say nothing.
    /// </summary>
    public sealed class FightCheers
    {
        int _counter;

        /// <summary>The line for a hit of <paramref name="reaction"/>; advances the counter (null = silent).</summary>
        public string LineFor(HitReaction reaction, Attitude toVictim, Attitude toAttacker)
        {
            _counter = (_counter + 1) % 3;
            bool heavy = reaction == HitReaction.SitKnockDown || reaction == HitReaction.PierceStumble;
            if (heavy && _counter < 2) return "HeavyHit";
            switch (reaction)
            {
                case HitReaction.ParadeStumble:
                case HitReaction.HeavyParadeStumble:
                case HitReaction.Flinch:
                case HitReaction.QuickStumble:
                case HitReaction.Stumble:
                    bool ooh = toVictim == Attitude.Friendly && toAttacker != Attitude.Friendly;
                    return (ooh ? "Ooh_0" : "Cheer_0") + _counter;
                default:
                    return null;
            }
        }
    }

    /// <summary>Lines around knock-outs and fights (Script_Game).</summary>
    public static class KnockOutLines
    {
        /// <summary>
        /// ZS_Unconscious_End: GetRandomNumber(100) &gt; 49 and the attacker not Friendly → "NextTimeYoureInForIt", else
        /// "OhMyHead" (then ZS_FetchWeapon).
        /// </summary>
        public static string GetUpLine(int random100, Attitude toAttacker) =>
            random100 > 49 && toAttacker != Attitude.Friendly ? "NextTimeYoureInForIt" : "OhMyHead";

        /// <summary>ZS_WatchFight's first line, when both fighters are humanoid and neither flees.</summary>
        public const string ThereIsAFight = "ThereIsAFight";
    }
}
