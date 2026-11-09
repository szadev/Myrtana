using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Story
{
    /// <summary>The hero receives an item (G3 script callback OnItemReceived): effects at the hero, sectors switched on.</summary>
    public sealed class ItemReceivedTrigger
    {
        [JsonConstructor]
        public ItemReceivedTrigger(string item, IReadOnlyList<string> sectors, IReadOnlyList<string> effects = null)
        {
            Item = item;
            Sectors = sectors ?? Array.Empty<string>();
            Effects = effects ?? Array.Empty<string>();
        }

        public string Item { get; }
        public IReadOnlyList<string> Sectors { get; }
        public IReadOnlyList<string> Effects { get; }
    }

    /// <summary>
    /// The hero uses up an item; with at least <see cref="MinStaminaMax"/> maximum stamina the flags change, with less the
    /// <see cref="Failure"/> happens (if any).
    /// </summary>
    public sealed class ItemUsedTrigger
    {
        [JsonConstructor]
        public ItemUsedTrigger(string item, int minStaminaMax, IReadOnlyList<string> setFlags, IReadOnlyList<string> clearFlags = null,
                               ItemUseFailure failure = null)
        {
            Item = item;
            MinStaminaMax = minStaminaMax;
            SetFlags = setFlags ?? Array.Empty<string>();
            ClearFlags = clearFlags ?? Array.Empty<string>();
            Failure = failure;
        }

        public string Item { get; }
        public int MinStaminaMax { get; }
        public IReadOnlyList<string> SetFlags { get; }
        public IReadOnlyList<string> ClearFlags { get; }
        /// <summary>What a hero below <see cref="MinStaminaMax"/> suffers; null = nothing.</summary>
        public ItemUseFailure Failure { get; }
    }

    /// <summary>
    /// The failed drink (CP _AI_UseInventoryItem, It_Stollengrollen below 200 maximum stamina): the hero drops what he holds
    /// and collapses ("DOWN"), and in a conversation <see cref="TalkLine"/> is said: by the hero's current target (the
    /// conversation partner: CP _AI_Say with Self = PSNpc::GetCurrentTarget) when <see cref="PartnerSays"/>, else by the hero.
    /// </summary>
    public sealed class ItemUseFailure
    {
        [JsonConstructor]
        public ItemUseFailure(bool collapse, string talkLine = null, string talkVoice = null, bool partnerSays = false)
        {
            Collapse = collapse;
            TalkLine = string.IsNullOrEmpty(talkLine) ? null : talkLine;
            TalkVoice = string.IsNullOrEmpty(talkVoice) ? null : talkVoice;
            PartnerSays = partnerSays;
        }

        public bool Collapse { get; }
        public string TalkLine { get; }
        public string TalkVoice { get; }
        /// <summary>fix-212: the line is the conversation partner's (Ugolf's "Адская штука"), not the hero's.</summary>
        public bool PartnerSays { get; }
    }

    /// <summary>
    /// Published when the hero collapses after a failed drink, before he goes down: the views drop the items in his hands
    /// (G3 DropHandItems); a conversation that made him drink shows <see cref="TalkLine"/>.
    /// </summary>
    public readonly struct HeroCollapsed : IGameEvent
    {
        public HeroCollapsed(string item, string talkLine, string talkVoice, bool partnerSays = false)
        {
            Item = item;
            TalkLine = talkLine;
            TalkVoice = talkVoice;
            PartnerSays = partnerSays;
        }

        public string Item { get; }
        public string TalkLine { get; }
        public string TalkVoice { get; }
        /// <summary>The conversation partner says <see cref="TalkLine"/> (else the hero).</summary>
        public bool PartnerSays { get; }
    }

    /// <summary>An actor enters a named area (G3 OnEnterArea of that entity and zone): flags are set.</summary>
    public sealed class AreaEnteredTrigger
    {
        [JsonConstructor]
        public AreaEnteredTrigger(string actor, string area, IReadOnlyList<string> setFlags)
        {
            Actor = actor;
            Area = area;
            SetFlags = setFlags ?? Array.Empty<string>();
        }

        public string Actor { get; }
        public string Area { get; }
        public IReadOnlyList<string> SetFlags { get; }
    }

    /// <summary>
    /// Story changes that G3 makes in its scripts rather than in infos (Docs/story-walk.md §5): CP 1.75 Script_Game callbacks
    /// for a few named items, actors and areas. Data, so the importer states which ones the game has.
    /// </summary>
    public sealed class StoryTriggerRules : IDefinition
    {
        public const string DefinitionId = "storyTriggers";

        [JsonConstructor]
        public StoryTriggerRules(string id, IReadOnlyList<ItemReceivedTrigger> itemReceived, IReadOnlyList<ItemUsedTrigger> itemUsed,
                                 IReadOnlyList<AreaEnteredTrigger> areaEntered)
        {
            Id = id;
            ItemReceived = itemReceived ?? Array.Empty<ItemReceivedTrigger>();
            ItemUsed = itemUsed ?? Array.Empty<ItemUsedTrigger>();
            AreaEntered = areaEntered ?? Array.Empty<AreaEnteredTrigger>();
        }

        public string Id { get; }
        public IReadOnlyList<ItemReceivedTrigger> ItemReceived { get; }
        public IReadOnlyList<ItemUsedTrigger> ItemUsed { get; }
        public IReadOnlyList<AreaEnteredTrigger> AreaEntered { get; }

        string IDefinition.Key => Id;

        public static readonly StoryTriggerRules None = new StoryTriggerRules(DefinitionId, null, null, null);
    }

    /// <summary>
    /// Runs <see cref="StoryTriggerRules"/>: items reaching the hero (<see cref="ItemsTransferred"/>), items the hero used up
    /// (<see cref="ItemUsed"/>), and, in <see cref="Check"/> (the session's once-a-second world check), actors inside areas.
    /// Area triggers fire once per entry: the actor must leave the area before it fires again.
    /// </summary>
    public sealed class StoryTriggers : IDisposable
    {
        readonly StoryTriggerRules _rules;
        readonly IGameEventFlags _flags;
        readonly Func<string, bool> _enableSector;
        readonly IAreaLocator _areas;
        readonly Func<int> _heroStaminaMax;
        readonly Action _collapseHero;
        readonly IEventBus _events;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly HashSet<AreaEnteredTrigger> _inside = new HashSet<AreaEnteredTrigger>();

        /// <param name="enableSector">Switches a world sector on (WorldSectors.Set(sector, true)).</param>
        /// <param name="collapseHero">Knocks the hero down after a failed drink (ActorVitals.KnockOut of the player).</param>
        public StoryTriggers(StoryTriggerRules rules, IEventBus events, IGameEventFlags flags, Func<string, bool> enableSector,
                             IAreaLocator areas, Func<int> heroStaminaMax, Action collapseHero = null)
        {
            _collapseHero = collapseHero;
            _rules = rules ?? StoryTriggerRules.None;
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _flags = flags ?? throw new ArgumentNullException(nameof(flags));
            _enableSector = enableSector;
            _areas = areas;
            _heroStaminaMax = heroStaminaMax;
            if (_rules.ItemReceived.Count > 0) _subscriptions.Add(events.Subscribe<ItemsTransferred>(OnTransferred));
            if (_rules.ItemUsed.Count > 0) _subscriptions.Add(events.Subscribe<ItemUsed>(OnUsed));
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }

        void OnTransferred(ItemsTransferred e)
        {
            if (!e.To.IsPlayer || e.Amount <= 0) return;
            foreach (var t in _rules.ItemReceived.Where(t => t.Item == e.Item))
            {
                foreach (var effect in t.Effects) _events.Publish(new StoryEffectStarted(effect, ActorId.Player.Name));
                foreach (var sector in t.Sectors) _enableSector?.Invoke(sector);
            }
        }

        void OnUsed(ItemUsed e)
        {
            foreach (var t in _rules.ItemUsed.Where(t => t.Item == e.Item))
            {
                if (t.MinStaminaMax > 0 && (_heroStaminaMax?.Invoke() ?? 0) < t.MinStaminaMax)
                {
                    if (t.Failure != null && t.Failure.Collapse)
                    {
                        _events.Publish(new HeroCollapsed(t.Item, t.Failure.TalkLine, t.Failure.TalkVoice, t.Failure.PartnerSays));
                        _collapseHero?.Invoke();
                    }
                    continue;
                }
                foreach (var f in t.SetFlags) _flags.Set(f);
                foreach (var f in t.ClearFlags) _flags.Clear(f);
            }
        }

        /// <summary>Actors that entered their trigger area since the last check set the trigger's flags.</summary>
        public void Check()
        {
            if (_areas == null) return;
            foreach (var t in _rules.AreaEntered)
            {
                if (!_areas.TryIsInside(new ActorId(t.Actor), t.Area, out bool inside)) continue;
                if (!inside) { _inside.Remove(t); continue; }
                if (!_inside.Add(t)) continue;
                foreach (var f in t.SetFlags) _flags.Set(f);
            }
        }
    }
}
