using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    public enum CommandResult
    {
        /// <summary>Done; run the next command.</summary>
        Continue,
        /// <summary>A line is being presented; continue when the presenter reports it finished.</summary>
        WaitForLine,
        /// <summary>The script pauses (G3 InfoWait); <see cref="DialogService.Tick"/> continues it when the time is up.</summary>
        WaitForTime,
    }

    /// <summary>Executes one command type.</summary>
    public interface ICommandHandler
    {
        CommandResult Execute(DialogCommand command, CommandContext context);
    }

    /// <summary>Typed base so handlers do not cast.</summary>
    public abstract class CommandHandler<T> : ICommandHandler where T : DialogCommand
    {
        public CommandResult Execute(DialogCommand command, CommandContext context) => Execute((T)command, context);
        protected abstract CommandResult Execute(T command, CommandContext context);
    }

    /// <summary>What the running conversation lets a command change about itself.</summary>
    public interface IConversationControl
    {
        void RequestEnd();
        void RequestLeaveSubmenu();
        /// <summary>Runs <paramref name="commands"/> right after the current command.</summary>
        void RunNext(IReadOnlyList<DialogCommand> commands);
        /// <summary>The script pauses for this many real seconds after the current command (G3 InfoWait).</summary>
        void RequestWait(float seconds);
    }

    /// <summary>Everything a command handler may use: the story world, the presenter and the running conversation.</summary>
    public sealed class CommandContext
    {
        public CommandContext(StoryWorld world, IDialogPresenter presenter, IConversationControl conversation,
                              ActorId partner, DialogEntryDef entry, Id<QuestDef> logQuest)
        {
            World = world;
            Presenter = presenter;
            Conversation = conversation;
            Partner = partner;
            Entry = entry;
            LogQuest = logQuest;
        }

        public StoryWorld World { get; }
        public IDialogPresenter Presenter { get; }
        public IConversationControl Conversation { get; }
        public ActorId Partner { get; }
        /// <summary>The dialog entry whose script runs.</summary>
        public DialogEntryDef Entry { get; }
        /// <summary>The quest whose log records spoken lines (empty for refusals and entries without quest).</summary>
        public Id<QuestDef> LogQuest { get; }

        public IGameEventFlags Flags => World.Flags;
        public QuestService Quests => World.Quests;
        public StoryServices Services => World.Services;
        public IEventBus Events => World.Events;
        public IGameLog Log => World.Log;

        public ActorId Resolve(ActorRef actor) => actor.Resolve(Partner);
    }

    /// <summary>Dispatches commands to the handler registered for their type.</summary>
    public sealed class CommandRunner
    {
        readonly HandlerRegistry<Type, ICommandHandler> _handlers = new HandlerRegistry<Type, ICommandHandler>();

        public void Register<T>(CommandHandler<T> handler) where T : DialogCommand => _handlers.Register(typeof(T), handler);

        public CommandResult Execute(DialogCommand command, CommandContext context)
        {
            if (_handlers.TryGet(command.GetType(), out var handler)) return handler.Execute(command, context);
            context.Log.Warning($"No handler for dialog command {command.GetType().Name}; skipped.");
            return CommandResult.Continue;
        }

        /// <summary>True when a handler is registered for the command type (coverage reports).</summary>
        public bool Handles(Type commandType) => _handlers.TryGet(commandType, out _);

        /// <summary>A runner with the handlers of every command type the runtime supports.</summary>
        /// <param name="sharedLines">How shared (SVM) line ids are named; null = shared lines are skipped.</param>
        public static CommandRunner CreateDefault(SharedLines sharedLines = null)
        {
            var runner = new CommandRunner();
            runner.Register(new SayHandler());
            runner.Register(new SetFlagHandler());
            runner.Register(new ClearFlagHandler());
            runner.Register(new StartQuestHandler());
            runner.Register(new EndDialogHandler());
            runner.Register(new LeaveSubmenuHandler());
            runner.Register(new NotSupportedHandler());
            runner.Register(new SucceedQuestHandler());
            runner.Register(new CloseQuestHandler());
            runner.Register(new FailQuestHandler());
            runner.Register(new ReportToQuestHandler());
            runner.Register(new DeliverQuestItemsHandler());
            runner.Register(new AddQuestLogHandler());
            runner.Register(new GiveXpHandler());
            runner.Register(new BoostAttributesHandler());
            runner.Register(new GiveItemsHandler());
            runner.Register(new SetRoleHandler());
            runner.Register(new OpenTradeHandler());
            runner.Register(new TeachLessonHandler());
            runner.Register(new ReportLearnShortfallsHandler());
            runner.Register(new SaySharedHandler(() => sharedLines));
            runner.Register(new ShowMessageHandler());
            runner.Register(new SayGuardNewsHandler());
            runner.Register(new SayBossNewsHandler());
            runner.Register(new BribeHandler());
            runner.Register(new ExcuseHandler());
            runner.Register(new ResetCrimeHandler());
            runner.Register(new SetChapterHandler());
            runner.Register(new StartFightHandler());
            runner.Register(new SetRoutineHandler());
            runner.Register(new SetAlignmentHandler());
            runner.Register(new JoinPartyHandler());
            runner.Register(new WaitPartyHandler());
            runner.Register(new LeavePartyHandler());
            runner.Register(new CompleteDeliveryHandler());
            runner.Register(new PartyRefusalHandler());
            runner.Register(new HealActorHandler());
            runner.Register(new SleepHandler());
            runner.Register(new RemoveItemsHandler());
            runner.Register(new GiveAllItemsHandler());
            runner.Register(new UseItemHandler());
            runner.Register(new ShowPictureHandler());
            runner.Register(new ShowSubtitleHandler());
            runner.Register(new WaitInScriptHandler());
            runner.Register(new PlayMusicHandler());
            runner.Register(new EndGameHandler());
            runner.Register(new StartEffectHandler());
            runner.Register(new SaySoundHandler());
            WorldStateHandlers.Register(runner);
            NpcControlHandlers.Register(runner);
            return runner;
        }
    }
}
