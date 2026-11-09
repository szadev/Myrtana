using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>The partner is a guard with crime news for the player (G3 condition EnclaveCrime → HasGuardNews).</summary>
    public sealed class HasGuardNews : Condition
    {
        public override bool Evaluate(in ConditionContext context) => context.Services.Crime.HasGuardNews(context.Partner);
    }

    /// <summary>The partner is a boss with news about its enclave's crimes (G3 condition PoliticalCrime → HasBossNews).</summary>
    public sealed class HasBossNews : Condition
    {
        public override bool Evaluate(in ConditionContext context) => context.Services.Crime.HasBossNews(context.Partner);
    }

    /// <summary>The partner tells the player what the guards heard (G3 SayGuardNews); opens the bribe/excuse/provoke choices.</summary>
    public sealed class SayGuardNews : DialogCommand { }

    /// <summary>The partner (a boss) comments on its enclave's crimes (G3 SayBossNews).</summary>
    public sealed class SayBossNews : DialogCommand { }

    /// <summary>The partner takes the bribe or refuses and attacks (G3 Bribe; the gold is a separate giveItems).</summary>
    public sealed class Bribe : DialogCommand { }

    /// <summary>The player makes excuses to the partner (G3 Excuse).</summary>
    public sealed class Excuse : DialogCommand { }

    /// <summary>The partner's enclave forgets the player's crimes (CP command ResetCrime).</summary>
    public sealed class ResetCrime : DialogCommand { }

    /// <summary>The story moves on to a chapter (G3 SetChapter).</summary>
    public sealed class SetChapter : DialogCommand
    {
        [JsonConstructor]
        public SetChapter(int chapter) => Chapter = chapter;

        public int Chapter { get; }
    }

    /// <summary>
    /// Speaks the lines of a crime talk as shared lines of the partner and applies its outcome: the way-out choices
    /// (story flags <see cref="BribeFlag"/>, <see cref="ExcuseFlag"/>, <see cref="ProvokeFlag"/>, as G3 sets its game
    /// events) and an attack that ends the dialog.
    /// </summary>
    public abstract class CrimeSpeechHandler<T> : CommandHandler<T> where T : DialogCommand
    {
        public const string BribeFlag = "DIA_BRIBE", ExcuseFlag = "DIA_EXCUSE", ProvokeFlag = "DIA_PROVOKE";

        protected abstract CrimeSpeech Talk(ICrimeTalk crime, ActorId partner);

        /// <summary>Whether this talk decides the way-out flags (the news itself does; bribe and excuse do not).</summary>
        protected virtual bool SetsWayOut => false;

        protected override CommandResult Execute(T command, CommandContext context)
        {
            var speech = Talk(context.Services.Crime, context.Partner);
            if (SetsWayOut)
                foreach (var flag in new[] { BribeFlag, ExcuseFlag, ProvokeFlag })
                    if (speech.OffersWayOut) context.Flags.Set(flag);
                    else context.Flags.Clear(flag);
            var lines = new List<DialogCommand>();
            foreach (var line in speech.Lines) lines.Add(new SayShared(ActorRef.Partner, line));
            if (speech.Attacks) lines.Add(new EndDialog());
            context.Conversation.RunNext(lines);
            return CommandResult.Continue;
        }
    }

    public sealed class SayGuardNewsHandler : CrimeSpeechHandler<SayGuardNews>
    {
        protected override bool SetsWayOut => true;
        protected override CrimeSpeech Talk(ICrimeTalk crime, ActorId partner) => crime.TellGuardNews(partner);
    }

    public sealed class SayBossNewsHandler : CrimeSpeechHandler<SayBossNews>
    {
        protected override CrimeSpeech Talk(ICrimeTalk crime, ActorId partner) => crime.TellBossNews(partner);
    }

    public sealed class BribeHandler : CrimeSpeechHandler<Bribe>
    {
        protected override CrimeSpeech Talk(ICrimeTalk crime, ActorId partner) => crime.Bribe(partner);
    }

    public sealed class ExcuseHandler : CrimeSpeechHandler<Excuse>
    {
        protected override CrimeSpeech Talk(ICrimeTalk crime, ActorId partner) => crime.Excuse(partner);
    }

    public sealed class ResetCrimeHandler : CommandHandler<ResetCrime>
    {
        protected override CommandResult Execute(ResetCrime command, CommandContext context)
        {
            context.Services.Crime.ResetCrime(context.Partner);
            return CommandResult.Continue;
        }
    }

    public sealed class SetChapterHandler : CommandHandler<SetChapter>
    {
        protected override CommandResult Execute(SetChapter command, CommandContext context)
        {
            context.Services.Chapter.Set(command.Chapter);
            return CommandResult.Continue;
        }
    }
}
