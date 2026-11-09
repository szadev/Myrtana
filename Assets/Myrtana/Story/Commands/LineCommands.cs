using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// An actor speaks one of its voice set's shared lines (G3 "SVM": "Sure", "NotEnoughGold", "Teach_1H_2_00").
    /// The text id is built from the speaker's voice set by <see cref="SharedLines"/>, so one entry works for every
    /// partner. The clip key equals the text id.
    /// </summary>
    public sealed class SayShared : DialogCommand
    {
        public SayShared(ActorRef speaker, string line) : this(speaker, line, 0) { }

        [JsonConstructor]
        public SayShared(ActorRef speaker, string line, int variants, string fallback = null, IReadOnlyList<string> voiced = null)
        {
            Speaker = speaker;
            Line = line;
            Variants = variants;
            Fallback = string.IsNullOrEmpty(fallback) ? null : fallback;
            Voiced = voiced != null && voiced.Count > 0 ? voiced : null;
        }

        /// <summary>
        /// With <see cref="Voiced"/>: the line speakers whose voice set is not listed say instead (G3 SayHoldingPosition:
        /// "PartyWait" for voices with the NPCParty block, else "Sure"). Null = always <see cref="Line"/>.
        /// </summary>
        public string Fallback { get; }
        /// <summary>The voice sets that have <see cref="Line"/> (null = all).</summary>
        public IReadOnlyList<string> Voiced { get; }

        /// <summary>The line a speaker with <paramref name="voiceSet"/> says (<see cref="Line"/> or <see cref="Fallback"/>).</summary>
        public string LineFor(string voiceSet)
        {
            if (Fallback == null || Voiced == null) return Line;
            foreach (var v in Voiced)
                if (string.Equals(v, voiceSet, StringComparison.OrdinalIgnoreCase)) return Line;
            return Fallback;
        }

        public ActorRef Speaker { get; }
        public string Line { get; }
        /// <summary>
        /// 0 = the line as named; n &gt; 0 = one of n numbered variants picked at random ("KeepItUp_00" … "_04", G3
        /// SayKeepItUp / SayConfirm).
        /// </summary>
        public int Variants { get; }

        /// <summary>The line name to speak this time.</summary>
        public string PickLine(IRandom random) => PickLine(random, null);

        /// <summary>The line name a speaker with <paramref name="voiceSet"/> speaks this time.</summary>
        public string PickLine(IRandom random, string voiceSet)
        {
            string line = LineFor(voiceSet);
            return Variants <= 0 ? line : $"{line}_{random.Next(Variants):00}";
        }
    }

    /// <summary>A short message on screen (G3 GO_ messages: "Not enough gold!").</summary>
    public sealed class ShowMessage : DialogCommand
    {
        [JsonConstructor]
        public ShowMessage(TextId text) => Text = text;

        public TextId Text { get; }
    }

    /// <summary>Published by <see cref="ShowMessage"/>; the HUD shows it.</summary>
    public readonly struct GameMessage : IGameEvent
    {
        public GameMessage(TextId text) : this(text, default) { }

        /// <param name="subject">The actor the text's <c>$(name)</c> stands for (empty = none).</param>
        public GameMessage(TextId text, ActorId subject)
        {
            Text = text;
            Subject = subject;
        }

        public TextId Text { get; }
        /// <summary>Who <c>$(name)</c> in the text names (GO_NoParty …); empty when the text has no name.</summary>
        public ActorId Subject { get; }
    }

    /// <summary>How shared-line text ids are named (content definition "sharedLines").</summary>
    public sealed class SharedLines : IDefinition
    {
        public const string DefinitionId = "sharedLines";
        public const string VoiceToken = "{voice}", LineToken = "{line}";

        [JsonConstructor]
        public SharedLines(string id, string textPattern)
        {
            Id = id;
            TextPattern = textPattern;
        }

        public string Id { get; }
        /// <summary>E.g. "SVM_{voice}_{line}".</summary>
        public string TextPattern { get; }

        public TextId TextOf(string voiceSet, string line) =>
            new TextId(TextPattern.Replace(VoiceToken, voiceSet).Replace(LineToken, line));

        string IDefinition.Key => Id;
    }

    public sealed class SaySharedHandler : CommandHandler<SayShared>
    {
        readonly Func<SharedLines> _lines;

        public SaySharedHandler(Func<SharedLines> lines) => _lines = lines;

        protected override CommandResult Execute(SayShared command, CommandContext context)
        {
            var speaker = context.Resolve(command.Speaker);
            string voiceSet = context.Services.VoiceSets.VoiceSetOf(speaker);
            var lines = _lines();
            if (string.IsNullOrEmpty(voiceSet) || lines == null)
            {
                context.Log.Info($"Shared line '{command.Line}' of {speaker} has no voice set; skipped.");
                return CommandResult.Continue;
            }
            var text = lines.TextOf(voiceSet, command.PickLine(context.Services.Random, voiceSet));
            context.Presenter.ShowLine(new DialogLine(speaker, text, text.Value));
            return CommandResult.WaitForLine;
        }
    }

    public sealed class ShowMessageHandler : CommandHandler<ShowMessage>
    {
        protected override CommandResult Execute(ShowMessage command, CommandContext context)
        {
            context.Events.Publish(new GameMessage(command.Text));
            return CommandResult.Continue;
        }
    }
}
