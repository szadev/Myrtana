namespace Myrtana.Story
{
    public sealed class SayHandler : CommandHandler<Say>
    {
        protected override CommandResult Execute(Say command, CommandContext context)
        {
            var line = new DialogLine(context.Resolve(command.Speaker), command.Text, command.Voice, command.Stage);
            context.Events.Publish(new DialogLineSpoken(context.Entry?.Id ?? default, context.LogQuest, line.Speaker, line.Text));
            context.Presenter.ShowLine(line);
            return CommandResult.WaitForLine;
        }
    }

    public sealed class SetFlagHandler : CommandHandler<SetFlag>
    {
        protected override CommandResult Execute(SetFlag command, CommandContext context)
        {
            context.Flags.Set(command.Flag);
            return CommandResult.Continue;
        }
    }

    public sealed class ClearFlagHandler : CommandHandler<ClearFlag>
    {
        protected override CommandResult Execute(ClearFlag command, CommandContext context)
        {
            context.Flags.Clear(command.Flag);
            return CommandResult.Continue;
        }
    }

    public sealed class StartQuestHandler : CommandHandler<StartQuest>
    {
        protected override CommandResult Execute(StartQuest command, CommandContext context)
        {
            context.Quests.TrySetStatus(command.Quest, QuestStatus.Running);
            return CommandResult.Continue;
        }
    }

    public sealed class EndDialogHandler : CommandHandler<EndDialog>
    {
        protected override CommandResult Execute(EndDialog command, CommandContext context)
        {
            context.Conversation.RequestEnd();
            return CommandResult.Continue;
        }
    }

    public sealed class LeaveSubmenuHandler : CommandHandler<LeaveSubmenu>
    {
        protected override CommandResult Execute(LeaveSubmenu command, CommandContext context)
        {
            context.Conversation.RequestLeaveSubmenu();
            return CommandResult.Continue;
        }
    }

    public sealed class NotSupportedHandler : CommandHandler<NotSupported>
    {
        protected override CommandResult Execute(NotSupported command, CommandContext context)
        {
            context.Log.Info($"Dialog command '{command.Command} {command.Arguments}' is not supported yet; skipped.");
            return CommandResult.Continue;
        }
    }
}
