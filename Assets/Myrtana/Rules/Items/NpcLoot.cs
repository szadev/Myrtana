using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>One line of a body's loot: units of one item and quality, from what it carries or from its weaponry.</summary>
    public readonly struct LootStack
    {
        public LootStack(string item, int amount, int quality, bool gear)
        {
            Item = item;
            Amount = amount;
            Quality = quality;
            Gear = gear;
        }

        public string Item { get; }
        public int Amount { get; }
        public int Quality { get; }
        /// <summary>A weapon, shield or ammunition of the NPC's weaponry (<see cref="NpcInventoryDef.Gear"/>).</summary>
        public bool Gear { get; }
    }

    /// <summary>
    /// What NPCs carry and what the hero can take from a downed or dead one (G3 CP 1.75, Docs/formats-items.md
    /// §3.6):
    /// <list type="bullet">
    /// <item>the content's static stacks are in the NPC's inventory from the start (<see cref="SeedStartStacks"/>);</item>
    /// <item>the plunder sets are rolled once, at the first knock-out or death (<see cref="GeneratePlunder"/>, G3
    /// CleanUpPlunderInv; the GeneratedPlunder flag); a plunder roll that is left on a body that gets up stays carried
    /// (an NPC may drink a potion it got that way in a later fight);</item>
    /// <item>the loot shows every carried stack except merchandise (a trader's stock: stack type 2) and spell books, plus
    /// the weaponry that is not lying in the world (the weapon in the hand drops) and was not taken. No crime, no
    /// restriction by party, species or friendship; quest items are listed.</item>
    /// <item>[owner] port option <see cref="LateTrophies"/> (fix-143): G3 decides the trophies once, at that roll, by the
    /// perks the hero knows then, so a corpse never gives a fur learnt about later; with the option a dead body's trophy
    /// sets add the stacks of each trophy perk learnt since the roll when its loot is looked at — once per perk and body
    /// (<see cref="TrophiesCovered"/>, saved), so nothing is ever given twice.</item>
    /// </list>
    /// The weaponry is not kept as inventory stacks here (the combat loadout holds it); a weapon the hero takes is lost
    /// to the NPC through <see cref="Connect"/> (the combat module's taken weapons).
    /// </summary>
    public sealed class NpcLoot : IActorStore
    {
        public const string TrophyTeethPerk = PlunderRoller.TrophyTeethPerk, TrophySkinPerk = PlunderRoller.TrophySkinPerk,
            TrophyFurPerk = PlunderRoller.TrophyFurPerk;
        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            if (_generated.Remove(actor) | _trophiesCovered.Remove(actor)) _byName = null;
        }

        public bool Holds(ActorId actor) => _generated.Contains(actor);

        public const string MiningPerk = PlunderRoller.MiningPerk;
        /// <summary>G3 GeneratePlunderInventory: rabbits get a carrot 7 % of the time.</summary>
        public const string RabbitSpecies = "Rabbit", CarrotItem = "It_Carrot";
        public const int CarrotPercent = 7;
        /// <summary>CP CleanUpPlunderInv: a demon's equippable stacks are deleted (its weaponry is not looted).</summary>
        public const string DemonSpecies = "Demon";

        readonly IContentDatabase _content;
        readonly Inventories _inventories;
        readonly WorldItems _world;
        readonly TradeService _trade;
        readonly IRandom _random;
        readonly IEventBus _events;
        readonly HashSet<ActorId> _generated = new HashSet<ActorId>();
        // fix-143: per rolled body, the trophy perks its trophies were decided for (known at the roll or granted later).
        readonly Dictionary<ActorId, HashSet<string>> _trophiesCovered = new Dictionary<ActorId, HashSet<string>>();
        // A copy of a content NPC (a dev spawn) -> the content NPC whose inventory it carries (CarryAs).
        readonly Dictionary<ActorId, string> _carriesAs = new Dictionary<ActorId, string>();
        // Weapons taken while no combat module is connected (tests, tools).
        readonly HashSet<(ActorId, string)> _lostFallback = new HashSet<(ActorId, string)>();
        Func<ActorId, string, bool> _gearLost;
        Action<ActorId, string> _takeGear;

        public NpcLoot(IContentDatabase content, Inventories inventories, WorldItems world, TradeService trade, IRandom random,
                       Func<string, bool> heroKnows, IEventBus events, PlunderRoller plunder = null)
        {
            Plunder = plunder ?? new PlunderRoller(content, random, heroKnows);
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _inventories = inventories ?? throw new ArgumentNullException(nameof(inventories));
            _world = world;
            _trade = trade;
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _events = events;
            _gearLost = (npc, item) => _lostFallback.Contains((npc, item));
            _takeGear = (npc, item) => _lostFallback.Add((npc, item));
            if (_world != null) _world.DroppedTaken += OnDroppedTaken;
        }

        /// <summary>G3 GeneratePlunderInventory, shared with the chests (one unique-treasure ledger for the world).</summary>
        public PlunderRoller Plunder { get; }

        /// <summary>The NPC's species ("Rabbit", "Demon" …) for the species rules; null = unknown.</summary>
        public Func<ActorId, string> Species { get; set; } = _ => null;

        /// <summary>The NPC is dead (not merely downed) — late trophies are for corpses only.</summary>
        public Func<ActorId, bool> IsDead { get; set; } = _ => false;

        /// <summary>
        /// [owner] port option "Late trophies" (fix-143, <c>PortOptions.LateTrophies</c>; false = G3 CP 1.75, which rolls the
        /// trophies once at death by the perks known then): a corpse rolled before the hero learnt a trophy perk gives that
        /// perk's trophies when its loot is looked at (<see cref="GrantLateTrophies"/>).
        /// </summary>
        public bool LateTrophies { get; set; }

        /// <summary>
        /// Connects the combat module's weaponry: <paramref name="gearLost"/> tells whether the NPC lost a weapon of its
        /// loadout, <paramref name="takeGear"/> takes one away (it fights without it).
        /// </summary>
        public void Connect(Func<ActorId, string, bool> gearLost, Action<ActorId, string> takeGear)
        {
            _gearLost = gearLost ?? _gearLost;
            _takeGear = takeGear ?? _takeGear;
        }

        // ------------------------------------------------------------------------------------------- generation

        /// <summary>The static stacks of every NPC (the content's start state; a loaded game keeps its saved inventories).</summary>
        public void SeedStartStacks()
        {
            foreach (var def in _content.All<NpcInventoryDef>())
            {
                var inventory = _inventories.Of(def.Npc);
                foreach (var s in def.Stacks) inventory.Add(s.Item, Math.Max(1, s.Amount), s.Quality);
            }
        }

        /// <summary>
        /// A copy of a content NPC (a dev spawn: "Orc_Warrior_01~dev01" from "Orc_Warrior_01#1ffb8290") carries what the
        /// template carries: its static stacks now, its plunder sets and weaponry at the loot (fix-120: a spawned orc had
        /// nothing to loot, so Use on him asleep said NothingToGet).
        /// </summary>
        public void CarryAs(ActorId npc, ActorId template)
        {
            if (npc.IsEmpty || npc.IsPlayer || template.IsEmpty || npc == template) return;
            _carriesAs[npc] = template.Name;
            if (!TryInventory(npc, out var def)) return;
            var inventory = _inventories.Of(npc);
            foreach (var s in def.Stacks) inventory.Add(s.Item, Math.Max(1, s.Amount), s.Quality);
        }

        bool TryInventory(ActorId npc, out NpcInventoryDef def) =>
            _content.TryGet(_carriesAs.TryGetValue(npc, out var template) ? template : npc.Name, out def);

        /// <summary>The NPC's plunder was rolled (G3 GeneratedPlunder).</summary>
        public bool IsGenerated(ActorId npc) => _generated.Contains(npc);

        public IEnumerable<ActorId> Generated => _generated;

        /// <summary>
        /// G3 CleanUpPlunderInv at a knock-out or death: once per NPC, every plunder set adds to its inventory —
        /// Plunder: rand[min, max] picks (a set with min = max below 1 still gives one) of a random stack, each
        /// rand[amount/2, amount] units (at least 1); Unique: the cheapest stack not handed out yet (<see cref="PlunderRoller"/>); Trophy: every stack in full, teeth, skins
        /// and furs only when the hero knows the trophy perk (at this moment); Mining: every stack, doubled with
        /// Perk_Mining. Rabbits get a carrot 7 % of the time. False when it was rolled before.
        /// </summary>
        public bool GeneratePlunder(ActorId npc)
        {
            if (npc.IsEmpty || npc.IsPlayer || !_generated.Add(npc)) return false;
            _byName = null;
            _trophiesCovered[npc] = new HashSet<string>(Plunder.KnownTrophyPerks(), StringComparer.Ordinal);
            var into = _inventories.Of(npc);
            if (TryInventory(npc, out var def))
                foreach (var id in def.PlunderSets)
                    if (_content.TryGet(id, out TreasureSetDef set)) Plunder.Roll(set, into);
            if (string.Equals(Species(npc), RabbitSpecies, StringComparison.Ordinal) && _random.Next(100) < CarrotPercent &&
                _content.TryGet(CarrotItem, out ItemDef _))
                into.Add(CarrotItem, 1);
            return true;
        }

        /// <summary>
        /// fix-143 [owner] (<see cref="LateTrophies"/>): a dead, rolled body gets the trophy stacks of its Trophy sets for
        /// every trophy perk the hero knows now and did not know at the roll (nor got granted here before); each such perk
        /// counts as handled for this body afterwards, whatever it added. Off, alive, downed or not rolled: nothing. Returns
        /// the stacks added.
        /// </summary>
        public int GrantLateTrophies(ActorId npc)
        {
            if (!LateTrophies || npc.IsEmpty || npc.IsPlayer || !_generated.Contains(npc) || !IsDead(npc)) return 0;
            if (!_trophiesCovered.TryGetValue(npc, out var covered)) return 0; // unknown history: never risk a duplicate
            List<string> missing = null;
            foreach (var perk in Plunder.KnownTrophyPerks())
                if (!covered.Contains(perk)) (missing ??= new List<string>()).Add(perk);
            if (missing == null) return 0;
            foreach (var perk in missing) covered.Add(perk);
            int added = 0;
            if (TryInventory(npc, out var def))
            {
                var into = _inventories.Of(npc);
                foreach (var id in def.PlunderSets)
                    if (_content.TryGet(id, out TreasureSetDef set)) added += Plunder.RollLateTrophies(set, into, missing);
            }
            return added;
        }

        /// <summary>Per rolled body, the trophy perks already decided for (saved, <see cref="ItemsSaveSection"/>).</summary>
        public IReadOnlyDictionary<ActorId, HashSet<string>> TrophiesCovered => _trophiesCovered;

        List<ActorId> _byName;

        /// <summary>
        /// fix-171: the rolled bodies (<see cref="Generated"/>, the keys of <see cref="TrophiesCovered"/> are the same set)
        /// ordered by name (ordinal), kept until a body is rolled, forgotten or loaded: the save's order, without sorting
        /// thousands of late-game bodies on every recovery snapshot.
        /// </summary>
        public IReadOnlyList<ActorId> GeneratedByName
        {
            get
            {
                if (_byName == null)
                {
                    _byName = new List<ActorId>(_generated);
                    _byName.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                }
                return _byName;
            }
        }

        // ------------------------------------------------------------------------------------------- the loot

        /// <summary>
        /// What the hero can take from <paramref name="npc"/> (G3 loot pool filter): carried stacks without merchandise and
        /// spell books (and items the content does not know), then the weaponry that was neither taken nor dropped.
        /// </summary>
        public IReadOnlyList<LootStack> Pool(ActorId npc)
        {
            GrantLateTrophies(npc); // fix-143: also makes a looted-out corpse focusable again (BodyFocusable)
            var list = new List<LootStack>();
            var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in _inventories.Of(npc).Stacks)
            {
                if (!Lootable(s.Item)) continue;
                if (!hidden.TryGetValue(s.Item, out int merch)) hidden[s.Item] = merch = _trade?.Merchandise(npc, s.Item) ?? 0;
                int shown = s.Amount - Math.Min(merch, s.Amount);
                hidden[s.Item] = merch - (s.Amount - shown);
                if (shown > 0) list.Add(new LootStack(s.Item, shown, s.Quality, gear: false));
            }
            foreach (var g in AvailableGear(npc)) list.Add(g);
            return list;
        }

        /// <summary>G3 PS_Normal: the body has anything to loot (else the hero says "NothingToGet").</summary>
        public bool HasLoot(ActorId npc) => Pool(npc).Count > 0;

        /// <summary>
        /// G3 gCFocus_PS::FindFocusEntity (Game.dll): a dead NPC stays focusable only while its inventory holds a
        /// stack the loot would show (not category 0/9 = spell books, not stack type 2 = merchandise), i.e. while it has
        /// loot; an emptied corpse is skipped by the focus (fix-68). Living and downed NPCs are not affected.
        /// </summary>
        public bool BodyFocusable(ActorId npc, bool dead) => !dead || HasLoot(npc);

        /// <summary>Units of an item the hero can take (all qualities, carried and gear).</summary>
        public int Available(ActorId npc, string item)
        {
            int n = 0;
            foreach (var s in Pool(npc))
                if (s.Item == item) n += s.Amount;
            return n;
        }

        /// <summary>Moves up to <paramref name="amount"/> units of an item to the hero (carried units first); returns the amount moved.</summary>
        public int Take(ActorId npc, string item, int amount)
        {
            int moved = 0;
            foreach (var s in Pool(npc))
            {
                if (moved >= amount) break;
                if (s.Item != item) continue;
                // A weaponry stack goes as a whole (it is one stack in G3).
                moved += s.Gear ? TakeGear(npc, s) : TakeCarried(npc, s, amount - moved);
            }
            return moved;
        }

        int TakeCarried(ActorId npc, LootStack s, int amount)
        {
            int moved = _inventories.Of(npc).RemoveExact(s.Item, s.Quality, Math.Min(amount, s.Amount));
            if (moved > 0) GiveHero(npc, s.Item, moved, s.Quality);
            return moved;
        }

        /// <summary>The loot page's "Take all" and the CP quick loot: every lootable stack; returns what was taken.</summary>
        public IReadOnlyList<LootStack> TakeAll(ActorId npc)
        {
            var taken = new List<LootStack>();
            foreach (var s in Pool(npc))
            {
                int moved = s.Gear ? TakeGear(npc, s) : TakeCarried(npc, s, s.Amount);
                if (moved <= 0) continue;
                taken.Add(new LootStack(s.Item, moved, s.Quality, s.Gear));
            }
            return taken;
        }

        int TakeGear(ActorId npc, LootStack s)
        {
            _takeGear(npc, s.Item);
            GiveHero(npc, s.Item, s.Amount, s.Quality);
            return s.Amount;
        }

        /// <summary>The hero puts items into the body's inventory (the loot page works both ways); returns the amount moved.</summary>
        public int Put(ActorId npc, string item, int amount)
        {
            if (npc.IsEmpty || npc.IsPlayer) return 0;
            return _inventories.Transfer(ActorId.Player, npc, item, amount, quality: _inventories.Of(ActorId.Player).QualityOf(item));
        }

        IEnumerable<LootStack> AvailableGear(ActorId npc)
        {
            if (!TryInventory(npc, out var def) || def.Gear.Count == 0) yield break;
            if (string.Equals(Species(npc), DemonSpecies, StringComparison.Ordinal)) yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in def.Gear)
            {
                if (!seen.Add(g.Item) || !Lootable(g.Item) || _gearLost(npc, g.Item) || Lying(npc, g.Item)) continue;
                yield return new LootStack(g.Item, Math.Max(1, g.Amount), g.Quality, gear: true);
            }
        }

        bool Lying(ActorId npc, string item)
        {
            if (_world == null) return false;
            foreach (var d in _world.DroppedBy(npc))
                if (d.Item == item) return true;
            return false;
        }

        bool Lootable(string item) =>
            _content.TryGet(item, out ItemDef def) && def.Category != ItemCategory.Spellbook;

        void GiveHero(ActorId npc, string item, int amount, int quality)
        {
            _inventories.Of(ActorId.Player).Add(item, amount, quality);
            _events?.Publish(new ItemsTransferred(npc, ActorId.Player, item, amount));
        }

        /// <summary>A dropped weapon of an NPC's weaponry was taken by someone else: the NPC no longer has it.</summary>
        void OnDroppedTaken(DroppedItem d, ActorId by)
        {
            if (d.Owner.IsEmpty || d.Owner.IsPlayer || by == d.Owner) return;
            if (TryInventory(d.Owner, out var def))
                foreach (var g in def.Gear)
                    if (g.Item == d.Item)
                    {
                        _takeGear(d.Owner, d.Item);
                        return;
                    }
        }

        // ------------------------------------------------------------------------------------------- save

        /// <summary>
        /// Loads the rolled bodies; <paramref name="covered"/> = their decided trophy perks (fix-143). Null (a save older than
        /// fix-143): every body counts as decided for the perks the hero knows at the load (he may have known them at the roll,
        /// so they are never granted late) — a perk he does not know yet was certainly not known then.
        /// </summary>
        internal void Restore(IEnumerable<ActorId> generated, IEnumerable<KeyValuePair<ActorId, IEnumerable<string>>> covered = null)
        {
            _generated.Clear();
            _trophiesCovered.Clear();
            _byName = null;
            foreach (var a in generated) _generated.Add(a);
            if (covered == null)
            {
                foreach (var a in _generated) _trophiesCovered[a] = new HashSet<string>(Plunder.KnownTrophyPerks(), StringComparer.Ordinal);
                return;
            }
            foreach (var kv in covered)
                if (_generated.Contains(kv.Key)) _trophiesCovered[kv.Key] = new HashSet<string>(kv.Value, StringComparer.Ordinal);
            foreach (var a in _generated)
                if (!_trophiesCovered.ContainsKey(a)) _trophiesCovered[a] = new HashSet<string>(PlunderRoller.TrophyPerks, StringComparer.Ordinal);
        }
    }
}
