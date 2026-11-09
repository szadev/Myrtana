using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Tests
{
    /// <summary>In-memory story world for dialog tests: content built in code, a recording presenter, fake reputation.</summary>
    public sealed class StoryFixture
    {
        public static readonly ActorId Npc = new ActorId("Gnar");

        readonly List<DialogEntryDef> _entries = new List<DialogEntryDef>();
        readonly ContentDatabase _db = new ContentDatabase();
        DialogService _dialog;

        public StoryFixture()
        {
            _db.Put(new NpcDef(Npc, new TextId("FO_Gnar"), "Orc_Warrior_Hard"));
            _db.Put(new NpcDef(ActorId.Player, new TextId("FO_PC_Hero"), "Player"));
            foreach (var attribute in TestRules.Attributes()) _db.Put(attribute);
            _db.Put(TestRules.Items());
            _db.Put(TestRules.Trade());
            _db.Put(TestRules.Teaching());
        }

        public RecordingPresenter Presenter { get; } = new RecordingPresenter();
        public GameEventFlags Flags { get; } = new GameEventFlags();
        public FakeReputation Reputation { get; } = new FakeReputation();
        public ListLog Log { get; } = new ListLog();
        public EventBus Events { get; } = new EventBus();
        /// <summary>CP Alternative Balancing for the quest counters (set before the first <see cref="Dialog"/>).</summary>
        public bool AlternativeBalancing { get; set; }
        public ActorRegistry Actors { get; private set; }
        public QuestService Quests { get; private set; }
        public DialogMemory Memory { get; } = new DialogMemory();
        public QuestLog QuestLog { get; private set; }
        public FameLedger Fame { get; } = new FameLedger();
        public Hero Hero { get; private set; }
        public ProgressionService Progression { get; private set; }
        public StoryWorld World { get; private set; }
        public ItemServices Items { get; private set; }
        public RecordingDirector Director { get; } = new RecordingDirector();
        /// <summary>World sectors, doors, enclaves (set before the first use of <see cref="Dialog"/>; null = none).</summary>
        public IWorldDirector WorldDirector { get; set; }
        /// <summary>Humanoid / can-fight facts (null = every actor a fighting humanoid).</summary>
        public IActorTraits Traits { get; set; }
        /// <summary>Pickpocketing (null = nobody can be robbed).</summary>
        public IPickpocket Pickpocket { get; set; }
        public GameClock Clock { get; } = new GameClock(startHour: 8f);
        /// <summary>Positions for owner-near and delivery conditions (set before the first use of <see cref="Dialog"/>).</summary>
        public FakePositions Positions { get; } = new FakePositions();
        /// <summary>The hero's party (valid after the first use of <see cref="Dialog"/>).</summary>
        public PlayerParty Party { get; private set; }

        /// <summary>Adds any definition (items, rules, merchants …) before the first use of <see cref="Dialog"/>.</summary>
        public StoryFixture Define<T>(T definition) where T : class, IDefinition
        {
            _db.Put(definition);
            return this;
        }

        public DialogService Dialog => _dialog ?? (_dialog = Build());

        public StoryFixture Quest(QuestDef quest)
        {
            _db.Put(quest);
            return this;
        }

        public StoryFixture Quest(string id, params string[] prerequisites)
        {
            _db.Put(new QuestDef(new Id<QuestDef>(id), new TextId("QUEST_" + id), prerequisites.Select(p => new Id<QuestDef>(p)).ToList()));
            return this;
        }

        public StoryFixture Entry(DialogEntryDef entry)
        {
            _entries.Add(entry);
            return this;
        }

        public StoryFixture Entry(string id, DialogEntryKind kind = DialogEntryKind.Choice, long order = 0, Condition condition = null,
                                  bool repeatable = false, string parent = null, ActorId? owner = null, string uniqueGroup = null,
                                  bool needsChild = false, bool closesSiblings = false, IReadOnlyList<PreCheck> preChecks = null,
                                  params DialogCommand[] commands)
        {
            _entries.Add(new DialogEntryDef(new Id<DialogEntryDef>(id), owner ?? Npc, new Id<DialogEntryDef>(parent), kind, order,
                                            new TextId("T_" + id), repeatable, needsChild, closesSiblings, uniqueGroup, condition,
                                            preChecks, commands));
            return this;
        }

        public static Say NpcSays(string text) => new Say(ActorRef.Partner, new TextId(text), "VOICE_" + text);
        public static Say PlayerSays(string text) => new Say(ActorRef.Player, new TextId(text), null);

        /// <summary>Completes every pending line; returns the texts shown.</summary>
        public List<string> FinishLines()
        {
            var shown = new List<string>();
            while (Dialog.IsActive && Dialog.Current.Phase == ConversationPhase.AwaitingLine)
            {
                shown.Add(Presenter.Lines.Last().Text.Value);
                Dialog.CompleteLine();
            }
            return shown;
        }

        public List<string> OfferedIds() => Dialog.Current.Options.Select(o => o.Entry.Name).ToList();

        public void Choose(string id) => Dialog.Choose(new Id<DialogEntryDef>(id));

        DialogService Build()
        {
            foreach (var e in _entries) _db.Put(e);
            Actors = new ActorRegistry(_db.All<NpcDef>());
            Quests = new QuestService(_db, Events, Log, Clock);
            QuestLog = new QuestLog(Events);
            var rules = TestRules.Progression();
            Hero = Hero.CreateNew(_db, rules, RuleOptions.Default);
            Progression = new ProgressionService(Hero, rules, RuleOptions.Default, Events);
            new QuestRewardGiver(Quests, Progression, Fame, Events);
            Items = ItemWiring.Create(_db, Hero, Progression, Fame, RuleOptions.Default, new SeededRandom(3), Events, Log);
            new QuestItemCounter(Quests, _db.All<QuestDef>(), Events);
            new QuestDefeatCounter(Quests, _db.All<QuestDef>(), Events, AlternativeBalancing);
            Party = new PlayerParty(Items.Roles, Events);
            var services = new StoryServices(Actors, new UnharmedVitals(), new NpcVoiceSets(_db), Reputation, Progression, Fame, Items,
                                             director: Director, random: new SeededRandom(5), party: Party, positions: Positions,
                                             world: WorldDirector, traits: Traits, pickpocket: Pickpocket);
            World = new StoryWorld(Flags, Quests, Memory, QuestLog, services, Events, Log);
            _db.TryGet(SharedLines.DefinitionId, out SharedLines shared);
            return new DialogService(World, new DialogCatalog(_db.All<DialogEntryDef>()), CommandRunner.CreateDefault(shared), Presenter);
        }

        public ConditionContext Conditions()
        {
            var _ = Dialog;
            return new ConditionContext(World, Npc);
        }
    }

    public sealed class RecordingPresenter : IDialogPresenter
    {
        public readonly List<DialogLine> Lines = new List<DialogLine>();
        public readonly List<IReadOnlyList<DialogOption>> Menus = new List<IReadOnlyList<DialogOption>>();
        public int Opened, Closed;
        /// <summary>Optional reaction inside ShowLine (simulates a presenter that finishes lines synchronously).</summary>
        public System.Action OnLine;

        public void Open(ActorId partner) => Opened++;
        public void ShowLine(DialogLine line)
        {
            Lines.Add(line);
            OnLine?.Invoke();
        }
        public void ShowOptions(IReadOnlyList<DialogOption> options) => Menus.Add(options);
        public void Close() => Closed++;
    }

    /// <summary>Records what story commands asked of the actors in the world.</summary>
    public sealed class RecordingDirector : IActorDirector
    {
        public readonly List<string> Calls = new List<string>();
        public bool CanAttack = true;

        public bool Attack(ActorId attacker, ActorId target, AttackReason reason)
        {
            Calls.Add($"attack {attacker} {target} {reason}");
            return CanAttack;
        }

        public bool SetRoutine(ActorId npc, string routine)
        {
            Calls.Add($"routine {npc} {routine}");
            return true;
        }

        public bool FullStop(ActorId npc)
        {
            Calls.Add($"fullstop {npc}");
            return true;
        }

        public void SetAlignment(ActorId npc, string alignment) => Calls.Add($"alignment {npc} {alignment ?? "none"}");
        public void HealFully(ActorId actor) => Calls.Add($"heal {actor}");
        public void PlayerSleepsUntil(int hour) => Calls.Add($"sleep {hour}");
    }

    /// <summary>Positions by name (actors and places) on a plane; unknown names have none.</summary>
    public sealed class FakePositions : IWorldPositions
    {
        public readonly Dictionary<string, (float X, float Z)> At = new Dictionary<string, (float, float)>();

        public FakePositions Put(string name, float x, float z)
        {
            At[name] = (x, z);
            return this;
        }

        public bool TryGetDistance(ActorId actor, string entity, out float metres)
        {
            metres = float.MaxValue;
            if (!At.TryGetValue(actor.Name, out var a) || !At.TryGetValue(entity, out var b)) return false;
            metres = (float)System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
            return true;
        }
    }

    public sealed class FakeReputation : IReputation
    {
        public int Fame;
        public int PoliticalFame;
        public int EnclaveFameOf(ActorId member) => Fame;
        public int PoliticalFameOf(ActorId member) => PoliticalFame;
    }

    public sealed class ListLog : IGameLog
    {
        public readonly List<string> Messages = new List<string>();
        public void Info(string message) => Messages.Add(message);
        public void Warning(string message) => Messages.Add("WARNING " + message);
    }
}
