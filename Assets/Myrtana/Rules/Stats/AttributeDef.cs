using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    public enum AttributeKind
    {
        /// <summary>A plain value without maximum (strength, crafts, protections).</summary>
        Attribute,
        /// <summary>A value with a maximum that it is capped to (hit points, mana, stamina).</summary>
        Vital,
    }

    /// <summary>
    /// One character attribute of the content ("STR", "HP", "PROT_FIRE"). Items, teaching, quests and the character
    /// screen refer to attributes by this id; the set of attributes is data, not code.
    /// </summary>
    public sealed class AttributeDef : IDefinition
    {
        [JsonConstructor]
        public AttributeDef(string id, TextId name, TextId maximumName, AttributeKind kind, int order, TextId requirementName = default)
        {
            RequirementName = requirementName;
            Id = id;
            Name = name;
            MaximumName = maximumName;
            Kind = kind;
            Order = order;
        }

        public string Id { get; }
        public TextId Name { get; }
        /// <summary>Name of the maximum of a vital ("Max. life energy").</summary>
        public TextId MaximumName { get; }
        /// <summary>Label of an item requirement on this attribute ("Required strength"); empty = <see cref="Name"/>.</summary>
        public TextId RequirementName { get; }
        public AttributeKind Kind { get; }
        /// <summary>Display order on the character screen.</summary>
        public int Order { get; }

        [JsonIgnore]
        public bool HasMaximum => Kind == AttributeKind.Vital;

        string IDefinition.Key => Id;
    }
}
