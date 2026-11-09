using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// G3 gCTreasureSet_PS::GeneratePlunderInventory (CP 1.75 @101b4d90): how a chest at its first open and an NPC at
    /// its first knock-out or death are filled from one treasure set:
    /// <list type="bullet">
    /// <item>Plunder: rand[min, max] picks (min = max below 1 still gives one) of a random stack, each rand[amount/2, amount]
    /// units;</item>
    /// <item>Unique: ONE stack per open, whatever min/max say: the cheapest (item gold value; the first on a tie) stack of
    /// the set that was not handed out yet anywhere in the world (G3 keeps the handed-out amount on the set's own slot,
    /// SetTransactionAmount), a random one when all were; rand[amount/2, amount] units;</item>
    /// <item>Trophy: every stack; teeth, skins and furs only when the hero knows the trophy perk;</item>
    /// <item>Mining: every stack, doubled with Perk_Mining;</item>
    /// <item>any other use (trade, pickpocket, weaponry, NotRandom …) gives nothing here.</item>
    /// </list>
    /// The handed-out unique stacks are world state (<see cref="TreasureSaveSection"/>).
    /// </summary>
    public sealed class PlunderRoller
    {
        public const string TrophyTeethPerk = "Perk_TrophyTeeth", TrophySkinPerk = "Perk_TrophySkin", TrophyFurPerk = "Perk_TrophyFur";
        public const string MiningPerk = "Perk_Mining";

        readonly IContentDatabase _content;
        readonly IRandom _random;
        readonly Func<string, bool> _heroKnows;
        readonly Dictionary<string, HashSet<int>> _handedOut = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        public PlunderRoller(IContentDatabase content, IRandom random, Func<string, bool> heroKnows)
        {
            _content = content;
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _heroKnows = heroKnows ?? (_ => false);
        }

        /// <summary>Adds the plunder of <paramref name="set"/> to <paramref name="into"/>.</summary>
        public void Roll(TreasureSetDef set, Inventory into)
        {
            if (set == null || set.Stacks.Count == 0) return;
            switch (set.Use)
            {
                case TreasureUse.Plunder:
                {
                    int picks = set.MinStacks == set.MaxStacks
                        ? Math.Max(1, set.MaxStacks)
                        : Between(Math.Min(set.MinStacks, set.MaxStacks), Math.Max(set.MinStacks, set.MaxStacks));
                    for (int i = 0; i < picks; i++) AddPart(set.Stacks[_random.Next(set.Stacks.Count)], into);
                    break;
                }
                case TreasureUse.Unique:
                {
                    int index = NextUnique(set);
                    AddPart(set.Stacks[index], into);
                    HandedOut(set.Id).Add(index);
                    break;
                }
                case TreasureUse.Trophy:
                    foreach (var s in set.Stacks)
                        if (TrophyAllowed(s.Item)) into.Add(s.Item, Math.Max(1, s.Amount), s.Quality);
                    break;
                case TreasureUse.Mining:
                    foreach (var s in set.Stacks) into.Add(s.Item, Math.Max(1, s.Amount) * (_heroKnows(MiningPerk) ? 2 : 1), s.Quality);
                    break;
            }
        }

        /// <summary>The stack a unique set gives next: the cheapest not handed out, else a random one.</summary>
        int NextUnique(TreasureSetDef set)
        {
            _handedOut.TryGetValue(set.Id, out var given);
            int best = -1, bestValue = 0;
            for (int i = 0; i < set.Stacks.Count; i++)
            {
                if (given != null && given.Contains(i)) continue;
                if (!_content.TryGet(set.Stacks[i].Item, out ItemDef item)) continue; // G3 skips a slot without an item template
                if (best < 0 || item.GoldValue < bestValue)
                {
                    best = i;
                    bestValue = item.GoldValue;
                }
            }
            return best >= 0 ? best : _random.Next(set.Stacks.Count);
        }

        HashSet<int> HandedOut(string set)
        {
            if (!_handedOut.TryGetValue(set, out var given)) _handedOut[set] = given = new HashSet<int>();
            return given;
        }

        /// <summary>Stack indices of each unique set that were handed out.</summary>
        public IReadOnlyDictionary<string, HashSet<int>> UniqueHandedOut => _handedOut;

        /// <summary>Loads the handed-out unique stacks (save).</summary>
        public void Restore(IEnumerable<KeyValuePair<string, IReadOnlyList<int>>> handedOut)
        {
            _handedOut.Clear();
            foreach (var kv in handedOut ?? Array.Empty<KeyValuePair<string, IReadOnlyList<int>>>())
                foreach (int i in kv.Value) HandedOut(kv.Key).Add(i);
        }

        void AddPart(TreasureStack s, Inventory into) => into.Add(s.Item, Math.Max(1, Between(s.Amount / 2, s.Amount)), s.Quality);

        /// <summary>G3 gates trophies by use type against the session player's perks (not the killer's).</summary>
        bool TrophyAllowed(string item)
        {
            string perk = TrophyPerkOf(item);
            return perk == null || _heroKnows(perk);
        }

        /// <summary>The three trophy perks (G3 UseType 0x1e teeth, 0x29 skin, 0x32 fur).</summary>
        public static readonly IReadOnlyList<string> TrophyPerks = new[] { TrophyTeethPerk, TrophySkinPerk, TrophyFurPerk };

        /// <summary>The trophy perks the hero knows now.</summary>
        public IEnumerable<string> KnownTrophyPerks()
        {
            foreach (var perk in TrophyPerks)
                if (_heroKnows(perk)) yield return perk;
        }

        /// <summary>The perk an item needs as a trophy (by its kind: TrophyTeeth / TrophySkin / TrophyFur), else null.</summary>
        public string TrophyPerkOf(string item)
        {
            switch (_content.TryGet(item, out ItemDef def) ? def.Kind : null)
            {
                case "TrophyTeeth": return TrophyTeethPerk;
                case "TrophySkin": return TrophySkinPerk;
                case "TrophyFur": return TrophyFurPerk;
                default: return null;
            }
        }

        /// <summary>
        /// [owner] port option "Late trophies" (fix-143, <c>NpcLoot.LateTrophies</c>): the stacks of a Trophy set that need one
        /// of <paramref name="perks"/>, in full (what <see cref="Roll"/> would have added had the hero known them); nothing
        /// else of the set. Returns the stacks added.
        /// </summary>
        public int RollLateTrophies(TreasureSetDef set, Inventory into, ICollection<string> perks)
        {
            if (set == null || set.Use != TreasureUse.Trophy || perks == null || perks.Count == 0) return 0;
            int added = 0;
            foreach (var s in set.Stacks)
            {
                string perk = TrophyPerkOf(s.Item);
                if (perk == null || !perks.Contains(perk)) continue;
                into.Add(s.Item, Math.Max(1, s.Amount), s.Quality);
                added++;
            }
            return added;
        }

        int Between(int min, int max) => max <= min ? min : min + _random.Next(max - min + 1);
    }

    /// <summary>Save section "rules.treasure": the unique treasure stacks already handed out (<see cref="PlunderRoller"/>).</summary>
    public sealed class TreasureSaveSection : ISaveParticipant
    {
        readonly PlunderRoller _roller;

        public TreasureSaveSection(PlunderRoller roller) => _roller = roller;

        public string Section => "rules.treasure";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var sets = new List<string>(_roller.UniqueHandedOut.Keys);
            sets.Sort(StringComparer.Ordinal);
            w.WriteInt32(sets.Count);
            foreach (var set in sets)
            {
                var given = new List<int>(_roller.UniqueHandedOut[set]);
                given.Sort();
                w.WriteString(set);
                w.WriteInt32(given.Count);
                foreach (int i in given) w.WriteInt32(i);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int n = r.ReadInt32();
            var list = new List<KeyValuePair<string, IReadOnlyList<int>>>(n);
            for (int i = 0; i < n; i++)
            {
                string set = r.ReadString();
                int count = r.ReadInt32();
                var given = new int[count];
                for (int k = 0; k < count; k++) given[k] = r.ReadInt32();
                list.Add(new KeyValuePair<string, IReadOnlyList<int>>(set, given));
            }
            _roller.Restore(list);
        }
    }
}
