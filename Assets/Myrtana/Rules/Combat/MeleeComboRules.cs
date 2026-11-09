using System;

namespace Myrtana.Rules
{
    /// <summary>Which motion phases a combat move plays and where it ends (G3 CP 1.75 _AI_* commands, Docs/combat.md §9.3).</summary>
    public readonly struct CombatMoveShape : IEquatable<CombatMoveShape>
    {
        public CombatMoveShape(bool raise, bool recoverInMove, double waitAfterHit = 0)
        {
            Raise = raise;
            RecoverInMove = recoverInMove;
            WaitAfterHit = waitAfterHit;
        }

        /// <summary>The move starts with its Raise (wind-up) motion; false = straight into the Hit.</summary>
        public bool Raise { get; }
        /// <summary>
        /// The Recover belongs to the move (it ends after the Recover). False = the move ends with its Hit and the Recover
        /// plays on as a tail that the next move (or walking) cuts.
        /// </summary>
        public bool RecoverInMove { get; }
        /// <summary>
        /// Seconds the move still waits after its Hit while the Recover tail already plays (G3 _AI_QuickAttack:
        /// <c>sAIWaitInstr(100)</c> after the combat move, game milliseconds); 0 = the move ends with the Hit.
        /// </summary>
        public double WaitAfterHit { get; }

        public bool Equals(CombatMoveShape other) => Raise == other.Raise && RecoverInMove == other.RecoverInMove && WaitAfterHit.Equals(other.WaitAfterHit);
        public override bool Equals(object obj) => obj is CombatMoveShape s && Equals(s);
        public override int GetHashCode() => (Raise ? 2 : 0) | (RecoverInMove ? 1 : 0) | (WaitAfterHit > 0 ? 4 : 0);
        public override string ToString() =>
            (Raise ? "Raise+" : "") + "Hit" + (WaitAfterHit > 0 ? FormattableString.Invariant($"+wait {WaitAfterHit * 1000:0} ms") : "") +
            (RecoverInMove ? "+Recover" : " (+Recover tail)");
    }

    /// <summary>Timeline of one move, in seconds from its start.</summary>
    public readonly struct MoveTimeline
    {
        public MoveTimeline(double hitStart, double end, double tailEnd)
        {
            HitStart = hitStart;
            End = end;
            TailEnd = tailEnd;
        }

        /// <summary>The swing window opens (the Hit motion starts).</summary>
        public double HitStart { get; }
        /// <summary>The move is over: a queued move starts now (G3: the state returns to PS_Melee_Loop).</summary>
        public double End { get; }
        /// <summary>The Recover tail is over (= <see cref="End"/> when the Recover belongs to the move).</summary>
        public double TailEnd { get; }
    }

    /// <summary>
    /// G3's melee combo timing (CP 1.75) as pure rules:
    /// * Each move is one or more combat-move motions. Attack, QuickAttack, SimpleWhirl, WhirlAttack and GetUpAttack play only
    ///   their Hit (no Raise); PowerAttack and SprintAttack play Raise then Hit; PierceAttack, HackAttack and FinishingAttack
    ///   play Raise, Hit and Recover. When the move's last motion is a Hit, the engine starts the matching Recover on its
    ///   own and gives the move back at once (sAICombatMoveInstr → sAICombatMoveStartRecover): the next move starts at the
    ///   Hit's end and cuts the Recover.
    /// * The input queue has one slot; a press replaces what waits there; a tap is queued on its release. So a click is
    ///   accepted at any time during a move and the queued move starts exactly at the move's end.
    /// * The combo pose: a move that starts while the previous attack's Hit or Recover tail plays continues from the
    ///   pose the Hit ended in (the motion name's end pose); otherwise P0.
    /// </summary>
    public static class MeleeComboRules
    {
        /// <summary>G3 _AI_QuickAttack waits this long after its Hit (sAIWaitInstr 100 game ms) before the state returns to the loop.</summary>
        public const double QuickAttackWait = 0.1;

        /// <summary>The phases of <paramref name="action"/> (G3 _AI_Attack, _AI_QuickAttack, _AI_SimpleWhirl, _AI_WhirlAttack, _AI_GetUpAttack: Hit; _AI_PowerAttack: Raise, Hit; _AI_PierceAttack/_AI_HackAttack/_AI_FinishingAttack: Raise, Hit, Recover).</summary>
        public static CombatMoveShape ShapeOf(CombatAction action)
        {
            switch (action)
            {
                case CombatAction.QuickAttack:
                case CombatAction.QuickAttackLeft:
                case CombatAction.QuickAttackRight:
                    return new CombatMoveShape(raise: false, recoverInMove: false, waitAfterHit: QuickAttackWait);
                case CombatAction.Attack:
                case CombatAction.SimpleWhirl:
                case CombatAction.WhirlAttack:
                case CombatAction.GetUpAttack:
                case CombatAction.JumpBack: // _AI_JumpBack: phase Hit only (vanilla; CP body not known), Recover tail
                    return new CombatMoveShape(raise: false, recoverInMove: false);
                case CombatAction.PowerAttack:
                case CombatAction.SprintAttack:
                    return new CombatMoveShape(raise: true, recoverInMove: false);
                default:
                    return new CombatMoveShape(raise: true, recoverInMove: true);
            }
        }

        /// <summary>
        /// fix-244 [CP 1.75 _AI_PierceAttack 0x10047210, stage 2]: when the pierce's Raise is over and the move's target
        /// is still in ZS_PierceStumble (a pierce landed on it before), the attacker runs _AI_QuickAttack instead and the
        /// pierce's Hit and Recover never play (stage 3 ends the command once the action is QuickAttackR/L). Returns the
        /// move to go on with after <paramref name="action"/>'s Raise: QuickAttack (the side is picked as for any quick
        /// attack) or <paramref name="action"/> itself.
        /// </summary>
        public static CombatAction AfterRaise(CombatAction action, bool targetInPierceStumble) =>
            action == CombatAction.PierceAttack && targetInPierceStumble ? CombatAction.QuickAttack : action;

        /// <summary>
        /// The timeline of a move from its motion lengths (authored seconds) and playback speeds (the motion speed factor
        /// of the wind-up and of the swing; the Recover plays at the swing's speed, G3 keeps the move's speed).
        /// </summary>
        public static MoveTimeline Timeline(CombatMoveShape shape, double raise, double hit, double recover, double raiseSpeed = 1, double hitSpeed = 1)
        {
            double hitStart = shape.Raise ? raise / Positive(raiseSpeed) : 0;
            double hitEnd = hitStart + hit / Positive(hitSpeed);
            double recoverEnd = hitEnd + recover / Positive(hitSpeed);
            return new MoveTimeline(hitStart, shape.RecoverInMove ? recoverEnd : hitEnd + shape.WaitAfterHit, Math.Max(recoverEnd, hitEnd + shape.WaitAfterHit));
        }

        /// <summary>
        /// When a click made at <paramref name="click"/> (seconds after the running move started) starts the next move:
        /// at the move's end when made before it (queued), else at once. G3 has no earliest time: the queue takes it.
        /// </summary>
        public static double NextStart(in MoveTimeline running, double click) => Math.Max(click, running.End);

        /// <summary>The combo pose (0…3) of the next attack: the previous Hit's end pose when chained, else P0.</summary>
        public static int NextComboPose(bool chained, int previousHitEndPose, int previousPose)
        {
            if (!chained) return 0;
            if (previousHitEndPose >= 0 && previousHitEndPose <= 3) return previousHitEndPose;
            return (previousPose + 1) % 4; // no end pose in the name: the next pose
        }

        /// <summary>
        /// The end pose (0…3) in a G3 motion name (word 12, after <c>_00_%_00_</c>: <c>…_P0_Attack_Hit_N_Fwd_00_%_00_P1_118_R</c> → 1);
        /// -1 when the name has none.
        /// </summary>
        public static int EndPoseOf(string motionName)
        {
            if (string.IsNullOrEmpty(motionName)) return -1;
            var words = motionName.Split('_');
            return words.Length < 13 ? -1 : PoseOf(words[12]);
        }

        /// <summary>
        /// The primary pose (0…3) of a motion-name pose word as G3 reads it (Game.dll gCNPC_PS::TrackCurrentPose →
        /// GetPrimaryPoseExt): <c>P0</c>…<c>P3</c> → that pose; a transition <c>Pxy</c> → its second digit y
        /// (<c>P31</c> → 1); <c>P40</c>…<c>P70</c> → 0 (the quick attacks' <c>P50</c>/<c>P60</c>/<c>P70</c> ends). -1 = not a pose word.
        /// </summary>
        public static int PoseOf(string word)
        {
            if (word == null || word.Length < 2 || word.Length > 3 || (word[0] != 'P' && word[0] != 'p')) return -1;
            for (int i = 1; i < word.Length; i++)
                if (word[i] < '0' || word[i] > '9') return -1;
            int last = word[word.Length - 1] - '0';
            if (word.Length == 2) return last <= 3 ? last : -1;
            return word[1] >= '4' ? 0 : last <= 3 ? last : -1;
        }

        // ------------------------------------------------------------------------------------------- quick attacks

        /// <summary>
        /// The side of a QuickAttack (G3 CP 1.75 _AI_QuickAttack): by the primary pose and the fighter's hit direction.
        /// P0 → alternate (hit direction 0 → QuickAttackR, else QuickAttackL); P1 → QuickAttackL, or alternate with a
        /// one-handed weapon or a torch in the left hand; P2 → QuickAttackR; P3 → QuickAttackL. The hit direction after
        /// the move is 1 after an R and 0 after an L (<see cref="HitDirectionAfter"/>).
        /// </summary>
        public static CombatAction QuickAttackSide(int pose, bool hitDirection, bool leftOneHandedOrTorch)
        {
            switch (pose)
            {
                case 0: return Alternate(hitDirection);
                case 1: return leftOneHandedOrTorch ? Alternate(hitDirection) : CombatAction.QuickAttackLeft;
                case 2: return CombatAction.QuickAttackRight;
                default: return CombatAction.QuickAttackLeft;
            }
        }

        static CombatAction Alternate(bool hitDirection) => hitDirection ? CombatAction.QuickAttackLeft : CombatAction.QuickAttackRight;

        /// <summary>
        /// The fighter's hit direction (G3 PSRoutine HitDirection, 0/1 = false/true) after <paramref name="action"/> started
        /// from <paramref name="pose"/>: _AI_Attack/_AI_PowerAttack (and the sprint attack) set 1 from P0/P2 and 0 from P1/P3,
        /// _AI_SimpleWhirl/_AI_PierceAttack the opposite, _AI_QuickAttack 1 after an R and 0 after an L, _AI_GetUpAttack 1;
        /// the other moves keep it.
        /// </summary>
        public static bool HitDirectionAfter(CombatAction action, int pose, bool current)
        {
            bool even = (pose & 1) == 0;
            switch (action)
            {
                case CombatAction.Attack:
                case CombatAction.PowerAttack:
                case CombatAction.SprintAttack: return even;
                case CombatAction.SimpleWhirl:
                case CombatAction.PierceAttack: return !even;
                case CombatAction.QuickAttackRight: return true;
                case CombatAction.QuickAttackLeft: return false;
                case CombatAction.GetUpAttack: return true;
                default: return current;
            }
        }

        // ------------------------------------------------------------------------------------------- combo stages

        /// <summary>The combo stage of a move: chained onto the previous attack → the next stage, else stage 1.</summary>
        public static int NextStage(bool chained, int previousStage) => chained && previousStage > 0 ? previousStage + 1 : 1;

        /// <summary>The dev log line of one combo stage (combatlog): <c>COMBO 2: QuickAttackL P0 [Hit+wait 100 ms (+Recover tail)]</c>.</summary>
        public static string StageLine(int stage, CombatAction action, int pose, CombatAction asked = CombatAction.None) =>
            $"COMBO {stage}: {action} P{pose} [{ShapeOf(action)}]" + (asked != CombatAction.None && asked != action ? $" (asked {asked})" : "");

        static double Positive(double speed) => speed > 1e-4 ? speed : 1e-4;
    }
}
