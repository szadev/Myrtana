using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Item rules of the content (id "items", G3: gameplay-data/items.json).</summary>
    public sealed class ItemRules : IDefinition
    {
        public const string DefinitionId = "items";

        [JsonConstructor]
        public ItemRules(string id, string goldItem, int forgedQuality, int forgedRequirementDelta)
        {
            Id = id;
            GoldItem = goldItem;
            ForgedQuality = forgedQuality;
            ForgedRequirementDelta = forgedRequirementDelta;
        }

        public string Id { get; }
        /// <summary>The item that is money ("It_Gold").</summary>
        public string GoldItem { get; }
        /// <summary>Quality bit of self-forged items …</summary>
        public int ForgedQuality { get; }
        /// <summary>… which need this much less of every required attribute (negative).</summary>
        public int ForgedRequirementDelta { get; }

        string IDefinition.Key => Id;

        public static readonly ItemRules None = new ItemRules(DefinitionId, null, 0, 0);
    }
}
