using System;

namespace Myrtana.Core
{
    /// <summary>
    /// Unique name of an actor: an NPC ("Gnar") or the hero (<see cref="Player"/>). NPC definitions, dialog owners and
    /// speakers all refer to actors by this id.
    /// </summary>
    public readonly struct ActorId : IEquatable<ActorId>
    {
        /// <summary>The hero, in every content pack.</summary>
        public static readonly ActorId Player = new ActorId("player");

        public string Name { get; }
        // The name's hash, computed once: ActorIds key dozens of dictionary lookups per NPC per frame (simulation,
        // perception, combat), and hashing the name every time showed in the frame (perf-9). 0 for no name.
        readonly int _hash;

        public ActorId(string name)
        {
            Name = name;
            _hash = name == null ? 0 : StringComparer.Ordinal.GetHashCode(name);
        }

        public bool IsEmpty => string.IsNullOrEmpty(Name);
        public bool IsPlayer => Equals(Player);

        public bool Equals(ActorId other) => _hash == other._hash && string.Equals(Name, other.Name, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ActorId other && Equals(other);
        public override int GetHashCode() => _hash;
        public override string ToString() => Name ?? "<none>";

        public static bool operator ==(ActorId a, ActorId b) => a.Equals(b);
        public static bool operator !=(ActorId a, ActorId b) => !a.Equals(b);
    }
}
