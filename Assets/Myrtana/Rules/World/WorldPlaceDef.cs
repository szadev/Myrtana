using System;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// A named world entity the story refers to by name (Docs/party.md §5): free points and AI helpers such as
    /// <c>FP_Wolfclan</c>, <c>Ardea_4Friends</c>, the FollowNpc checkpoints (<c>Bram_HuntWolvesCheckPoint</c>) and other
    /// entities named by CondOwnerNearEntity or a quest's DestinationEntity. Position in metres in the presentation's axes;
    /// yaw in degrees clockwise from north (+z).
    /// </summary>
    public sealed class WorldPlaceDef : IDefinition
    {
        [JsonConstructor]
        public WorldPlaceDef(string id, float[] position, float yaw)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Position = position ?? new float[3];
            Yaw = yaw;
        }

        /// <summary>The G3 entity name.</summary>
        public string Id { get; }
        public float[] Position { get; }
        public float Yaw { get; }

        string IDefinition.Key => Id;
    }
}
