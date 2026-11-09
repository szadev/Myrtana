using System;

namespace Myrtana.Core
{
    /// <summary>A placed world instance (NPC spawn, container, door). Imported G3 entities keep their entity GUID.</summary>
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public Guid Value { get; }

        public EntityId(Guid value) => Value = value;

        public bool IsEmpty => Value == Guid.Empty;

        public bool Equals(EntityId other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is EntityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("D");

        public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);
        public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);
    }
}
