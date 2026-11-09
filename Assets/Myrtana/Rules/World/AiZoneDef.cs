using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// An owned AI zone of the world (G3 gCNavZone_PS + gCAIZone_PS: a house, a camp, a city quarter): a polygon on the
    /// ground with a height band, who owns it and how well it is guarded (Docs/perception-crime.md §3.3). Metres, in the
    /// presentation's axes.
    /// </summary>
    public sealed class AiZoneDef : IDefinition
    {
        [JsonConstructor]
        public AiZoneDef(string id, string name, string owner, ZoneSecurity security, bool indoor, float[] outline, float floor,
                         float above, float below)
        {
            Id = id;
            Name = name;
            Owner = owner;
            Security = security;
            Indoor = indoor;
            Outline = outline ?? Array.Empty<float>();
            Floor = floor;
            Above = above;
            Below = below;
        }

        /// <summary>The G3 entity GUID.</summary>
        public string Id { get; }
        public string Name { get; }
        /// <summary>The owning enclave (<see cref="EnclaveDef"/> id); null = nobody.</summary>
        public string Owner { get; }
        public ZoneSecurity Security { get; }
        /// <summary>A house (G3 gEZoneType Indoor): NPCs in the same indoor zone see each other without line of sight.</summary>
        public bool Indoor { get; }
        /// <summary>The ground polygon as x, z pairs (world metres).</summary>
        public float[] Outline { get; }
        /// <summary>Height of the zone's origin (metres).</summary>
        public float Floor { get; }
        /// <summary>How far above <see cref="Floor"/> the zone reaches (G3 TopToleranz, metres).</summary>
        public float Above { get; }
        /// <summary>How far below <see cref="Floor"/> the zone reaches (G3 BottomToleranz, metres).</summary>
        public float Below { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>A town, camp or clan (G3 gCEnclave_PS): its name is also its fame group.</summary>
    public sealed class EnclaveDef : IDefinition
    {
        [JsonConstructor]
        public EnclaveDef(string id, string alignment, float[] position = null)
        {
            Id = id;
            Alignment = alignment;
            Position = position != null && position.Length >= 3 ? position : null;
        }

        /// <summary>The enclave's name ("Faring"), as used by fame and NPC membership.</summary>
        public string Id { get; }
        /// <summary>Political alignment of the enclave ("Orc", "Reb" …; null = none).</summary>
        public string Alignment { get; }
        /// <summary>Where the enclave entity stands (metres, presentation axes; null = unknown): CP measures the hero's
        /// distance to it before a liberated town is exchanged.</summary>
        public float[] Position { get; }

        string IDefinition.Key => Id;
    }
}
