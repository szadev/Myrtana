using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// One step of a dialog entry's script (<c>{"op": "say", …}</c>). Commands are plain data; each concrete type has
    /// one <see cref="ICommandHandler"/> registered in the <see cref="CommandRunner"/>.
    /// </summary>
    public abstract class DialogCommand { }

    /// <summary>An actor speaks a line: subtitle + voice clip. Waits until the line has finished.</summary>
    public sealed class Say : DialogCommand
    {
        [JsonConstructor]
        public Say(ActorRef speaker, TextId text, string voice, string stage = null)
        {
            Speaker = speaker;
            Text = text;
            Voice = voice;
            Stage = string.IsNullOrEmpty(stage) ? null : stage;
        }

        /// <summary>
        /// Stage direction of the line (CP 1.75 <c>SD_Angry</c>, <c>SD_Point</c> …; picks the speaker's talk gesture,
        /// Docs/dialog-presentation.md §3); null = none (G3 then picks a random gesture).
        /// </summary>
        public string Stage { get; }

        public ActorRef Speaker { get; }
        public TextId Text { get; }
        /// <summary>Language-independent voice clip key; null = no recording (subtitle only).</summary>
        public string Voice { get; }
    }

    public sealed class SetFlag : DialogCommand
    {
        [JsonConstructor]
        public SetFlag(string flag) => Flag = flag;

        public string Flag { get; }
    }

    public sealed class ClearFlag : DialogCommand
    {
        [JsonConstructor]
        public ClearFlag(string flag) => Flag = flag;

        public string Flag { get; }
    }

    /// <summary>Sets the quest Running (from Open or Lost).</summary>
    public sealed class StartQuest : DialogCommand
    {
        [JsonConstructor]
        public StartQuest(Id<QuestDef> quest) => Quest = quest;

        public Id<QuestDef> Quest { get; }
    }

    /// <summary>The conversation ends after the current entry.</summary>
    public sealed class EndDialog : DialogCommand { }

    /// <summary>After the current entry, return to the top-level choices even if the entry has sub-choices.</summary>
    public sealed class LeaveSubmenu : DialogCommand { }

    /// <summary>
    /// A command of a later slice, kept in the content so nothing is lost on import. Running it does nothing except a
    /// log message.
    /// </summary>
    public sealed class NotSupported : DialogCommand
    {
        [JsonConstructor]
        public NotSupported(string command, string arguments)
        {
            Command = command;
            Arguments = arguments;
        }

        public string Command { get; }
        public string Arguments { get; }
    }
}
