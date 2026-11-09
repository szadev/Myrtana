using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// Where actors and named world entities are, as far as story conditions need it (G3 CondOwnerNearEntity, the
    /// BringNpc and FollowNpc delivery tests; Docs/party.md §5). An entity name is an actor's name ("Tragak", "player")
    /// or a named place of the world ("FP_Wolfclan"). Implemented by the session over the NPC simulation, the hero's pose
    /// and the imported places; tests pass fakes.
    /// </summary>
    public interface IWorldPositions
    {
        /// <summary>Distance in metres (3D, as G3's Entity::GetDistanceTo) between an actor and a named entity; false when either is unknown.</summary>
        bool TryGetDistance(ActorId actor, string entity, out float metres);
    }

    /// <summary>A world without positions (tools, isolated tests): nothing is near anything.</summary>
    public sealed class NoWorldPositions : IWorldPositions
    {
        public static readonly NoWorldPositions Instance = new NoWorldPositions();

        public bool TryGetDistance(ActorId actor, string entity, out float metres)
        {
            metres = float.MaxValue;
            return false;
        }
    }
}
