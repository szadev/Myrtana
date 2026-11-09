using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Finds the owned zone at a world position (implemented by the crime module's zone map; null = no zone).</summary>
    public interface IZoneLocator
    {
        string ZoneAt(float x, float y, float z);
    }

    /// <summary>The result of trying to open a lock.</summary>
    public readonly struct UnlockResult
    {
        public UnlockResult(LockVerdict verdict, bool opened, bool lockpickUsed, int roll = -1, int breakChance = 0)
        {
            Verdict = verdict;
            Opened = opened;
            LockpickUsed = lockpickUsed;
            Roll = roll;
            BreakChance = breakChance;
        }

        public LockVerdict Verdict { get; }
        /// <summary>The lock is open now (or was not locked).</summary>
        public bool Opened { get; }
        /// <summary>A lockpick was used up ("Lockpick used").</summary>
        public bool LockpickUsed { get; }
        /// <summary>The pick failed (CP 1.75; the hero says "PickBroke").</summary>
        public bool Broke => Verdict == LockVerdict.Pick && !Opened;
        /// <summary>The pick roll 0..99 (G3 <c>rand() % 100</c>), -1 when none was needed.</summary>
        public int Roll { get; }
        /// <summary>The chance in percent the pick had to fail (0 = it could not fail).</summary>
        public int BreakChance { get; }
    }

    /// <summary>
    /// The changing state of the interactive objects (<see cref="InteractiveObjectDef"/>): which locks were opened, which
    /// doors and switches differ from their start state, and which chests were filled from their treasure sets. A chest's
    /// contents are an ordinary inventory (<see cref="ContainerOf"/>), so they are saved with the other inventories.
    /// </summary>
    public sealed class InteractiveObjects
    {
        const string ContainerPrefix = "container:";

        readonly IContentDatabase _content;
        readonly LockRules _rules;
        readonly Inventories _inventories;
        readonly Func<string, bool> _heroKnows;
        readonly PlunderRoller _plunder;
        readonly IRandom _pickRandom;
        readonly IEventBus _events;
        readonly HashSet<string> _lockToggled = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _moverToggled = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _filled = new HashSet<string>(StringComparer.Ordinal);

        public InteractiveObjects(IContentDatabase content, LockRules rules, Inventories inventories, Func<string, bool> heroKnows,
                                  IRandom random, IEventBus events, PlunderRoller plunder = null, IRandom pickRandom = null)
        {
            _plunder = plunder ?? new PlunderRoller(content, random, heroKnows);
            _content = content;
            _rules = rules ?? LockRules.Default;
            _inventories = inventories;
            _heroKnows = heroKnows;
            // fix-117: G3 rolls a pick with the C runtime's rand(), which a save does not hold: a reloaded game rolls anew.
            // fix-119: the session random itself is no longer restored from a save; pickRandom stays a test seam.
            _pickRandom = pickRandom ?? random;
            _events = events;
        }

        public LockRules Rules => _rules;

        /// <summary>Where the crime module finds the zone of a container (none by default).</summary>
        public IZoneLocator Zones { get; set; }

        /// <summary>Raised with the object id whenever its lock, mover or contents state changes.</summary>
        public event Action<string> Changed;

        /// <summary>Raised after every pick attempt (a lockpick used up) with the object id and its result.</summary>
        public event Action<string, UnlockResult> Picked;

        public bool TryGet(string id, out InteractiveObjectDef def) => _content.TryGet(id, out def);

        /// <summary>
        /// Whether a story-switchable world sector is on (G3 SetSectorStatus; set by the session from its WorldSectors).
        /// Null = every sector is on.
        /// </summary>
        public Func<string, bool> SectorEnabled { get; set; }

        /// <summary>The object is in the world: it belongs to no switchable sector, or its sector is on.</summary>
        public bool IsInWorld(InteractiveObjectDef def) =>
            def != null && (def.Sector == null || SectorEnabled == null || SectorEnabled(def.Sector));

        public bool IsInWorld(string id) => TryGet(id, out var def) && IsInWorld(def);

        /// <summary>The inventory holding a chest's contents.</summary>
        public static ActorId ContainerOf(string id) => new ActorId(ContainerPrefix + id);

        /// <summary>The chest id of a container inventory, or null for any other inventory.</summary>
        public static string ObjectOf(ActorId inventory) =>
            inventory.Name != null && inventory.Name.StartsWith(ContainerPrefix, StringComparison.Ordinal) ? inventory.Name.Substring(ContainerPrefix.Length) : null;

        // -------------------------------------------------------------------------------------------------------------
        // Locks

        public bool IsLocked(string id) => TryGet(id, out var def) && def.Lock != null && def.Lock.Locked != _lockToggled.Contains(id);

        /// <summary>What happens when the hero tries the lock now (nothing changes).</summary>
        public LockVerdict CheckLock(string id)
        {
            if (!TryGet(id, out var def)) return LockVerdict.Open;
            return LockCheck.Check(_rules, def.Lock, IsLocked(id), new LockPicker(HeroCount, _heroKnows));
        }

        /// <summary>
        /// G3 <c>_AI_Unlock</c>: opens the lock with the key, or picks it (one lockpick used up; CP 1.75 may fail). Refusals
        /// change nothing.
        /// </summary>
        public UnlockResult Unlock(string id)
        {
            var verdict = CheckLock(id);
            switch (verdict)
            {
                case LockVerdict.Open:
                    return new UnlockResult(verdict, true, false);
                case LockVerdict.Key:
                    SetUnlocked(id, viaKey: true);
                    return new UnlockResult(verdict, true, false);
                case LockVerdict.Pick:
                    _inventories.Consume(ActorId.Player, _rules.LockpickItem, 1);
                    TryGet(id, out var def);
                    if (!LockCheck.RollPick(_rules, def.Lock.Difficulty, _heroKnows, _pickRandom, out int roll, out int chance))
                    {
                        _events.Publish(new LockpickBroke(id));
                        var broke = new UnlockResult(verdict, false, true, roll, chance);
                        Picked?.Invoke(id, broke);
                        return broke;
                    }
                    SetUnlocked(id, viaKey: false);
                    var picked = new UnlockResult(verdict, true, true, roll, chance);
                    Picked?.Invoke(id, picked);
                    return picked;
                default:
                    return new UnlockResult(verdict, false, false);
            }
        }

        /// <summary>
        /// The Open Locks spell (G3 MagicOpenLocks, Docs/combat.md §12.5): a locked lock below difficulty 5 opens, unless it is a
        /// difficulty-0 lock that needs its key; nothing else changes.
        /// </summary>
        public LockSpellOutcome OpenByMagic(string id)
        {
            TryGet(id, out var def);
            var l = def?.Lock;
            var outcome = SpellEffectRules.OpenLocks(l != null, IsLocked(id), l?.Difficulty ?? 0, !string.IsNullOrEmpty(l?.Key));
            if (outcome == LockSpellOutcome.Opens) SetUnlocked(id, viaKey: false);
            return outcome;
        }

        void SetUnlocked(string id, bool viaKey)
        {
            if (TryGet(id, out var def) && def.Lock != null)
            {
                if (def.Lock.Locked) _lockToggled.Add(id);
                else _lockToggled.Remove(id);
            }
            _events.Publish(new LockOpened(id, viaKey));
            Changed?.Invoke(id);
        }

        /// <summary>
        /// Puts a lock back to its start state (a chest that starts locked is shut again). Not a G3 action: dev scenarios try
        /// one lock many times.
        /// </summary>
        public void ResetLock(string id)
        {
            if (_lockToggled.Remove(id)) Changed?.Invoke(id);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Chests

        /// <summary>A chest's contents were rolled (it was opened at least once).</summary>
        public bool IsFilled(string id) => _filled.Contains(id);

        /// <summary>
        /// Opens an unlocked chest: the first time its treasure sets are rolled into it (G3 generates plunder on the first
        /// open: GeneratePlunderInvFromTreasureSets, <see cref="PlunderRoller"/>; a unique set gives one stack). Returns false if it is locked or not a chest.
        /// </summary>
        public bool OpenContainer(string id)
        {
            if (!TryGet(id, out var def) || def.Container == null || IsLocked(id)) return false;
            if (_filled.Add(id)) Fill(def);
            _events.Publish(new ContainerOpened(id, def.Owner, ZoneOf(def), def.Position));
            Changed?.Invoke(id);
            return true;
        }

        void Fill(InteractiveObjectDef def)
        {
            var inventory = _inventories.Of(ContainerOf(def.Id));
            foreach (var setId in def.Container.TreasureSets)
            {
                if (_content.TryGet(setId, out TreasureSetDef set)) _plunder.Roll(set, inventory);
            }
            foreach (var stack in def.Container.Stacks) inventory.Add(stack.Item, Math.Max(1, stack.Amount), stack.Quality);
        }

        /// <summary>Moves items from an opened chest to the hero; returns the amount moved. Publishes <see cref="ContainerItemTaken"/>.</summary>
        public int Take(string id, string item, int amount)
        {
            if (!TryGet(id, out var def) || def.Container == null || !_filled.Contains(id)) return 0;
            var from = ContainerOf(id);
            int moved = _inventories.Transfer(from, ActorId.Player, item, amount, quality: _inventories.Of(from).QualityOf(item));
            if (moved <= 0) return 0;
            _events.Publish(new ContainerItemTaken(id, item, moved, def.Owner, ZoneOf(def), def.Position));
            Changed?.Invoke(id);
            return moved;
        }

        /// <summary>Takes every stack (the loot page's "Take all"); returns the number of stacks taken.</summary>
        public int TakeAll(string id)
        {
            var stacks = new List<ItemStack>(_inventories.Of(ContainerOf(id)).Stacks);
            int taken = 0;
            foreach (var stack in stacks)
                if (Take(id, stack.Item, stack.Amount) > 0) taken++;
            return taken;
        }

        /// <summary>The hero puts items into an opened chest; returns the amount moved.</summary>
        public int Put(string id, string item, int amount)
        {
            if (!TryGet(id, out var def) || def.Container == null || !_filled.Contains(id)) return 0;
            int moved = _inventories.Transfer(ActorId.Player, ContainerOf(id), item, amount, quality: _inventories.Of(ActorId.Player).QualityOf(item));
            if (moved > 0) Changed?.Invoke(id);
            return moved;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Doors and switches

        /// <summary>A door or switch is in its "open" (switched) state.</summary>
        public bool IsOpen(string id)
        {
            if (!TryGet(id, out var def)) return false;
            bool startOpen = def.Door != null ? !def.Door.Closed : false;
            return startOpen != _moverToggled.Contains(id);
        }

        /// <summary>Opens a closed door or closes an open one (G3 <c>PS_Interact_Door</c>); false while it is locked.</summary>
        public bool ToggleDoor(string id)
        {
            if (!TryGet(id, out var def) || def.Door == null || IsLocked(id)) return false;
            ToggleMover(id);
            return true;
        }

        /// <summary>Throws a lever or switch: it and every linked mover change state. False while it is locked.</summary>
        public bool UseSwitch(string id)
        {
            if (!TryGet(id, out var def) || def.Kind != InteractiveKind.Switch || IsLocked(id)) return false;
            ToggleMover(id);
            foreach (var target in def.Targets)
                if (TryGet(target, out var t) && t.Door != null) ToggleMover(target);
            return true;
        }

        void ToggleMover(string id)
        {
            if (!_moverToggled.Add(id)) _moverToggled.Remove(id);
            _events.Publish(new MoverToggled(id, IsOpen(id)));
            Changed?.Invoke(id);
        }

        string ZoneOf(InteractiveObjectDef def) => Zones?.ZoneAt(def.Position[0], def.Position[1], def.Position[2]);

        int HeroCount(string item) => _inventories.Of(ActorId.Player).Count(item);

        // -------------------------------------------------------------------------------------------------------------
        // Save state

        internal IEnumerable<string> LockToggled => _lockToggled;
        internal IEnumerable<string> MoverToggled => _moverToggled;
        internal IEnumerable<string> Filled => _filled;

        internal void Restore(IEnumerable<string> lockToggled, IEnumerable<string> moverToggled, IEnumerable<string> filled)
        {
            _lockToggled.Clear();
            _moverToggled.Clear();
            _filled.Clear();
            foreach (var id in lockToggled) if (_content.TryGet(id, out InteractiveObjectDef _)) _lockToggled.Add(id);
            foreach (var id in moverToggled) if (_content.TryGet(id, out InteractiveObjectDef _)) _moverToggled.Add(id);
            foreach (var id in filled) if (_content.TryGet(id, out InteractiveObjectDef _)) _filled.Add(id);
        }
    }
}
