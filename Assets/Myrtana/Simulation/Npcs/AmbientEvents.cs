using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// An NPC says an ambient SVM line of its voice set (smalltalk "Smalltalk_03", a sermon's "Speech_05"): voice only, heard
    /// near the speaker. Raised by <see cref="NpcSimulation"/> (<see cref="NpcSimulation.Said"/>), published by the session.
    /// </summary>
    public readonly struct NpcAmbientLine : IGameEvent
    {
        public NpcAmbientLine(ActorId speaker, string line)
        {
            Speaker = speaker;
            Line = line;
        }

        public ActorId Speaker { get; }
        public string Line { get; }
    }

    /// <summary>
    /// G3 ZS_Applaud: the NPC faces <see cref="Target"/> and applauds (when its hands are empty), then watches it
    /// (ZS_ObserveSuspect). Raised after a watched fight (the winner) and by the oppressed of a liberated enclave
    /// (ZS_RevolutionSuccess: the liberator).
    /// </summary>
    public readonly struct NpcApplauds : IGameEvent
    {
        public NpcApplauds(ActorId npc, ActorId target)
        {
            Npc = npc;
            Target = target;
        }

        public ActorId Npc { get; }
        public ActorId Target { get; }
    }
}
