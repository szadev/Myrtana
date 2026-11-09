using System.Text.Json.Serialization;

namespace Myrtana.Story
{
    /// <summary>The hero gains experience (final amount; G3's ×5 is applied at import).</summary>
    public sealed class GiveXp : DialogCommand
    {
        [JsonConstructor]
        public GiveXp(int amount) => Amount = amount;

        public int Amount { get; }
    }

    /// <summary>A named permanent attribute boost of the progression rules (G3 BoostAttribs STR|DEX|INT|XAR).</summary>
    public sealed class BoostAttributes : DialogCommand
    {
        [JsonConstructor]
        public BoostAttributes(string boost) => Boost = boost;

        public string Boost { get; }
    }

    public sealed class GiveXpHandler : CommandHandler<GiveXp>
    {
        protected override CommandResult Execute(GiveXp command, CommandContext context)
        {
            context.Services.Progression.AwardXp(command.Amount);
            return CommandResult.Continue;
        }
    }

    public sealed class BoostAttributesHandler : CommandHandler<BoostAttributes>
    {
        protected override CommandResult Execute(BoostAttributes command, CommandContext context)
        {
            if (!context.Services.Progression.Boost(command.Boost))
                context.Log.Info($"Attribute boost '{command.Boost}' does not exist in the current rules; skipped.");
            return CommandResult.Continue;
        }
    }
}
