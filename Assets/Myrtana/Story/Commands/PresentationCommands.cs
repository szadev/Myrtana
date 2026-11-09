using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Story
{
    // G3 cutscene / epilogue commands (IS_Running_ProcessInfo; Docs/story-coverage.md): the story publishes what is to be
    // shown or heard as events; the presentation (Unity: StoryCutsceneView) shows the slideshow picture and subtitle, triggers
    // the music situation, plays the effect and the end credits. Only InfoWait changes the conversation's own flow.

    /// <summary>G3 ShowPicture: the full-screen slideshow page shows this picture (<c>G3_Epilog_NN.dds</c>).</summary>
    public sealed class ShowPicture : DialogCommand
    {
        [JsonConstructor]
        public ShowPicture(string image) => Image = image;

        /// <summary>The picture's G3 file name as the info names it (with extension).</summary>
        public string Image { get; }
    }

    /// <summary>G3 ShowSubtitle: the slideshow page's subtitle shows this text.</summary>
    public sealed class ShowSubtitle : DialogCommand
    {
        [JsonConstructor]
        public ShowSubtitle(TextId text) => Text = text;

        public TextId Text { get; }
    }

    /// <summary>G3 InfoWait: the script pauses for this many milliseconds (real time) before the next line.</summary>
    public sealed class WaitInScript : DialogCommand
    {
        [JsonConstructor]
        public WaitInScript(int milliseconds) => Milliseconds = milliseconds;

        public int Milliseconds { get; }
    }

    /// <summary>G3 PlayMusic: Music::TriggerSituation(ID1), e.g. "Extro" in the epilogue.</summary>
    public sealed class PlayMusic : DialogCommand
    {
        [JsonConstructor]
        public PlayMusic(string situation) => Situation = situation;

        public string Situation { get; }
    }

    /// <summary>G3 EndGame: the end credits (G3_Credits2, then G3_Credits), then the application ends.</summary>
    public sealed class EndGame : DialogCommand
    {
    }

    /// <summary>G3 StartEffect: the named effect starts at a world entity (an actor or a named object such as "Erzofen_Effekt").</summary>
    public sealed class StartEffect : DialogCommand
    {
        [JsonConstructor]
        public StartEffect(ActorRef at, string effect)
        {
            At = at;
            Effect = effect;
        }

        public ActorRef At { get; }
        public string Effect { get; }
    }

    /// <summary>The slideshow page shows a picture (until the conversation ends or the next picture).</summary>
    public readonly struct StoryPictureShown : IGameEvent
    {
        public StoryPictureShown(string image) => Image = image;
        public string Image { get; }
    }

    /// <summary>The slideshow page's subtitle changes.</summary>
    public readonly struct StorySubtitleShown : IGameEvent
    {
        public StorySubtitleShown(TextId text) => Text = text;
        public TextId Text { get; }
    }

    /// <summary>The story asks for a music situation ("Extro").</summary>
    public readonly struct StoryMusicRequested : IGameEvent
    {
        public StoryMusicRequested(string situation) => Situation = situation;
        public string Situation { get; }
    }

    /// <summary>The game is over (G3 EndGame): the presentation plays the end credits and leaves the game.</summary>
    public readonly struct GameEndRequested : IGameEvent
    {
    }

    /// <summary>
    /// The story starts a named effect at a world entity (G3 EffectSystem::StartEffect). <see cref="At"/> is an actor id
    /// or the name of a world entity; the effect player (Unity) finds where it is.
    /// </summary>
    public readonly struct StoryEffectStarted : IGameEvent
    {
        public StoryEffectStarted(string effect, string at)
        {
            Effect = effect;
            At = at;
        }

        public string Effect { get; }
        public string At { get; }
    }

    public sealed class ShowPictureHandler : CommandHandler<ShowPicture>
    {
        protected override CommandResult Execute(ShowPicture command, CommandContext context)
        {
            context.Events.Publish(new StoryPictureShown(command.Image));
            return CommandResult.Continue;
        }
    }

    public sealed class ShowSubtitleHandler : CommandHandler<ShowSubtitle>
    {
        protected override CommandResult Execute(ShowSubtitle command, CommandContext context)
        {
            context.Events.Publish(new StorySubtitleShown(command.Text));
            return CommandResult.Continue;
        }
    }

    public sealed class WaitInScriptHandler : CommandHandler<WaitInScript>
    {
        protected override CommandResult Execute(WaitInScript command, CommandContext context)
        {
            if (command.Milliseconds <= 0) return CommandResult.Continue;
            context.Conversation.RequestWait(command.Milliseconds / 1000f);
            return CommandResult.WaitForTime;
        }
    }

    public sealed class PlayMusicHandler : CommandHandler<PlayMusic>
    {
        protected override CommandResult Execute(PlayMusic command, CommandContext context)
        {
            if (!string.IsNullOrEmpty(command.Situation)) context.Events.Publish(new StoryMusicRequested(command.Situation));
            return CommandResult.Continue;
        }
    }

    public sealed class EndGameHandler : CommandHandler<EndGame>
    {
        protected override CommandResult Execute(EndGame command, CommandContext context)
        {
            context.Conversation.RequestEnd();
            context.Events.Publish(new GameEndRequested());
            return CommandResult.Continue;
        }
    }

    /// <summary>
    /// G3 SaySound: the speaker "says" a sound (a cow's moo, the wolf Grauhaar's growl) instead of a voice line; the
    /// script waits for it.
    /// </summary>
    public sealed class SaySound : DialogCommand
    {
        /// <summary>How long the script waits for the sound (seconds) [interp: G3 waits for the sample's end].</summary>
        public const float WaitSeconds = 1.5f;

        [JsonConstructor]
        public SaySound(ActorRef speaker, string sound)
        {
            Speaker = speaker;
            Sound = sound;
        }

        public ActorRef Speaker { get; }
        /// <summary>The G3 sample name ("sfx_creature_cow_moo_03").</summary>
        public string Sound { get; }
    }

    /// <summary>A story sound plays at an actor (SaySound); the presentation plays the sample there.</summary>
    public readonly struct StorySoundRequested : IGameEvent
    {
        public StorySoundRequested(string sound, string at)
        {
            Sound = sound;
            At = at;
        }

        public string Sound { get; }
        public string At { get; }
    }

    public sealed class SaySoundHandler : CommandHandler<SaySound>
    {
        protected override CommandResult Execute(SaySound command, CommandContext context)
        {
            if (string.IsNullOrEmpty(command.Sound)) return CommandResult.Continue;
            context.Events.Publish(new StorySoundRequested(command.Sound, context.Resolve(command.Speaker).Name));
            context.Conversation.RequestWait(SaySound.WaitSeconds);
            return CommandResult.WaitForTime;
        }
    }

    public sealed class StartEffectHandler : CommandHandler<StartEffect>
    {
        protected override CommandResult Execute(StartEffect command, CommandContext context)
        {
            if (!string.IsNullOrEmpty(command.Effect))
                context.Events.Publish(new StoryEffectStarted(command.Effect, context.Resolve(command.At).Name));
            return CommandResult.Continue;
        }
    }
}
