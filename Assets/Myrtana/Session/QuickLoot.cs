using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>What a quick loot did.</summary>
    public enum QuickLootOutcome
    {
        /// <summary>At least one stack went to the hero.</summary>
        Taken,
        /// <summary>Nothing to take (the hero says "NothingToGet").</summary>
        Nothing,
        /// <summary>The chest is locked (<c>HUD_ChestLocked</c>).</summary>
        Locked,
        /// <summary>A fight against the hero is on (<c>HUD_NotDuringBattle</c>, chests only as CP).</summary>
        InBattle,
        /// <summary>The target is no body, robbable sleeper or chest.</summary>
        NotLootable,
    }

    /// <summary>One line of a quick loot's summary: an item and the units taken of it, all its stacks together.</summary>
    public readonly struct QuickLootLine : IEquatable<QuickLootLine>
    {
        public QuickLootLine(string item, int amount)
        {
            Item = item;
            Amount = amount;
        }

        public string Item { get; }
        public int Amount { get; }

        public bool Equals(QuickLootLine other) => Item == other.Item && Amount == other.Amount;
        public override bool Equals(object obj) => obj is QuickLootLine l && Equals(l);
        public override int GetHashCode() => (Item?.GetHashCode() ?? 0) ^ Amount;
        public override string ToString() => $"{Amount} x {Item}";
    }

    /// <summary>The result of <see cref="QuickLoot"/>: the outcome, the grouped items, the gold and the thefts it counted.</summary>
    public sealed class QuickLootResult
    {
        internal QuickLootResult(QuickLootOutcome outcome, IReadOnlyList<QuickLootLine> lines, int gold, int thefts)
        {
            Outcome = outcome;
            Lines = lines;
            Gold = gold;
            Thefts = thefts;
        }

        public QuickLootOutcome Outcome { get; }
        /// <summary>The items taken other than gold, grouped by item in the order first taken.</summary>
        public IReadOnlyList<QuickLootLine> Lines { get; }
        /// <summary>Gold taken (all stacks).</summary>
        public int Gold { get; }
        /// <summary>Stacks the crime rules saw as a theft (<see cref="BroadcastKind.TakeItem"/> broadcasts, as via the loot page).</summary>
        public int Thefts { get; }
        public bool Theft => Thefts > 0;

        internal static QuickLootResult Of(QuickLootOutcome outcome) => new QuickLootResult(outcome, Array.Empty<QuickLootLine>(), 0, 0);
    }

    /// <summary>A quick loot begins: <see cref="GameNotices"/> collects its item moves instead of a line per stack.</summary>
    public readonly struct QuickLootStarted : IGameEvent { }

    /// <summary>A quick loot ended: <see cref="GameNotices"/> posts the collected moves grouped ("Got: …" lines).</summary>
    public readonly struct QuickLootFinished : IGameEvent
    {
        public QuickLootFinished(QuickLootOutcome outcome) => Outcome = outcome;
        public QuickLootOutcome Outcome { get; }
    }

    /// <summary>
    /// Takes everything from a body, a robbable sleeper or a chest without the loot page: the CP quick loot (right button,
    /// ge3.ini QuickLoot) and fix-205's [owner] chord (Ctrl / right button held + left button; not G3). It moves exactly what
    /// the loot page's "Take all" moves (<see cref="NpcLoot.TakeAll"/>: no spell books, no merchandise;
    /// <see cref="InteractiveObjects.Take"/> for a chest), so every rule hangs on the same events: a chest's owner sees a theft
    /// per stack (<see cref="ContainerItemTaken"/>), a spell sleeper's stacks are thefts (<see cref="SleeperTheft"/>), looting a
    /// knocked-out or dead body is none. A chest checks the fight and its lock first as CP's action 0x90 does, and is walked
    /// from the last stack to the first. Engine-free; the hero's presentation (voice line, messages) is the caller's.
    /// </summary>
    public sealed class QuickLoot
    {
        readonly GameSession _session;

        public QuickLoot(GameSession session) => _session = session ?? throw new ArgumentNullException(nameof(session));

        /// <summary>A knocked-out or dead NPC (G3 AIMode 8/9).</summary>
        public bool IsBody(ActorId npc) =>
            !npc.IsEmpty && !npc.IsPlayer && (_session.Combat.Vitals.IsDown(npc) || _session.Combat.Vitals.IsDead(npc));

        /// <summary>Everything lootable on a downed or dead NPC.</summary>
        public QuickLootResult Body(ActorId npc)
        {
            var loot = _session.Items.NpcLoot;
            if (loot == null || !IsBody(npc)) return QuickLootResult.Of(QuickLootOutcome.NotLootable);
            return Run(() => loot.TakeAll(npc));
        }

        /// <summary>
        /// Robs a helpless NPC as Use would (<see cref="SleeperTheft.Rob"/>: plunder rolled once; a spell sleeper's stacks are
        /// thefts) and takes everything.
        /// </summary>
        public QuickLootResult Sleeper(ActorId npc, bool heroSneaking)
        {
            var theft = _session.SleeperTheft;
            if (theft == null || !theft.CanRob(npc, heroSneaking)) return QuickLootResult.Of(QuickLootOutcome.NotLootable);
            if (!theft.Rob(npc, heroSneaking)) return QuickLootResult.Of(QuickLootOutcome.Nothing);
            return Run(() => _session.Items.NpcLoot.TakeAll(npc));
        }

        /// <summary>A chest: not during a fight, not while locked; opens it (owner's perception, first fill) and takes every stack.</summary>
        public QuickLootResult Chest(string id)
        {
            var objects = _session.Interaction.Objects;
            if (id == null || !objects.TryGet(id, out var def) || def.Kind != InteractiveKind.Chest || def.Container == null)
                return QuickLootResult.Of(QuickLootOutcome.NotLootable);
            if (_session.NpcCombat.IsPlayerInCombat) return QuickLootResult.Of(QuickLootOutcome.InBattle);
            if (objects.IsLocked(id)) return QuickLootResult.Of(QuickLootOutcome.Locked);
            if (!objects.OpenContainer(id)) return QuickLootResult.Of(QuickLootOutcome.NotLootable);
            return Run(() =>
            {
                var stacks = new List<ItemStack>(_session.Items.Inventories.Of(InteractiveObjects.ContainerOf(id)).Stacks);
                // CP PS_Normal_Loop action 0x90: from the last stack to the first (fix-77)
                for (int i = stacks.Count - 1; i >= 0; i--) objects.Take(id, stacks[i].Item, stacks[i].Amount);
            });
        }

        QuickLootResult Run(Action take)
        {
            var moved = new List<(string item, int amount)>();
            int thefts = 0;
            var events = _session.Events;
            using (events.Subscribe<ItemsTransferred>(e => { if (e.To.IsPlayer && !e.From.IsPlayer) moved.Add((e.Item, e.Amount)); }))
            using (events.Subscribe<PerceptionBroadcast>(b =>
                   {
                       if (b.Kind == BroadcastKind.TakeItem && b.Source.IsPlayer && b.Reason == AttackReason.Theft) thefts++;
                   }))
            {
                events.Publish(new QuickLootStarted());
                try { take(); }
                finally
                {
                    var outcome0 = moved.Count > 0 ? QuickLootOutcome.Taken : QuickLootOutcome.Nothing;
                    events.Publish(new QuickLootFinished(outcome0));
                }
            }
            var lines = Group(moved, _session.Items.GoldItem, out int gold);
            var outcome = moved.Count > 0 ? QuickLootOutcome.Taken : QuickLootOutcome.Nothing;
            return new QuickLootResult(outcome, lines, gold, thefts);
        }

        /// <summary>Sums the moves per item in the order first seen; the gold item goes to <paramref name="gold"/>.</summary>
        public static IReadOnlyList<QuickLootLine> Group(IEnumerable<(string item, int amount)> moves, string goldItem, out int gold)
        {
            gold = 0;
            var order = new List<string>();
            var sums = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (item, amount) in moves)
            {
                if (amount <= 0 || item == null) continue;
                if (goldItem != null && item == goldItem)
                {
                    gold += amount;
                    continue;
                }
                if (sums.TryGetValue(item, out int n)) sums[item] = n + amount;
                else
                {
                    sums[item] = amount;
                    order.Add(item);
                }
            }
            var lines = new List<QuickLootLine>(order.Count);
            foreach (var item in order) lines.Add(new QuickLootLine(item, sums[item]));
            return lines;
        }
    }
}
