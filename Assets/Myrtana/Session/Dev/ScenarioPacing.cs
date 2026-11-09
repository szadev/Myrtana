#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;

namespace Myrtana.Session.Dev
{
    /// <summary>What the dev runner does about a hero sitting in the sit knock-down (fix-41).</summary>
    public enum GetUpAction { None, Press, Force }

    /// <summary>
    /// Pure timing rules of the dev scenario runner (fix-41, editor-runner-28): when the runner presses the get-up again,
    /// how long a <c>draw</c> or <c>fight</c> may take while the hero is knocked about, and how much paused time the
    /// scenario-level timeout grants. None of this is game logic: G3's player sits until a press (ZS_SitKnockDown_Loop).
    /// </summary>
    public static class ScenarioPacing
    {
        /// <summary>A get-up press that did not get the hero up is repeated after this, s.</summary>
        public const float GetUpRepressSeconds = 1.5f;
        /// <summary>After this many presses without getting up, the dev get-up (<c>getup</c> op) frees him.</summary>
        public const int GetUpPressesBeforeForce = 4;

        /// <summary>
        /// The runner's get-up for a sitting hero: the first press at once (a press during the fall waits for the sit, G3
        /// queue); while he still sits and no get-up is pending, a new press every <see cref="GetUpRepressSeconds"/>; after
        /// <see cref="GetUpPressesBeforeForce"/> presses the dev get-up. r28 (vangard_01, hammerclan_01/02/04): one press per
        /// sit, lost to a draw cut by the fall; the hero sat 20-249 s with empty hands.
        /// </summary>
        /// <param name="newSit">A sit the runner has not pressed for yet (not the scenario's own 'sit').</param>
        /// <param name="pending">A get-up is queued (the fall still plays) or the get-up motion runs.</param>
        public static GetUpAction NextGetUp(bool sitting, bool newSit, bool pending, float sinceLastPress, int presses)
        {
            if (!sitting) return GetUpAction.None;
            if (newSit) return GetUpAction.Press;
            if (pending || sinceLastPress < GetUpRepressSeconds) return GetUpAction.None;
            return presses >= GetUpPressesBeforeForce ? GetUpAction.Force : GetUpAction.Press;
        }

        /// <summary>
        /// fix-64 (r31 hammerclan_02 [shield]: a BlackGoblin sat the hero 15 times in 22 s, each get-up attack ran into its
        /// next hit): the press the runner gets the hero up with. RMB (GetUpParade, DefenseLevel 2) when the block is held or
        /// a shield is in the left hand, as G3's NPCs answer an attacker (ZS_SitKnockDown); else LMB (GetUpAttack).
        /// </summary>
        public static bool GetUpWithParade(bool blockHeld, bool shieldInHand) => blockHeld || shieldInHand;

        /// <summary>
        /// fix-64: a down seen again after more than this unseen (he got up, or a defend fight ran in between: r31
        /// hammerclan_02 failed "stays down 23 s" right after a 22 s defend in which he sat and got up 15 times) is a new one.
        /// </summary>
        public const float DownGapSeconds = 0.5f;

        /// <summary>When the current down spell began: <paramref name="downSince"/> while it goes on, else <paramref name="now"/>.</summary>
        /// <param name="downSince">Start of the spell so far (negative: none).</param>
        /// <param name="lastSeenDown">When the hero was last seen down (negative: never).</param>
        public static float DownStart(float downSince, float lastSeenDown, float now) =>
            downSince >= 0f && lastSeenDown >= 0f && now - lastSeenDown <= DownGapSeconds ? downSince : now;

        /// <summary>Where the tip of a one-handed blade reaches in the hero's swings, m ahead of his feet.</summary>
        public const float BladeReach = 1.4f;
        /// <summary>The fight op's swing distance for large targets, m (as before fix-64).</summary>
        public const float MaxMeleeReach = 2.4f;
        /// <summary>The fight op walks on until this much inside its swing distance, m.</summary>
        public const float MeleeStopInside = 0.4f;

        /// <summary>
        /// fix-64 (r31 melee_small [dual]: a meatbug, 8 swings, touched 0): the distance the fight op swings from, by the
        /// target's weapon trigger (<paramref name="targetHalf"/> = its smaller half width, m). The fixed 2.4 m (stop at 2.0)
        /// left a meatbug (±0.25 m) beyond the blade's 1.4 m; large targets keep 2.4 m.
        /// </summary>
        public static float MeleeReach(float targetHalf) =>
            Clamp(BladeReach + Math.Max(0f, targetHalf), BladeReach, MaxMeleeReach);

        /// <summary>The distance the fight op closes to before it stands and swings, m.</summary>
        public static float MeleeStop(float targetHalf) => MeleeReach(targetHalf) - MeleeStopInside;

        /// <summary>The draw op's own budget, s (as before fix-41).</summary>
        public const float DrawSeconds = 12f;
        /// <summary>The longest the draw op waits for the hero to get up and recover in all, s.</summary>
        public const float DrawWaitDownSeconds = 30f;

        /// <summary>
        /// Time the draw op may still run: its own <see cref="DrawSeconds"/> plus the time the hero spent knocked down, in a
        /// hit reaction or getting up (up to <see cref="DrawWaitDownSeconds"/>): a draw waits for the get-up and recovery
        /// instead of failing (r28 hammerclan_02/04, vangard_01: "draw: weapon not drawn ... (Down, hand empty None)").
        /// </summary>
        /// <summary>Draws cut by hits before the runner freezes the hero's attackers (fix-48, r29 hammerclan_04 Ripper stun-lock).</summary>
        public const int DrawCutsBeforeFreeze = 3;

        /// <summary>How long those attackers stand frozen, s (G3's shortest freeze: max(5, damage/20) s).</summary>
        public const float AttackerFreezeSeconds = 5f;

        public static float DrawLimit(float knockedAboutSeconds, float frozenSeconds = 0f) =>
            DrawSeconds + Clamp(knockedAboutSeconds, 0f, DrawWaitDownSeconds) + Clamp(frozenSeconds, 0f, DrawWaitFrozenSeconds);

        /// <summary>
        /// fix-54 (r30 wolfclan_01, an IceGolem's ice): time a draw waits for the hero to thaw, s. G3 refuses every action while
        /// he is frozen (ZS_Freeze, max(5, damage/20) s per hit, renewed by the next hit), so the draw waits instead of using
        /// up its tries.
        /// </summary>
        public const float DrawWaitFrozenSeconds = 60f;

        /// <summary>
        /// Time a fight op may run: its <c>secs</c> plus the time the hero spent in hit reactions, knocked down or getting up,
        /// up to <c>secs</c> again. r28 ch09/ch13: Gotha_Demon_01, LivingAncestor_01 and Rhobar (D &gt; 3, G3 AssessHit) kept
        /// the hero in Stumble/SitKnockDown 19-26 of the 30 s.
        /// </summary>
        public static float FightLimit(float secs, float staggeredSeconds) => secs + Clamp(staggeredSeconds, 0f, Math.Max(0f, secs));

        /// <summary>
        /// The longest the draw op waits for the hero to stand on the ground and not slide, s (fix-53). G3 has no ground
        /// rule for drawing (OnPlayerSelectWeapon refuses only by the player routine and the ani state; PlayerCombat's
        /// DrawRefusal is the game's rule): the wait only lets a landing or a short slide after a tp finish. r30
        /// hammerclan_04 spent the whole draw budget on "not grounded" for a hero whose controller a dialog lock had
        /// switched off right after a teleport.
        /// </summary>
        public const float DrawGroundGraceSeconds = 2f;

        /// <summary>
        /// The draw op may try now: no combat motion runs, and the hero stands (grounded, not sliding) or has not stood for
        /// <see cref="DrawGroundGraceSeconds"/>.
        /// </summary>
        /// <param name="unsteadySeconds">How long the hero has been off the ground or sliding during this draw op.</param>
        public static bool DrawReady(bool grounded, bool sliding, bool busy, float unsteadySeconds) =>
            !busy && (grounded && !sliding || unsteadySeconds >= DrawGroundGraceSeconds);

        /// <summary>
        /// The fight op's hp clamp holds for the whole fight: the target's hit points rose above the clamp without a heal of
        /// the fight (G3 RefreshHitPoints when its cell streamed in and it entered the processing range: r28 Gotha_Demon_01
        /// 5 -> 1000 0.4 s after "Loaded scene Cell_15_20"). True = clamp again.
        /// </summary>
        public static bool Reclamp(int hitPoints, int clamp) => clamp > 0 && hitPoints > clamp;

        /// <summary>
        /// The scenario timeout grows by the time the steps paused their own clocks (escort defends, the guide's fights, the
        /// hero down) and by the step time they added (an escort's G3 route, a fight's stagger), up to
        /// <paramref name="maxFactor"/> x the scenario's timeout. r28 nemora_02: "clock paused 702 s of 420 s", the 900 s
        /// timeout ran out at step 51/106 although every step was within its own limit.
        /// </summary>
        public static bool TimedOut(float elapsed, float timeout, float credit, float maxFactor = 3f) =>
            elapsed > timeout + Clamp(credit, 0f, Math.Max(0f, timeout * (maxFactor - 1f)));

        /// <summary>Hostiles fighting the hero (or the escorted NPC) within this, m, keep the weapon drawn after a fight.</summary>
        public const float KeepDrawnRadius = 15f;

        /// <summary>Ops that need the hero's hands empty (or lead away from the fight): a fight before one of them sheathes.</summary>
        public static readonly string[] EmptyHandOps =
            { "talk", "ask", "use", "loot", "pickup", "eat", "sit", "rob", "usenpc", "tp", "escort", "sneak", "save", "load", "sheathe", "drawtrace" };

        /// <summary>
        /// After a fight op (owner, fix-51): a player keeps the weapon drawn while foes are still about instead of sheathing
        /// and drawing again for every one of them. True = sheathe: <paramref name="sheathe"/> "1" forces it and "0" forbids it
        /// (explicit overrides); otherwise sheathe when no hostile that attacks the hero or the escorted NPC is within
        /// <see cref="KeepDrawnRadius"/> (<paramref name="hostilesNear"/> = 0), or when <paramref name="nextOp"/> needs empty hands.
        /// </summary>
        public static bool SheatheAfterFight(string sheathe, int hostilesNear, string nextOp)
        {
            if (sheathe == "0" || string.Equals(sheathe, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (sheathe == "1" || string.Equals(sheathe, "true", StringComparison.OrdinalIgnoreCase)) return true;
            return hostilesNear <= 0 || NeedsEmptyHands(nextOp);
        }

        /// <summary>
        /// fix-58 (runner-31 sideq_okara_01): an escort reaches its target only when the escorted NPC stands within
        /// <paramref name="near"/> of it and, for a guide, is out of every fight. G3's guide fights what attacks it wherever it is
        /// (OnGuidePlayer: AssessAgressor -> ZS_Attack) and goes back to its destination afterwards (ZS_GuidePlayer); a player
        /// keeps with it until it stands there. Fraser passed 4.6 m from Okara_FraserHuntCheckPoint while the boars of the hunt
        /// attacked him: the step ended, the fight drew him 45 m off and the delivery info (Fraser within 5 m) never came.
        /// </summary>
        /// <param name="guide">The NPC leads (FollowNpc quest), not a follower the hero brings.</param>
        /// <param name="fighting">The NPC's own fight runs, or something attacks it.</param>
        public static bool EscortArrived(float flatToTarget, float near, bool guide, bool fighting) =>
            flatToTarget <= near && !(guide && fighting);

        /// <summary>fix-65: a fight keeps an arrived guide "fighting" only within this (m, 3D) of the guide.</summary>
        public const float EscortFightRadius = 15f;

        /// <summary>fix-65: ... and only within this height difference (m) of the guide.</summary>
        public const float EscortFightBand = 5f;

        /// <summary>
        /// fix-65 (r32 sideq_okara_01, R32-1): whether a fight counts for <see cref="EscortArrived"/>. Only a foe that
        /// targets the guide or the hero and stands near the guide (<see cref="EscortFightRadius"/> in 3D,
        /// <see cref="EscortFightBand"/> up or down) is a fight at the target; the guide's own fight counts the same way
        /// by where its target is. Okara_SlaveZombie_01 "attacked" Rakus from 137.8 m below (no swing, the hero had given
        /// up on it as out of reach): the escort waited at the target until the step timed out. <paramref name="dx"/>,
        /// <paramref name="dy"/>, <paramref name="dz"/> = foe minus guide, m.
        /// </summary>
        public static bool EscortFightNear(bool targetsGuideOrHero, float dx, float dy, float dz) =>
            targetsGuideOrHero && Math.Abs(dy) <= EscortFightBand && dx * dx + dy * dy + dz * dz <= EscortFightRadius * EscortFightRadius;

        /// <summary>
        /// fix-57 (r31 hammerclan_04 bow: "fight: sheathe ... (3-4 tries)" 14 times between the defends of one wolf pack): a
        /// defend inside an escort never sheathes by itself — the next wolf attacks the guide a moment later. The escort puts
        /// the weapon away once nothing has attacked the hero or the guide for <see cref="EscortClearSeconds"/>
        /// (<see cref="EscortSheathe"/>). Explicit <c>sheathe=</c> still wins; outside an escort as
        /// <see cref="SheatheAfterFight(string,int,string)"/>.
        /// </summary>
        public static bool SheatheAfterFight(string sheathe, int hostilesNear, string nextOp, bool inEscort)
        {
            if (inEscort && sheathe != "1" && !string.Equals(sheathe, "true", StringComparison.OrdinalIgnoreCase)) return false;
            return SheatheAfterFight(sheathe, hostilesNear, nextOp);
        }

        /// <summary>fix-57: the radius (m) of <see cref="SheatheAfterFight(string,int,string)"/>'s "hostiles near" for an attack
        /// style: a bow, a crossbow or a spell fights from up to 20 m, so foes within 25 m keep it drawn; melee 15 m.</summary>
        public static float KeepDrawnRadiusFor(AttackStyle style) =>
            style == AttackStyle.Bow || style == AttackStyle.Crossbow || style == AttackStyle.Spell ? RangedKeepDrawnRadius : KeepDrawnRadius;

        public const float RangedKeepDrawnRadius = 25f;

        /// <summary>An escort sheathes a drawn weapon after this long without a foe attacking the hero or the guide nearby, s.</summary>
        public const float EscortClearSeconds = 8f;

        /// <summary>fix-57: the escort puts the drawn weapon away (a drawn weapon makes NPCs around react, editor-runner-24).</summary>
        public static bool EscortSheathe(bool drawn, float clearSeconds) => drawn && clearSeconds >= EscortClearSeconds;

        /// <summary>
        /// fix-57: a foe this close (flat, m) is fought in melee by a bow or crossbow fighter that carries a melee weapon.
        /// G3 has no melee with a bow in the hands (PS_Ranged: aim and shoot only) and no automatic switch; a player presses
        /// the melee weapon's quick-slot key (OnPlayerUseQuickSlot: the bow is put away, the weapon drawn) when a wolf is at
        /// his feet — the runner does the same (r31: Mort died to wolves biting him while the hero aimed at 1-3 m).
        /// </summary>
        public const float MeleeSwitchDistance = 4f;

        public static bool SwitchToMelee(AttackStyle style, float distance, bool hasMeleeWeapon) =>
            (style == AttackStyle.Bow || style == AttackStyle.Crossbow) && hasMeleeWeapon && distance <= MeleeSwitchDistance;

        /// <summary>
        /// fix-57 (r31 hammerclan_04: "hits=0 lost=5" in every bow fight): hits of a distance fight. The kill shot ends the
        /// fight loop before its next hit point check, so the loop's count misses it; the resolved hits (HitResolved from the
        /// hero on the target) and the hit points lost count too.
        /// </summary>
        public static int RangedHits(int hitPointDrops, int resolvedHits, int lost) =>
            Math.Max(Math.Max(hitPointDrops, resolvedHits), lost > 0 ? 1 : 0);

        /// <summary>What the sheathe op does this frame.</summary>
        public enum SheatheAct { Wait, Try }

        /// <summary>Sheathe presses that are refused although nothing holds the hands before the op gives up pressing.</summary>
        public const int SheatheTries = 4;

        /// <summary>
        /// fix-57 (r31 crossbow: "sheathe ... refused: aiming, shooting, reloading or casting" after 4 presses in 0.8 s, the
        /// bolt was still being loaded; draw_aim_strafe: "drawtrace: sheathe refused" 0.5 s after a bow draw, the arrow was
        /// being nocked): a weapon key while a motion, the aim, a shot, the reload or a cast runs does not act at once (fix-101:
        /// G3 queues it for the next loop state, PlayerCombat.OfferSwitch). The op waits for those without using up its presses.
        /// </summary>
        public static SheatheAct NextSheathe(bool drawn, bool busy, bool aimShotReloadOrCast, int tries) =>
            !drawn || busy || aimShotReloadOrCast || tries >= SheatheTries ? SheatheAct.Wait : SheatheAct.Try;

        public static bool NeedsEmptyHands(string op) => op != null && Array.IndexOf(EmptyHandOps, op.ToLowerInvariant()) >= 0;

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// fix-57 (r31 hammerclan_02: "teleport #1 ... -> (500.0,185.3,1334.9) next to Mort", Mort 30 m up a ledge; the hero
    /// stood at y 153.8 a moment later and got stuck 6 times): where the escort may put the hero next to a guide. A spot is
    /// on the NavMesh at about the guide's height, with ground under it, away from the NavMesh edge (a ledge rim drops the
    /// hero's capsule) and joined to the guide by a complete NavMesh path; the first candidates lie on the guide's own path
    /// behind him (the way he came, which the hero follows anyway).
    /// </summary>
    public static class GuideSpots
    {
        /// <summary>Height difference to the guide a spot may have, m.</summary>
        public const float MaxRise = 2f;
        /// <summary>Flat distance to the guide a spot may have, m.</summary>
        public const float MaxFlat = 8f;
        /// <summary>A spot this close to the hero is no teleport, m.</summary>
        public const float MinFromHero = 1.5f;
        /// <summary>The physics ground under the NavMesh point must be this close to it, m.</summary>
        public const float GroundTolerance = 0.6f;
        /// <summary>Distance to the NavMesh edge a spot needs, m (the hero's radius 0.4 plus a margin).</summary>
        public const float MinEdge = 0.6f;
        /// <summary>A hero this much below the spot shortly after the teleport fell off it, m.</summary>
        public const float FellBelow = 3f;
        /// <summary>Path distances behind the guide along his own path to try, m.</summary>
        public static readonly float[] TrailDistances = { 3f, 5f, 8f };

        /// <summary>
        /// The point <paramref name="distance"/> m back along <paramref name="trail"/> from its last point (the guide), or
        /// false when the trail is shorter.
        /// </summary>
        public static bool BehindOnTrail(System.Collections.Generic.IReadOnlyList<HeroRoutePoint> trail, float distance, out HeroRoutePoint at)
        {
            at = default;
            if (trail == null || trail.Count < 2 || distance <= 0f) return false;
            float left = distance;
            for (int i = trail.Count - 1; i > 0; i--)
            {
                var a = trail[i];
                var b = trail[i - 1];
                float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                float seg = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (seg >= left && seg > 1e-4f)
                {
                    float k = left / seg;
                    at = new HeroRoutePoint(a.X + dx * k, a.Y + dy * k, a.Z + dz * k);
                    return true;
                }
                left -= seg;
            }
            return false;
        }

        /// <summary>
        /// Why <paramref name="spot"/> is no place for the hero next to <paramref name="guide"/>, or null when it is one.
        /// </summary>
        /// <param name="groundY">Height of the physics ground under the spot (null = none found).</param>
        /// <param name="edgeDistance">Distance to the NavMesh edge, m.</param>
        /// <param name="reachable">A complete NavMesh path joins the spot and the guide.</param>
        public static string Reject(HeroRoutePoint spot, HeroRoutePoint guide, HeroRoutePoint hero, float? groundY, float edgeDistance, bool reachable)
        {
            float rise = Math.Abs(spot.Y - guide.Y);
            if (rise > MaxRise) return F($"{rise:0.0} m above/below the guide");
            float flat = Flat(spot, guide);
            if (flat > MaxFlat) return F($"{flat:0.0} m from the guide");
            if (Flat(spot, hero) < MinFromHero && Math.Abs(spot.Y - hero.Y) < MaxRise) return "where the hero stands";
            if (groundY == null) return "no ground under it";
            if (Math.Abs(groundY.Value - spot.Y) > GroundTolerance) return F($"ground {groundY.Value - spot.Y:0.0} m off the NavMesh");
            if (edgeDistance < MinEdge) return F($"{edgeDistance:0.0} m from the NavMesh edge");
            if (!reachable) return "no NavMesh path to the guide";
            return null;
        }

        /// <summary>The hero fell off the spot (checked a moment after the teleport).</summary>
        public static bool Fell(float spotY, float heroY) => spotY - heroY > FellBelow;

        static float Flat(HeroRoutePoint a, HeroRoutePoint b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        static string F(FormattableString s) => FormattableString.Invariant(s);
    }

    /// <summary>
    /// The dev runner's watch over an escorted NPC (fix-46, editor-runner-28): who hit it last (the escort's death line named
    /// "attacked by nobody" when the killer had died in the same exchange or was already back to idle: vangard_01 Georg,
    /// main_02 Xardas), and the runner's top-up of its hit points. G3 does not protect a guide (CanBeKilled: only the CP
    /// Immortal property, used by Lardo's Humanize, and Friend-type NPCs below player level 3 on Easy/Medium; IsDeadlyDamage:
    /// Xardas and bosses die, a hostile orc kills Georg); a player clears the orc camp before walking Georg through it, the
    /// runner's hero (one foe at a time, given up after "no hit landed") cannot. So the runner tops up a guide that is low:
    /// a dev shortcut, logged and counted, never game logic.
    /// </summary>
    public sealed class EscortWatch
    {
        /// <summary>Below this share of its maximum the escorted NPC is topped up.</summary>
        public const float LowShare = 0.25f;
        /// <summary>The top-up brings it to this share of its maximum.</summary>
        public const float TopUpShare = 0.5f;
        /// <summary>Top-ups per escort step; after that the escort plays out as the game has it (and may fail).</summary>
        public const int MaxTopUps = 3;

        string _lastBy;
        int _lastDamage;
        float _lastAt = -1f;
        bool _lastLethal;

        /// <summary>Hits taken in all (damage &gt; 0).</summary>
        public int Hits { get; private set; }
        /// <summary>Top-ups done.</summary>
        public int TopUps { get; private set; }

        /// <summary>A resolved hit on the escorted NPC (HitResolved with the NPC as victim).</summary>
        public void OnHit(string attacker, int damage, bool lethal, float time)
        {
            if (damage <= 0 && !lethal) return;
            Hits++;
            _lastBy = string.IsNullOrEmpty(attacker) ? "?" : attacker;
            _lastDamage = damage;
            _lastAt = time;
            _lastLethal = lethal;
        }

        /// <summary>
        /// The hit points to set (a top-up), or -1: the NPC is alive below <see cref="LowShare"/> of <paramref name="max"/>
        /// and fewer than <see cref="MaxTopUps"/> top-ups were done.
        /// </summary>
        public int TopUp(int hp, int max)
        {
            if (max <= 0 || hp <= 0 || TopUps >= MaxTopUps || hp >= max * LowShare) return -1;
            TopUps++;
            return Math.Max(hp, (int)(max * TopUpShare));
        }

        /// <summary>The death line's cause: the last hit, or none (a death without a hit: drowned, liberation clean-up …).</summary>
        public string Cause(float now) =>
            _lastBy == null
                ? "no hit recorded on it during the escort (not killed by a blow: drowned / swim depth, burned, a script kill)"
                : $"last hit by {_lastBy} ({_lastDamage} hp{(_lastLethal ? ", lethal" : "")}, {(now - _lastAt).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s before), {Hits} hits taken";
    }

    /// <summary>How a <c>walkto</c> step stands (fix-49).</summary>
    public enum WalkToState { Walking, Reached, Stuck, TimedOut }

    /// <summary>
    /// Progress of the dev runner's <c>walkto</c> (fix-49, scenario terrain_ledges): reached within <see cref="Near"/> m
    /// (flat); stuck when the distance left has not dropped by <see cref="MinGain"/> m for <see cref="StuckSeconds"/> s
    /// (the r29 Hammerclan escort: the hero stood at the landscape folds without moving); timed out after the given time.
    /// </summary>
    public sealed class WalkToProgress
    {
        public const float MinGain = 0.5f;
        public const float StuckSeconds = 4f;
        public readonly float Near, Timeout;
        public float Best { get; private set; } = float.MaxValue;
        float _bestAt;

        public WalkToProgress(float near, float timeout) { Near = near; Timeout = timeout; }

        /// <param name="left">Flat distance to the target, m.</param>
        /// <param name="elapsed">Seconds since the step started.</param>
        public WalkToState Update(float left, float elapsed)
        {
            if (left <= Near) return WalkToState.Reached;
            if (left < Best - MinGain || Best == float.MaxValue) { Best = left; _bestAt = elapsed; }
            if (elapsed - _bestAt > StuckSeconds) return WalkToState.Stuck;
            return elapsed > Timeout ? WalkToState.TimedOut : WalkToState.Walking;
        }

        /// <summary>A hero stopped by a cliff or a wall climbed at most this much (m): a step, not the cliff.</summary>
        public const float BlockedClimbMax = 1f;

        /// <summary>
        /// The step's verdict: reached, or with <paramref name="expectBlocked"/> (a cliff or wall the hero must not climb or
        /// pass) not reached and not more than <see cref="BlockedClimbMax"/> m higher.
        /// </summary>
        public static bool Passed(WalkToState state, bool expectBlocked, float climbed) =>
            expectBlocked ? state != WalkToState.Reached && state != WalkToState.Walking && climbed <= BlockedClimbMax
                          : state == WalkToState.Reached;
    }

    /// <summary>One choice of a talk an NPC started by itself, as the runner sees it (fix-53).</summary>
    public readonly struct TalkChoice
    {
        public TalkChoice(bool isExit, bool passesGuard, bool startsFight)
        {
            IsExit = isExit;
            PassesGuard = passesGuard;
            StartsFight = startsFight;
        }

        /// <summary>The "End" topic.</summary>
        public bool IsExit { get; }
        /// <summary>The entry switches its gate guard off (G3 SetGuardStatus not "Active": Sivert's "Friend!", Garik's
        /// "Why are you stopping me?").</summary>
        public bool PassesGuard { get; }
        /// <summary>The entry starts a fight (Sivert's "Foe!").</summary>
        public bool StartsFight { get; }
    }

    /// <summary>
    /// What the dev runner answers when an NPC talks to the hero by itself while he walks or follows a guide (fix-53,
    /// editor-runner-30). G3 gate guards stop the hero with a FirstWarn Important info (gateGuardMayWarn): Garik at the
    /// Hammerclan path ("Halt! Not a step further.", then the topic menu), Sivert at the Fire clan gate ("Welcome! Friend
    /// or foe?", two sub-choices and no End). The conversation locks the hero's controls (HeroControlLock switches the
    /// controller off) until the player answers, so walkto stalled ("Stuck", grounded) and the escorts teleported him
    /// again and again while Mort waited. A player answers as somebody who wants to pass: the answer that switches the
    /// guard off (no fight), else End; with neither the talk is aborted (the warning's commands have run).
    /// </summary>
    public static class UnaskedTalk
    {
        /// <summary>Choices the runner makes in one such talk before it aborts it.</summary>
        public const int MaxChoices = 4;

        /// <summary>The index of the choice to take, or -1 (abort).</summary>
        public static int Pick(System.Collections.Generic.IReadOnlyList<TalkChoice> choices)
        {
            if (choices == null) return -1;
            for (int i = 0; i < choices.Count; i++)
                if (choices[i].PassesGuard && !choices[i].StartsFight && !choices[i].IsExit) return i;
            for (int i = 0; i < choices.Count; i++)
                if (choices[i].IsExit) return i;
            return -1;
        }
    }
}
#endif
