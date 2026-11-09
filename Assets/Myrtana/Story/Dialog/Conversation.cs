using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    public enum ConversationPhase
    {
        /// <summary>Commands are being executed.</summary>
        Running,
        /// <summary>Waiting for the presenter to finish the current line.</summary>
        AwaitingLine,
        /// <summary>Waiting for the player's choice.</summary>
        AwaitingChoice,
        /// <summary>The script pauses (G3 InfoWait) until <see cref="Conversation.WaitSeconds"/> have passed.</summary>
        AwaitingTime,
        /// <summary>
        /// fix-140: a page opened by the script (the trade page of G3 DoTrade) holds the conversation until it closes
        /// (<see cref="DialogService.HoldForPage"/>, <see cref="DialogService.ResumeAfterPage"/>).
        /// </summary>
        AwaitingPage,
    }

    /// <summary>Why a command list is running: an entry's own script, or the refusal of a failed pre-check.</summary>
    public enum ScriptPurpose { Entry, Refusal }

    /// <summary>
    /// Progress of the current conversation as plain data (no coroutine or iterator), so it can be inspected, tested
    /// and later saved: the entry, the script position and what the presenter is waiting for.
    /// </summary>
    public sealed class Conversation : IConversationControl
    {
        public Conversation(ActorId partner) => Partner = partner;

        public ActorId Partner { get; }
        public ConversationPhase Phase { get; set; }
        public DialogEntryDef Entry { get; private set; }
        public IReadOnlyList<DialogCommand> Script { get; private set; } = Array.Empty<DialogCommand>();
        public ScriptPurpose Purpose { get; private set; }
        public int NextCommand { get; private set; }
        public IReadOnlyList<DialogOption> Options { get; private set; } = Array.Empty<DialogOption>();
        IReadOnlyList<DialogEntryDef> _offered = Array.Empty<DialogEntryDef>();

        public bool EndRequested { get; private set; }
        public bool LeaveSubmenuRequested { get; private set; }
        /// <summary>The presenter finished a line while its command was still executing (synchronous presenters).</summary>
        public bool LineFinishedEarly { get; set; }

        public bool HasMoreCommands => NextCommand < Script.Count;

        /// <summary>fix-140: a page asked to hold the conversation while the current command ran.</summary>
        public bool PageHeld { get; set; }

        /// <summary>Real seconds the script still pauses (G3 InfoWait); 0 when not pausing.</summary>
        public float WaitSeconds { get; set; }

        public void StartScript(DialogEntryDef entry, IReadOnlyList<DialogCommand> script, ScriptPurpose purpose)
        {
            Entry = entry;
            Script = script;
            Purpose = purpose;
            NextCommand = 0;
            LeaveSubmenuRequested = false;
        }

        public DialogCommand TakeNextCommand() => Script[NextCommand++];

        /// <summary>Offers the entries as menu options and waits for the choice.</summary>
        public void Offer(IReadOnlyList<DialogEntryDef> entries, System.Func<DialogEntryDef, OptionCost> costOf = null)
        {
            _offered = entries;
            var options = new DialogOption[entries.Count];
            for (int i = 0; i < options.Length; i++)
                options[i] = new DialogOption(entries[i].Id, entries[i].Title, entries[i].Kind == DialogEntryKind.Exit,
                                              costOf != null ? costOf(entries[i]) : default);
            Options = options;
            Phase = ConversationPhase.AwaitingChoice;
        }

        public bool TryGetOffered(Id<DialogEntryDef> id, out DialogEntryDef entry)
        {
            foreach (var e in _offered)
                if (e.Id == id)
                {
                    entry = e;
                    return true;
                }
            entry = null;
            return false;
        }

        /// <summary>Runs <paramref name="commands"/> right after the current command (a command that expands into lines).</summary>
        public void RunNext(IReadOnlyList<DialogCommand> commands)
        {
            if (commands == null || commands.Count == 0) return;
            var script = new List<DialogCommand>(Script);
            script.InsertRange(NextCommand, commands);
            Script = script;
        }

        public void RequestEnd() => EndRequested = true;
        public void RequestLeaveSubmenu() => LeaveSubmenuRequested = true;
        public void RequestWait(float seconds) => WaitSeconds = seconds > 0 ? seconds : 0;
    }
}
