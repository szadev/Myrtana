using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Story
{
    /// <summary>
    /// Shows a conversation: the Unity dialog view, the G3Cli text player, a recorder in tests. Every call carries text
    /// ids, so the presenter resolves them in the current language when it displays them.
    /// </summary>
    public interface IDialogPresenter
    {
        void Open(ActorId partner);
        /// <summary>Present a spoken line; call <see cref="DialogService.CompleteLine"/> when it has finished (voice ended or skipped).</summary>
        void ShowLine(DialogLine line);
        /// <summary>Let the player choose; answer with <see cref="DialogService.Choose"/>.</summary>
        void ShowOptions(IReadOnlyList<DialogOption> options);
        void Close();
    }

    public readonly struct DialogLine
    {
        public DialogLine(ActorId speaker, TextId text, string voice, string stage = null)
        {
            Speaker = speaker;
            Text = text;
            Voice = voice;
            Stage = stage;
        }

        /// <summary>Stage direction (<c>SD_*</c>) choosing the talk gesture, or null (random gesture).</summary>
        public string Stage { get; }

        public ActorId Speaker { get; }
        public TextId Text { get; }
        /// <summary>Voice clip key, or null.</summary>
        public string Voice { get; }
    }

    public readonly struct DialogOption
    {
        public DialogOption(Id<DialogEntryDef> entry, TextId title, bool isExit, OptionCost cost = default)
        {
            Entry = entry;
            Title = title;
            IsExit = isExit;
            Cost = cost;
        }

        public Id<DialogEntryDef> Entry { get; }
        public TextId Title { get; }
        public bool IsExit { get; }
        /// <summary>What the choice costs now (lesson price, gold cost), shown after its title (<see cref="DialogOptionText"/>).</summary>
        public OptionCost Cost { get; }
    }

    /// <summary>
    /// The price a menu choice names after its title (G3 gCInfo_PS::GetDialogDescription 0x101c60b0): a teach entry its
    /// learning points and gold (both as they cost now: Alternative Balancing doubles them for STR/DEX/INT ≥ 250), any
    /// other entry its gold cost. <see cref="AttributeGain"/> &gt; 0: a lesson of an attribute raised by more than one
    /// point per learning point (HP/MP/SP ×4), whose text names the points gained instead of the learning points.
    /// </summary>
    public readonly struct OptionCost
    {
        public OptionCost(bool isLesson, int learningPoints, int gold, int attributeGain = 0)
        {
            IsLesson = isLesson;
            LearningPoints = learningPoints;
            Gold = gold;
            AttributeGain = attributeGain;
        }

        public bool IsLesson { get; }
        public int LearningPoints { get; }
        public int Gold { get; }
        public int AttributeGain { get; }

        public bool IsEmpty => !IsLesson && Gold <= 0 && AttributeGain <= 0;
    }

    /// <summary>The menu text of a choice: its title and its price (G3 gCInfo_PS::GetDialogDescription).</summary>
    public static class DialogOptionText
    {
        public static readonly TextId LearningPointsWord = new TextId("GO_LP");
        public static readonly TextId GoldWord = new TextId("GO_GoldCost");

        /// <summary>
        /// "Сила +1 (1 Оч. обучения, 50 Золото)", "… (100 Золото)"; an HP/MP/SP lesson drops the digits 1 and 5 from its
        /// title and appends the points gained ("Здоровье +5" → "Здоровье +20"), as G3 does. <paramref name="text"/>
        /// resolves GO_LP / GO_GoldCost in the current language.
        /// </summary>
        public static string Format(string title, OptionCost cost, System.Func<TextId, string> text)
        {
            title ??= "";
            if (cost.IsEmpty) return title;
            if (cost.AttributeGain > 0) title = title.Replace("1", "").Replace("5", "") + cost.AttributeGain;
            var parts = new List<string>(2);
            if (cost.IsLesson && cost.LearningPoints > 0) parts.Add(cost.LearningPoints + " " + text(LearningPointsWord));
            if (cost.Gold > 0) parts.Add(cost.Gold + " " + text(GoldWord));
            return parts.Count == 0 ? title : title + " (" + string.Join(", ", parts) + ")";
        }
    }

    /// <summary>
    /// fix-158: the player tried to talk to someone with nothing to say (G3 AssessTalk: <c>HasInfo</c> false →
    /// <c>StartSayRefuseTalk</c>, a shared refusal line and no dialog). The session picks and plays the line.
    /// </summary>
    public readonly struct TalkRefused : IGameEvent
    {
        public TalkRefused(ActorId npc) => Npc = npc;
        public ActorId Npc { get; }
    }

    /// <summary>Published when a conversation starts or ends (views use it to lock input, turn the NPC …).</summary>
    public readonly struct DialogStarted : IGameEvent
    {
        public DialogStarted(ActorId partner) => Partner = partner;
        public ActorId Partner { get; }
    }

    /// <summary>
    /// fix-247: the partner's prelude line (<see cref="DialogService.Prelude"/>: a woken sleeper's "YouDisturbedMySlumber")
    /// is over; it stands up now and the conversation proper begins (G3 ZS_Talk: say, _AI_StandUp, then the infos).
    /// </summary>
    public readonly struct DialogPreludeEnded : IGameEvent
    {
        public DialogPreludeEnded(ActorId partner) => Partner = partner;
        public ActorId Partner { get; }
    }

    /// <summary>
    /// fix-76: the conversation shows its first line or a menu with a real choice: the dialog camera takes over and hero
    /// and partner turn to each other now, not at the talk key (a talk with nothing to say only runs its exit and never
    /// engages: no camera cut, no turn). Published at most once per conversation, between DialogStarted and DialogEnded.
    /// </summary>
    public readonly struct DialogEngaged : IGameEvent
    {
        public DialogEngaged(ActorId partner) => Partner = partner;
        public ActorId Partner { get; }
    }

    public readonly struct DialogEnded : IGameEvent
    {
        public DialogEnded(ActorId partner) => Partner = partner;
        public ActorId Partner { get; }
    }
}
