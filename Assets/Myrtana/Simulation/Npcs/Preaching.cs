namespace Myrtana.Simulation
{
    /// <summary>
    /// G3 ZS_Preach (CP 1.75 Script_Game): at its Preach point an NPC whose voice set has the "Speech" SVM block
    /// preaches (ZS_Preach_SVM): it says Speech_00 … Speech_09 in turn and gathers listeners (SendPerceptionPreach) before
    /// lines 00, 05 and 08; at the end it releases them (they ContinueRoutine) and has a break (HaveABreak: ContinueRoutine
    /// when its point belongs to an anchor, else ZS_Break). Without the block it only gestures (ZS_Preach_AutoPreach).
    /// SendPerceptionPreach sends each NPC in its routine (not listening yet, not a gate guard, not a slave, able to
    /// receive perceptions: not dead, down, talking or asleep) to a free Listener point (use type 97) — ZS_Listen: walk
    /// there, stand and listen.
    /// </summary>
    public static class Preaching
    {
        public const string Interaction = "Preach", ListenerInteraction = "Listener", SpeechBlock = "Speech";
        public const int LineCount = 10;
        /// <summary>
        /// Listener points and listeners are taken within this distance of the preacher (m) [interp: G3 takes them from
        /// Entity::GetNPCs, the entities near the preacher; the perception scan's 20 m].
        /// </summary>
        public const float Range = 20f;
        /// <summary>Real seconds of one sermon line [interp: G3 waits for the recorded line to end].</summary>
        public const float LineSeconds = 7f;
        /// <summary>The break after a sermon at a point without an anchor (real s) [interp: ZS_Break is not simulated].</summary>
        public const float BreakSeconds = 60f;

        /// <summary>SendPerceptionPreach runs before these lines.</summary>
        public static bool GathersBefore(int line) => line == 0 || line == 5 || line == 8;

        public static string LineOf(int line) => "Speech_" + line.ToString("00");
    }

    /// <summary>A sermon in progress (one per preaching NPC with a view; not saved).</summary>
    internal sealed class Sermon
    {
        public int Line;
        public double NextAt;
        public double RestUntil;
    }
}
