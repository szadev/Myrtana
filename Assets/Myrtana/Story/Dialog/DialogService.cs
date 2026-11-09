using System;
using System.Linq;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// Runs conversations. The flow follows gameplay-logic.md §9:
    /// <list type="number">
    /// <item>Start: the first Refuse, the first Important and all News entries play automatically, then the menu.</item>
    /// <item>A list is shown as a menu when it has more than one entry and starts with a menu choice; otherwise its
    /// first entry plays automatically (a lone answer, a lone "End").</item>
    /// <item>Picking an entry runs its pre-checks (a failure plays the refusal and returns to the sub-menu it was picked
    /// from, or else the top level), marks it given unless repeatable, and runs its commands one by one; Say lines wait for the presenter.</item>
    /// <item>After an entry: end if requested; else its available sub-choices; else the top level again (News first).</item>
    /// </list>
    /// The presenter answers with <see cref="CompleteLine"/> and <see cref="Choose"/>.
    /// </summary>
    public sealed class DialogService
    {
        /// <summary>Entries run back-to-back without waiting for the player before the service assumes a content loop.</summary>
        const int MaxEntriesWithoutWaiting = 64;

        readonly StoryWorld _world;
        readonly DialogCatalog _catalog;
        readonly TopicListBuilder _topics;
        readonly CommandRunner _commands;
        readonly IDialogPresenter _presenter;
        Conversation _conversation;
        int _entriesWithoutWaiting;

        public DialogService(StoryWorld world, DialogCatalog catalog, CommandRunner commands, IDialogPresenter presenter)
        {
            _world = world;
            _catalog = catalog;
            _commands = commands;
            _presenter = new EngagingPresenter(this, presenter);
            _topics = new TopicListBuilder(catalog, world.DialogMemory);
        }

        public bool IsActive => _conversation != null;

        /// <summary>
        /// fix-76: the running conversation has shown a line or a menu with a real choice (<see cref="DialogEngaged"/>).
        /// A talk that only runs an exit (nothing to say) never engages.
        /// </summary>
        public bool IsEngaged => _conversation != null && _engaged;

        bool _engaged;

        /// <summary>
        /// fix-140: the conversation waits for a page its script opened (G3 DoTrade: the trader idles in IS_Idle_Trade, facing
        /// the hero, and the info's line completes only when the page closes). It stays active, so the partner keeps the talk
        /// state — no routine, turned to the hero — for the whole page.
        /// </summary>
        public bool IsHeldByPage => _conversation != null && _conversation.Phase == ConversationPhase.AwaitingPage;

        /// <summary>
        /// fix-140: called while a command runs (a listener of the event it published, e.g. <see cref="TradeRequested"/>):
        /// the script pauses after that command until <see cref="ResumeAfterPage"/>. Without a page host (G3Cli, tests that
        /// do not call it) the script just goes on. False when nothing can be held: no command runs, or the conversation is
        /// being finished silently (<see cref="Abort"/>: the hero is taken away, no page opens).
        /// </summary>
        public bool HoldForPage()
        {
            if (_conversation == null || _conversation.Phase != ConversationPhase.Running || _finishingSilently) return false;
            _conversation.PageHeld = true;
            return true;
        }

        bool _finishingSilently;

        /// <summary>fix-140: the page that held the conversation closed: its script goes on (G3: back to ProcessInfo).</summary>
        public void ResumeAfterPage()
        {
            var c = _conversation;
            if (c == null || c.Phase != ConversationPhase.AwaitingPage) return;
            _entriesWithoutWaiting = 0;
            Continue();
        }

        /// <summary>The running conversation, or null.</summary>
        public Conversation Current => _conversation;

        /// <summary>
        /// An NPC that cannot be talked to now whatever its topics ([owner] fix-113: held by the sleep spell). Null = none.
        /// </summary>
        public Func<ActorId, bool> TalkBlocked { get; set; }

        /// <summary>
        /// fix-247: the line the partner says before the conversation proper, or null (G3 ZS_Talk @10024ff0: an NPC whose
        /// last task was ZS_Sleep says "YouDisturbedMySlumber", then _AI_StandUp, then the info manager's lines). Asked when
        /// the conversation starts, before <see cref="DialogStarted"/>; null = none.
        /// </summary>
        public Func<ActorId, DialogLine?> Prelude { get; set; }

        /// <summary>fix-247: the partner is saying its <see cref="Prelude"/> line; the topics come when it is complete.</summary>
        public bool InPrelude => _conversation != null && _prelude;

        bool _prelude;

        /// <summary>
        /// The player can start a conversation: an actor with topics of its own (as before fix-158), or one that has an
        /// available topic of the player's (G3 HasInfos: the hero's generic infos — trade for a trader, teaching for a
        /// teacher, join, pickpocket … — make every generic NPC talkable whose conditions they meet).
        /// </summary>
        public bool CanTalkTo(ActorId npc) =>
            !npc.IsEmpty && !npc.IsPlayer && !(TalkBlocked?.Invoke(npc) ?? false) &&
            (_catalog.HasTopics(npc) || _topics.HasInfos(_world.ConditionsFor(npc)));

        /// <summary>What a conversation with <paramref name="npc"/> would offer now (start list: automatic entries, then the menu).</summary>
        public List<DialogEntryDef> TopicsFor(ActorId npc) => _topics.TopLevel(_world.ConditionsFor(npc), AutoPlay.All);

        /// <summary>
        /// The player talks to <paramref name="npc"/>. With nothing to talk about (G3 AssessTalk: HasInfo false) there is no
        /// dialog: <see cref="TalkRefused"/> is published and the NPC answers with a refusal line (StartSayRefuseTalk).
        /// </summary>
        public void Talk(ActorId npc)
        {
            if (IsActive || npc.IsEmpty || npc.IsPlayer) return;
            if (CanTalkTo(npc)) Start(npc);
            else if (!(TalkBlocked?.Invoke(npc) ?? false)) _world.Events.Publish(new TalkRefused(npc));
        }

        /// <summary>
        /// G3 HasImportantInfo: the NPC has an available Important entry for the player and no available Refuse entry of
        /// its own. Such an NPC starts the conversation where it stands (G3 AssessTalk → ZS_Talk; gate guards' warnings,
        /// crime news, greetings).
        /// </summary>
        public bool HasImportantInfo(ActorId npc) =>
            !IsActive && !npc.IsEmpty && !npc.IsPlayer && !(TalkBlocked?.Invoke(npc) ?? false) &&
            _topics.HasImportantInfo(_world.ConditionsFor(npc));

        /// <summary>
        /// fix-206: <paramref name="speaker"/> has a News entry of its own available now and could be talked to (no dialog
        /// running, not blocked); an invisible talking object then speaks on approach (<c>Myrtana.Rules.SpeakerApproach</c>).
        /// </summary>
        public bool HasNews(ActorId speaker) =>
            !IsActive && !speaker.IsEmpty && !speaker.IsPlayer && !(TalkBlocked?.Invoke(speaker) ?? false) &&
            _topics.HasNews(_world.ConditionsFor(speaker));

        /// <summary>G3 AssessTalk: the NPC starts a conversation with its important entry; false when it has none.</summary>
        public bool TalkFirst(ActorId npc)
        {
            if (!HasImportantInfo(npc)) return false;
            Start(npc);
            return true;
        }

        /// <summary>
        /// G3 ZS_Talk sets this player game event whenever a conversation with the player starts; the Innos/Beliar
        /// statues' News entry (CondPlayerKnows PERM_NEWS) enables their teaching and clears it again.
        /// </summary>
        public const string TalkStartedFlag = "PERM_NEWS";

        void Start(ActorId npc)
        {
            _world.Flags.Set(TalkStartedFlag);
            _conversation = new Conversation(npc);
            _engaged = false;
            _entriesWithoutWaiting = 0;
            var prelude = Prelude?.Invoke(npc);
            _prelude = prelude.HasValue;
            _presenter.Open(npc);
            _world.Events.Publish(new DialogStarted(npc));
            if (prelude.HasValue && _conversation != null)
            {
                _conversation.Phase = ConversationPhase.AwaitingLine;
                _presenter.ShowLine(prelude.Value);
                return;
            }
            _prelude = false;
            Present(_topics.TopLevel(Conditions, AutoPlay.All));
        }

        /// <summary>fix-247: the prelude line is over: the conversation proper starts (its automatic entries, then the menu).</summary>
        void EndPrelude()
        {
            _prelude = false;
            _entriesWithoutWaiting = 0;
            _world.Events.Publish(new DialogPreludeEnded(_conversation.Partner));
            if (_conversation != null) Present(_topics.TopLevel(Conditions, AutoPlay.All));
        }

        public void Choose(Id<DialogEntryDef> entry)
        {
            var c = _conversation;
            if (c == null || c.Phase != ConversationPhase.AwaitingChoice) return;
            if (!c.TryGetOffered(entry, out var chosen)) return;
            _entriesWithoutWaiting = 0;
            Run(chosen);
        }

        public void CompleteLine()
        {
            var c = _conversation;
            if (c == null) return;
            if (c.Phase == ConversationPhase.Running) c.LineFinishedEarly = true;
            else if (c.Phase == ConversationPhase.AwaitingLine && _prelude) EndPrelude();
            else if (c.Phase == ConversationPhase.AwaitingLine)
            {
                _entriesWithoutWaiting = 0;
                Continue();
            }
        }

        /// <summary>
        /// Real time passes (the session's frame): a script paused by G3 InfoWait continues once its time is up. Waits
        /// do not count as input for the loop guard, so an epilogue of many timed slides runs through.
        /// </summary>
        public void Tick(float realDeltaSeconds)
        {
            var c = _conversation;
            if (c == null || c.Phase != ConversationPhase.AwaitingTime || realDeltaSeconds <= 0) return;
            c.WaitSeconds -= realDeltaSeconds;
            if (c.WaitSeconds > 0) return;
            c.WaitSeconds = 0;
            _entriesWithoutWaiting = 0;
            Continue();
        }

        /// <summary>
        /// Ends the conversation now (the hero is taken away: teleport, a cutscene, a tool). G3 has no way to cut an info
        /// short — its InfoGiven is set when it starts, Escape only skips lines — so the running entry's remaining
        /// commands still run, without presenting their lines; otherwise a cut-off Important would count as given while
        /// its quest commands never ran (Gorn's arrival talk at Reddock). A refusal or an open menu just closes.
        /// </summary>
        public void Abort()
        {
            if (!IsActive) return;
            FinishEntrySilently();
            if (IsActive) Close();
        }

        /// <summary>Closes without running anything more: the session is being replaced (load, new game).</summary>
        public void Discard()
        {
            if (IsActive) Close();
        }

        ConditionContext Conditions => _world.ConditionsFor(_conversation.Partner);

        void Present(List<DialogEntryDef> list)
        {
            if (list.Count == 0)
            {
                Close();
                return;
            }
            if (list.Count > 1 && !list[0].PlaysAutomatically)
            {
                _conversation.Offer(list, CostOf);
                _presenter.ShowOptions(_conversation.Options);
                return;
            }
            Run(list[0]);
        }

        void Run(DialogEntryDef entry)
        {
            if (++_entriesWithoutWaiting > MaxEntriesWithoutWaiting)
            {
                _world.Log.Warning($"Dialog with {_conversation.Partner} loops without player input at '{entry.Id}'; conversation closed.");
                Close();
                return;
            }
            var check = new PreCheckContext(_world, _conversation.Partner);
            foreach (var pre in entry.PreChecks)
            {
                if (pre.Passes(check)) continue;
                _conversation.StartScript(entry, pre.Refusal, ScriptPurpose.Refusal);
                Continue();
                return;
            }
            if (!entry.Repeatable) _world.DialogMemory.MarkGiven(entry.Id);
            _conversation.StartScript(entry, entry.Commands, ScriptPurpose.Entry);
            Continue();
        }

        void Continue()
        {
            var c = _conversation;
            var logQuest = c.Purpose == ScriptPurpose.Entry ? c.Entry.LogQuest : default;
            var context = new CommandContext(_world, _presenter, c, c.Partner, c.Entry, logQuest);
            while (c.HasMoreCommands)
            {
                c.Phase = ConversationPhase.Running;
                c.LineFinishedEarly = false;
                var result = _commands.Execute(c.TakeNextCommand(), context);
                if (result == CommandResult.WaitForLine && !c.LineFinishedEarly)
                {
                    c.Phase = ConversationPhase.AwaitingLine;
                    return;
                }
                if (result == CommandResult.WaitForTime && c.WaitSeconds > 0 && _conversation == c)
                {
                    c.Phase = ConversationPhase.AwaitingTime;
                    return;
                }
                if (c.PageHeld && _conversation == c)
                {
                    c.PageHeld = false;
                    c.Phase = ConversationPhase.AwaitingPage;
                    return;
                }
                if (_conversation != c) return; // closed by a command
            }
            if (c.Purpose == ScriptPurpose.Refusal) AfterRefusal(c.Entry);
            else AfterEntry(c.Entry);
        }

        /// <summary>
        /// fix-118: a refused entry did not run, so it has no sub-choices of its own; G3 OnInfoScriptFinished (0x101cd5a0,
        /// "script ran" flag clear) shows its parent's sub-choices again while one other than Back is available (a refused
        /// lesson leaves the hero in the teacher's lesson list), else the top level without automatic entries.
        /// </summary>
        void AfterRefusal(DialogEntryDef entry)
        {
            if (TryPresentSiblings(entry)) return;
            Present(_topics.TopLevel(Conditions, AutoPlay.None));
        }

        /// <summary>
        /// G3 OnInfoScriptFinished: in a sub-menu, an entry without sub-choices of its own shows its parent's sub-choices
        /// again while one (other than Back) is available; a Back entry returns to the top level.
        /// </summary>
        bool TryPresentSiblings(DialogEntryDef entry)
        {
            if (entry == null || !entry.IsSubChoice || entry.Kind == DialogEntryKind.Back) return false;
            var siblings = _topics.SubChoices(entry.Parent, Conditions);
            if (!siblings.Any(s => s.Kind != DialogEntryKind.Back)) return false;
            Present(siblings);
            return true;
        }

        /// <summary>
        /// The price a choice shows after its title, as it is now (G3 GetDialogDescription): a lesson's learning points
        /// and gold, any other entry's gold cost; recomputed every time the menu opens, so it follows the hero.
        /// </summary>
        OptionCost CostOf(DialogEntryDef entry)
        {
            var teaching = _world.Services?.Items?.Teaching;
            foreach (var pre in entry.PreChecks)
                if (pre is LessonCheck lesson && teaching != null)
                {
                    int gain = teaching.AttributeGain(lesson.Lesson);
                    return new OptionCost(true, teaching.LearningPointCost(lesson.Lesson), teaching.GoldCost(lesson.Lesson, lesson.Gold),
                                          gain != lesson.Lesson.LearningPoints && gain > 0 ? gain : 0);
                }
            foreach (var pre in entry.PreChecks)
                if (pre is MinGold gold) return new OptionCost(false, 0, gold.Amount);
            return default;
        }

        void AfterEntry(DialogEntryDef entry)
        {
            var c = _conversation;
            if (c.EndRequested)
            {
                Close();
                return;
            }
            if (entry.ClosesSiblings && entry.IsSubChoice)
                foreach (var sibling in _catalog.ChildrenOf(entry.Parent)) _world.DialogMemory.MarkGiven(sibling.Id);
            if (!c.LeaveSubmenuRequested)
            {
                var subChoices = _topics.SubChoices(entry, Conditions);
                if (subChoices.Count > 0)
                {
                    Present(subChoices);
                    return;
                }
                if (TryPresentSiblings(entry)) return;
            }
            Present(_topics.TopLevel(Conditions, AutoPlay.News));
        }

        void FinishEntrySilently()
        {
            var c = _conversation;
            if (c.Purpose != ScriptPurpose.Entry || c.Entry == null || !c.HasMoreCommands) return;
            var context = new CommandContext(_world, SilentPresenter.Instance, c, c.Partner, c.Entry, c.Entry.LogQuest);
            _finishingSilently = true;
            try
            {
                for (int guard = 0; c.HasMoreCommands && _conversation == c && guard < 4096; guard++)
                {
                    c.Phase = ConversationPhase.Running;
                    var command = c.TakeNextCommand();
                    if (command is SaySound || command is ShowSubtitle) continue; // presentation only
                    _commands.Execute(command, context);
                    c.WaitSeconds = 0;
                    c.PageHeld = false;
                }
            }
            finally { _finishingSilently = false; }
        }

        /// <summary>Takes the lines of a script finished by <see cref="Abort"/>: nothing is shown or voiced.</summary>
        /// <summary>
        /// fix-76: passes everything to the real presenter and publishes <see cref="DialogEngaged"/> before the first line
        /// or the first menu with a choice that is not an exit (G3 starts its dialog camera in SetDialogRoles for the first
        /// line, or in ShowDialogManager when the choice menu opens; Docs/dialog-presentation.md §1.2).
        /// </summary>
        sealed class EngagingPresenter : IDialogPresenter
        {
            readonly DialogService _service;
            readonly IDialogPresenter _inner;

            public EngagingPresenter(DialogService service, IDialogPresenter inner)
            {
                _service = service;
                _inner = inner ?? SilentPresenter.Instance;
            }

            public void Open(ActorId partner) => _inner.Open(partner);

            public void ShowLine(DialogLine line)
            {
                _service.Engage();
                _inner.ShowLine(line);
            }

            public void ShowOptions(IReadOnlyList<DialogOption> options)
            {
                if (options != null && options.Any(o => !o.IsExit)) _service.Engage();
                _inner.ShowOptions(options);
            }

            public void Close() => _inner.Close();
        }

        void Engage()
        {
            if (_conversation == null || _engaged) return;
            _engaged = true;
            _world.Events.Publish(new DialogEngaged(_conversation.Partner));
        }

        sealed class SilentPresenter : IDialogPresenter
        {
            public static readonly SilentPresenter Instance = new SilentPresenter();
            public void Open(ActorId partner) { }
            public void ShowLine(DialogLine line) { }
            public void ShowOptions(IReadOnlyList<DialogOption> options) { }
            public void Close() { }
        }

        void Close()
        {
            var partner = _conversation.Partner;
            _conversation = null;
            _engaged = false;
            _prelude = false;
            _world.Actors.MarkTalkedToPlayer(partner);
            _world.DialogMemory.MarkDialogEnd(partner, _world.Quests.GameSeconds);
            _presenter.Close();
            _world.Events.Publish(new DialogEnded(partner));
        }
    }
}
