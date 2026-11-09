using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The finishing move of G3 CP 1.75 (Docs/combat.md §5.3, §5.5): a downed, killable actor is killed by a
    /// FinishingAttack. The blow lands by time, not by weapon contact: OnAI_FinishingAttack kills the attacker's target
    /// once the move has run 1.2 s, if the target is still down.
    /// </summary>
    public static class FinishingMove
    {
        /// <summary>The player's focus must be this close to start a finishing move (OnPlayerAction, cm).</summary>
        public const float ReachCm = 300f;
        /// <summary>PS_Melee_Loop keeps a downed focus (when the scan finds nothing) only while the block is held and within this distance (cm).</summary>
        public const float DownedFocusCm = 500f;
        /// <summary>OnAI_FinishingAttack: the kill happens after this much time in the move (state time &gt; 1.2 s).</summary>
        public const double KillAfterSeconds = 1.2;

        /// <summary>The player's block + hold starts a finishing move on this focus (focus AIMode Down, CanBeKilled, ≤ 300).</summary>
        public static bool InReach(VitalState focus, float distanceCm, bool killable) =>
            focus == VitalState.Down && killable && distanceCm <= ReachCm;

        /// <summary>PS_Melee_Loop: a downed previous focus stays while the block is held and within 500 (CombatFocus.KeepsPrevious).</summary>
        public static bool KeepsDownedFocus(float distanceCm, bool blockHeld) => blockHeld && distanceCm <= DownedFocusCm;
    }

    /// <summary>
    /// One finishing move in progress on a body: its target and when it started. <see cref="Tick"/> reports the moment
    /// the blow lands (1.2 s into the move); a move that ends normally before that lands at its end <b>[interp: G3's
    /// motions are longer than 1.2 s]</b>; an interrupted move (hit, fall) lands nothing.
    /// </summary>
    public sealed class FinishingStrike
    {
        double _start;

        public ActorId Target { get; private set; }
        public bool Pending { get; private set; }

        public void Begin(ActorId target, double now)
        {
            Target = target;
            _start = now;
            Pending = !target.IsEmpty;
        }

        /// <summary>True once, when the blow lands.</summary>
        public bool Tick(double now)
        {
            if (!Pending || now - _start <= FinishingMove.KillAfterSeconds) return false;
            Pending = false;
            return true;
        }

        /// <summary>The move ended: true when the blow still lands now (a normal end before 1.2 s).</summary>
        public bool End(bool interrupted)
        {
            if (!Pending) return false;
            Pending = false;
            return !interrupted;
        }

        public void Cancel() => Pending = false;
    }

    /// <summary>
    /// Combat voice lines (G3 shared lines, "SVM_&lt;voice set&gt;_&lt;line&gt;"). G3 says them through FUN_1000baf0 with
    /// the speaker as both roles: a humanoid NPC starting ZS_Attack_FinishingAttack shouts "FinishingMove"; every
    /// actor entering ZS_RagDollDead / ZS_PiercedDead says "DEAD", entering ZS_PiercedKO (and the knock-out family) says
    /// "DOWN". The player's PS_Melee_FinishingAttack says nothing. None of these lines has a stringtable text (SVMTable.ini
    /// maps them to SVM_HOGE3… ids that no language defines), so G3 shows no subtitle [data].
    /// </summary>
    public static class CombatLines
    {
        public const string FinishingMove = "FinishingMove", Dead = "Dead", Down = "Down";

        /// <summary>The finisher's shout as the move starts: humanoid NPCs only (null = silent).</summary>
        public static string OfFinisher(bool isPlayer, bool humanoid) => !isPlayer && humanoid ? FinishingMove : null;

        /// <summary>The victim's cry: "Dead" when killed, "Down" when knocked out.</summary>
        public static string OfVictim(bool killed) => killed ? Dead : Down;
    }

    /// <summary>An actor says a combat line (shared line name, see <see cref="CombatLines"/>); the views play its voice.</summary>
    public readonly struct CombatShout : IGameEvent
    {
        public CombatShout(ActorId speaker, string line)
        {
            Speaker = speaker;
            Line = line;
        }

        public ActorId Speaker { get; }
        public string Line { get; }
    }
}
