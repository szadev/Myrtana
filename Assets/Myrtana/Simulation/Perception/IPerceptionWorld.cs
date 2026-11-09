using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// What perception needs to know about the world right now: where actors are, which NPCs could receive a broadcast
    /// and where they work. The session implements it over the NPC simulation; the presentation reports the player's
    /// pose and the indoor zones.
    /// </summary>
    public interface IPerceptionWorld
    {
        bool TryGetPose(ActorId actor, out WorldPose pose);

        /// <summary>The indoor AI zone the actor stands in; null = outdoors or unknown.</summary>
        string IndoorZoneOf(ActorId actor);

        /// <summary>Every NPC that might receive a broadcast (not Abstract); range and line of sight are checked by the caller.</summary>
        IEnumerable<BroadcastReceiver> Receivers();

        /// <summary>Centimetres from <paramref name="point"/> to the NPC's routine destination (its workplace).</summary>
        float DistanceToWorkplace(ActorId npc, WorldPose point);
    }
}
