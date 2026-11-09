using System;

namespace Myrtana.Core
{
    /// <summary>
    /// Stable, typed id of a content definition, e.g. <c>Id&lt;QuestDef&gt;("Gnar_EnterCastle")</c>.
    /// The type parameter only exists for the compiler: an <c>Id&lt;QuestDef&gt;</c> cannot be passed where a dialog entry
    /// id is expected. Comparison is ordinal and case-sensitive; importers write canonical names.
    /// In JSON an id is a plain string (<see cref="Content.IdJsonConverterFactory"/>).
    /// </summary>
    public readonly struct Id<T> : IEquatable<Id<T>>
    {
        public string Name { get; }
        // The name's hash, computed once: ids key the per-frame lookups of routine points, stations and claims (perf-9,
        // as ActorId). 0 for no name.
        readonly int _hash;

        public Id(string name)
        {
            Name = name;
            _hash = name == null ? 0 : StringComparer.Ordinal.GetHashCode(name);
        }

        public bool IsEmpty => string.IsNullOrEmpty(Name);

        public bool Equals(Id<T> other) => _hash == other._hash && string.Equals(Name, other.Name, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is Id<T> other && Equals(other);
        public override int GetHashCode() => _hash;
        public override string ToString() => Name ?? "<none>";

        public static bool operator ==(Id<T> a, Id<T> b) => a.Equals(b);
        public static bool operator !=(Id<T> a, Id<T> b) => !a.Equals(b);
    }
}
