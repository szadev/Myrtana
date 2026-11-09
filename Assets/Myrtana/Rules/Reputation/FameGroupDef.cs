using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>A faction or enclave the player can have fame with, and its display name.</summary>
    public sealed class FameGroupDef : IDefinition
    {
        [JsonConstructor]
        public FameGroupDef(string id, FameKind kind, TextId name)
        {
            Id = id;
            Kind = kind;
            Name = name;
        }

        /// <summary>The group id fame changes use ("Orc", "Faring"); unique across both kinds.</summary>
        public string Id { get; }
        public FameKind Kind { get; }
        public TextId Name { get; }

        string IDefinition.Key => Id;
    }
}
