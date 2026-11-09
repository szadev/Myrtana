using System;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Where a teleport stone takes the hero (G3 AI helper <c>Teleport_&lt;X&gt;</c>, Docs/map-teleport.md §1.5): the pose
    /// he takes, in metres in the presentation's axes, and the heading in degrees clockwise from north (+z).
    /// </summary>
    public sealed class TeleportDestinationDef : IDefinition
    {
        [JsonConstructor]
        public TeleportDestinationDef(string id, float[] position, float yaw)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Position = position ?? new float[3];
            Yaw = yaw;
        }

        /// <summary>The helper's name, e.g. "Teleport_Faring".</summary>
        public string Id { get; }
        public float[] Position { get; }
        public float Yaw { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>The hero cast a teleport: the presentation moves him to <see cref="Destination"/>.</summary>
    public readonly struct TeleportRequested : IGameEvent
    {
        public TeleportRequested(TeleportDestinationDef destination, string item)
        {
            Destination = destination;
            Item = item;
        }

        public TeleportDestinationDef Destination { get; }
        /// <summary>The stone that was cast (null when the teleport has another cause).</summary>
        public string Item { get; }
    }
}
