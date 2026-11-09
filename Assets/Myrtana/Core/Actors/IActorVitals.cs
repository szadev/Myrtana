namespace Myrtana.Core
{
    /// <summary>Whether an actor is alive, hurt, knocked out or dead (G3 gEInfoNPCStatus 0–3).</summary>
    public enum ActorCondition { Alive, Unharmed, Defeated, Dead }

    /// <summary>
    /// Port through which story rules ask about an actor's health without depending on the simulation or combat
    /// modules (implemented by whoever owns NPC health; G3: a missing NPC fails Alive/Unharmed and passes the others).
    /// </summary>
    public interface IActorVitals
    {
        bool Is(ActorId actor, ActorCondition condition);
    }

    /// <summary>
    /// Until combat exists nobody can be hurt: every actor is alive and unharmed. This deliberately includes actors the
    /// content names but the importer has not made actors yet (shared-name spawns), which do exist in the world.
    /// </summary>
    public sealed class UnharmedVitals : IActorVitals
    {
        public bool Is(ActorId actor, ActorCondition condition) =>
            condition == ActorCondition.Alive || condition == ActorCondition.Unharmed;
    }
}
