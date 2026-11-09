using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// What a screen message reports; the HUD colours it (gEGameMessageType colour table, Docs/formats-gui.md §6.3): gold
    /// for most, tan for items taken or given (types 2/3/6), teal results, orange quests, red errors.
    /// </summary>
    public enum NoticeKind { Info, Result, Quest, Error, Item }

    /// <summary>
    /// Turns game events into G3's short screen messages (GO_ strings): new quest, quest solved/failed/cancelled,
    /// experience, level up. The HUD shows <see cref="Posted"/> texts in order.
    /// </summary>
    public sealed class GameNotices : IDisposable
    {
        static readonly TextId QuestRunning = new TextId("GO_QuestRunning"), QuestSuccess = new TextId("GO_QuestSuccess"),
                               QuestFailure = new TextId("GO_QuestFailure"), QuestCancel = new TextId("GO_QuestCancel"),
                               Experience = new TextId("GO_XP"), LevelUp = new TextId("GO_LevelUp"),
                               ItemTaken = new TextId("GO_ItemTaken_Formatted"), ItemGiven = new TextId("GO_ItemGiven"),
                               GoldTaken = new TextId("GO_GoldTaken"), GoldGiven = new TextId("GO_GoldGiven"),
                               Learned = new TextId("GO_Learned"), Mining = new TextId("GO_Mining"),
                               LearningPointsLeft = new TextId("GO_LearningPointsLeft"),
                               MissingSkill = new TextId("HUD_MissingSkill"), MissingSpell = new TextId("HUD_MissingSpell"),
                               MissingAttribute = new TextId("HUD_MissingAttrib"),
                               PickpocketCaught = new TextId(PickpocketCaughtText);

        /// <summary>fix-248 [port]: the notice of a failed pickpocket (a patch string; G3 shows none).</summary>
        public const string PickpocketCaughtText = "MYR_PickpocketCaught";

        readonly GameSession _session;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        public GameNotices(GameSession session)
        {
            _session = session;
            _subscriptions.Add(session.Events.Subscribe<QuestStatusChanged>(OnQuest));
            _subscriptions.Add(session.Events.Subscribe<XpAwarded>(e => Post($"{Text(Experience)} +{e.Amount}", NoticeKind.Info)));
            _subscriptions.Add(session.Events.Subscribe<LevelReached>(e => Post(Text(LevelUp), NoticeKind.Info)));
            _subscriptions.Add(session.Events.Subscribe<GameMessage>(e => Post(Named(Text(e.Text), e.Subject), NoticeKind.Info)));
            _subscriptions.Add(session.Events.Subscribe<PartyChanged>(e =>
            {
                if (!e.Message.IsEmpty) Post(Named(Text(e.Message), e.Member), NoticeKind.Info);
            }));
            _subscriptions.Add(session.Events.Subscribe<ItemsTransferred>(OnItems));
            // fix-205: a quick loot announces its moves grouped once it ends ("Got: …" per item, gold summed)
            _subscriptions.Add(session.Events.Subscribe<QuickLootStarted>(_ => _quickLoot = new List<(string, int)>()));
            _subscriptions.Add(session.Events.Subscribe<QuickLootFinished>(_ => FlushQuickLoot()));
            // CP 1.75 ConsumeItems / GeneratePickOreInventory (par-interact)
            _subscriptions.Add(session.Events.Subscribe<ItemEffectAnnounced>(e => Post(ConsumeText(e), NoticeKind.Info)));
            _subscriptions.Add(session.Events.Subscribe<SkillLearnedFromItem>(e => Post(Text(Learned) + Text(new TextId("FO_It_" + e.Skill)), NoticeKind.Info)));
            // G3 Add<Attribute> → PSPlayerMemory::PrintAddValue (bookstands, orc boulders, BoostAttribs, teaching): type 15
            _subscriptions.Add(session.Events.Subscribe<AttributeRaised>(e => Post(RaiseText(e), NoticeKind.Info)));
            _subscriptions.Add(session.Events.Subscribe<LessonTaught>(OnLesson));
            _subscriptions.Add(session.Events.Subscribe<LearnRequirementsMissing>(e =>
            {
                foreach (var shortfall in e.Shortfalls) Post(ShortfallText(shortfall), NoticeKind.Info);
            }));
            _subscriptions.Add(session.Events.Subscribe<PickpocketAttempted>(OnPickpocket));
            _subscriptions.Add(session.Events.Subscribe<OreMined>(e => Post(Text(Mining).Replace("$(value)", e.Amount.ToString())
                                                                                   .Replace("$(name)", ItemName(e.Item)), NoticeKind.Info)));
        }

        /// <summary>gCPlayerMemory_PS::Consume: "&lt;ATTRIB_x[Max]&gt; = old + n".</summary>
        string ConsumeText(ItemEffectAnnounced e)
        {
            string name = _session.Content.Database.TryGet(e.Attribute, out AttributeDef a)
                ? Text(e.Maximum && !a.MaximumName.IsEmpty ? a.MaximumName : a.Name)
                : Text(new TextId("ATTRIB_" + e.Attribute + (e.Maximum ? "Max" : "")));
            return $"{name} = {e.OldValue} + {e.Amount}";
        }

        /// <summary>PSPlayerMemory::PrintAddValue (Script 0x10011770): long name + Format(" + %d = %d", amount, value).</summary>
        string RaiseText(AttributeRaised e)
        {
            string name = _session.Content.Database.TryGet(e.Attribute, out AttributeDef a) ? Text(a.Name) : Text(new TextId("ATTRIB_" + e.Attribute));
            return $"{name} + {e.Amount} = {e.Value}";
        }

        /// <summary>
        /// gCInfo_PS::OnEndInfo (Game 0x101c1270) after a lesson: a skill or spell prints "GO_Learned" + FO_It_&lt;name&gt;
        /// (type 5), then "GO_LearningPointsLeft" with $(value) (type 5 after a skill, 15 after an attribute).
        /// </summary>
        void OnLesson(LessonTaught e)
        {
            var kind = e.Skill != null ? NoticeKind.Result : NoticeKind.Info;
            if (e.Skill != null) Post(Text(Learned) + Text(new TextId("FO_It_" + e.Skill)), kind);
            Post(Text(LearningPointsLeft).Replace("$(value)", e.LearningPointsLeft.ToString()), kind);
        }

        /// <summary>
        /// fix-248 [port]: G3 only plays the hero's "TooHard" / "Impossible" for a too strong victim and only lets a victim
        /// that caught the hero shout and attack; the HUD names the missing perk (HUD_MissingSkill, as a lesson's) or says
        /// "You were caught stealing!".
        /// </summary>
        void OnPickpocket(PickpocketAttempted e)
        {
            if (e.Verdict == PickpocketVerdict.Caught) Post(Text(PickpocketCaught), NoticeKind.Error);
            else if (e.MissingPerk != null) Post(ShortfallText(new LearnShortfall(LearnShortfallKind.Skill, e.MissingPerk, 0)), NoticeKind.Error);
        }

        /// <summary>G3 CanLearnSkill: "Missing skill: &lt;FO_It_skill&gt;" or "You are lacking &lt;n&gt; &lt;ATTRIB_x&gt; points".</summary>
        public string ShortfallText(LearnShortfall s)
        {
            if (s.Kind != LearnShortfallKind.Attribute)
                return Text(s.Kind == LearnShortfallKind.Spell ? MissingSpell : MissingSkill).Replace("$(name)", Text(new TextId("FO_It_" + s.Id)));
            string name = _session.Content.Database.TryGet(s.Id, out AttributeDef a) && !a.Name.IsEmpty ? Text(a.Name) : Text(new TextId("ATTRIB_" + s.Id));
            return Text(MissingAttribute).Replace("$(value)", s.Missing.ToString()).Replace("$(name)", name).TrimEnd();
        }

        string ItemName(string item) =>
            _session.Content.Database.TryGet(item, out ItemDef def) ? ItemLists.Name(_session, def) : item;

        public event Action<string, NoticeKind> Posted;

        /// <summary>While this returns true, item moves are not announced (the trade screen shows them itself).</summary>
        public Func<bool> MuteItems { get; set; }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }

        void OnQuest(QuestStatusChanged e)
        {
            var message = MessageFor(e.To);
            if (message.IsEmpty) return;
            string title = _session.Quests.TryGetDefinition(e.Quest, out var def) && !def.Title.IsEmpty ? Text(def.Title) : e.Quest.Name;
            Post($"{Text(message)}: {title}", NoticeKind.Quest);
        }

        /// <summary>Items an NPC gives the hero or takes from him (dialog "Give"); pickups announce themselves.</summary>
        void OnItems(ItemsTransferred e)
        {
            if (MuteItems?.Invoke() == true || e.From.IsEmpty || e.To.IsEmpty) return;
            bool toHero = e.To == ActorId.Player;
            if (!toHero && e.From != ActorId.Player) return;
            if (toHero && _quickLoot != null)
            {
                _quickLoot.Add((e.Item, e.Amount)); // fix-205: grouped at QuickLootFinished
                return;
            }
            PostItem(e.Item, e.Amount, toHero);
        }

        void PostItem(string item, int amount, bool toHero)
        {
            if (item == _session.Items.GoldItem)
            {
                Post($"{Text(toHero ? GoldTaken : GoldGiven)}: {amount}", NoticeKind.Item);
                return;
            }
            string name = _session.Content.Database.TryGet(item, out ItemDef def) ? ItemLists.Name(_session, def) : item;
            Post(toHero ? Text(ItemTaken).Replace("$(amount)", amount.ToString()).Replace("$(name)", name)
                        : $"{amount} x {name} {Text(ItemGiven)}", NoticeKind.Item); // gCInfo_PS::Give types 2/3, quick loot 6: E0C495
        }

        /// <summary>The moves of a quick loot (<see cref="QuickLoot"/>), one line per item and one gold line.</summary>
        List<(string item, int amount)> _quickLoot;

        void FlushQuickLoot()
        {
            var moves = _quickLoot;
            _quickLoot = null;
            if (moves == null || moves.Count == 0) return;
            var lines = QuickLoot.Group(moves, _session.Items.GoldItem, out int gold);
            foreach (var line in lines) PostItem(line.Item, line.Amount, toHero: true);
            if (gold > 0) PostItem(_session.Items.GoldItem, gold, toHero: true);
        }

        static TextId MessageFor(QuestStatus status)
        {
            switch (status)
            {
                case QuestStatus.Running: return QuestRunning;
                case QuestStatus.Success:
                case QuestStatus.Won: return QuestSuccess;
                case QuestStatus.Failed: return QuestFailure;
                case QuestStatus.Cancelled: return QuestCancel;
                default: return default;
            }
        }

        string Text(TextId id) => _session.Localizer.Text(id);

        /// <summary>G3's <c>$(name)</c> in a message: the actor's display name.</summary>
        string Named(string text, ActorId actor)
        {
            if (actor.IsEmpty || text == null || text.IndexOf("$(name)", StringComparison.Ordinal) < 0) return text;
            string name = _session.Actors.TryGet(actor, out var state) && !state.Definition.DisplayName.IsEmpty
                ? Text(state.Definition.DisplayName) : actor.Name;
            return text.Replace("$(name)", name);
        }

        void Post(string text, NoticeKind kind) => Posted?.Invoke(text, kind);
    }
}
