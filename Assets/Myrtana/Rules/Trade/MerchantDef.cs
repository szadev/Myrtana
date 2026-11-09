using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>An NPC that can trade: its specialty and the treasure sets its stock comes from.</summary>
    public sealed class MerchantDef : IDefinition
    {
        [JsonConstructor]
        public MerchantDef(ActorId actor, string faction, string specialty, IReadOnlyList<string> stock, bool tradesAtStart)
        {
            Actor = actor;
            Faction = faction;
            Specialty = specialty;
            Stock = stock ?? Array.Empty<string>();
            TradesAtStart = tradesAtStart;
        }

        public ActorId Actor { get; }
        /// <summary>Political faction whose fame decides which armour the merchant sells.</summary>
        public string Faction { get; }
        /// <summary>Goods this merchant pays the specialist price for (<see cref="TradeRules.Specialties"/>); null = none.</summary>
        public string Specialty { get; }
        /// <summary>Treasure sets (generate, generate-all and refresh uses) of the stock.</summary>
        public IReadOnlyList<string> Stock { get; }
        /// <summary>Whether trading is enabled at the start of a game (dialogs switch it later).</summary>
        public bool TradesAtStart { get; }

        string IDefinition.Key => Actor.Name;
    }

    /// <summary>An NPC that can teach: the attributes and skills it offers.</summary>
    public sealed class TeacherDef : IDefinition
    {
        [JsonConstructor]
        public TeacherDef(ActorId actor, IReadOnlyList<string> attributes, IReadOnlyList<string> skills, bool teachesAtStart)
        {
            Actor = actor;
            Attributes = attributes ?? Array.Empty<string>();
            Skills = skills ?? Array.Empty<string>();
            TeachesAtStart = teachesAtStart;
        }

        public ActorId Actor { get; }
        public IReadOnlyList<string> Attributes { get; }
        public IReadOnlyList<string> Skills { get; }
        public bool TeachesAtStart { get; }

        string IDefinition.Key => Actor.Name;
    }
}
