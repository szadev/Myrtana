using System;

namespace Myrtana.Core
{
    /// <summary>
    /// Key of a localized string ("INFO_FILLER787", "FO_Gnar"). Definitions and state store text ids, never resolved
    /// text, so switching the language changes everything that is shown.
    /// </summary>
    public readonly struct TextId : IEquatable<TextId>
    {
        public string Value { get; }

        public TextId(string value) => Value = value;

        public bool IsEmpty => string.IsNullOrEmpty(Value);

        public bool Equals(TextId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is TextId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? "<none>";
    }
}
