namespace Myrtana.Core
{
    /// <summary>
    /// Port through which story rules read and update actor facts without depending on the simulation module
    /// (implemented by <c>Myrtana.Simulation.ActorRegistry</c>).
    /// </summary>
    public interface IActorFacts
    {
        bool Exists(ActorId actor);
        bool HasTalkedToPlayer(ActorId actor);
        void MarkTalkedToPlayer(ActorId actor);
    }
}
