using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// A G3 world sector the story can switch (an entity layer of <c>G3_World_01.wrldatasc</c>, e.g.
    /// "G3_Myrtana_01_Gotha_NPC_02"): its name and whether it is on at the start of a new game. NPCs that live in it name
    /// it in <see cref="NpcDef.Sector"/>.
    /// </summary>
    public sealed class WorldSectorDef : IDefinition
    {
        [System.Text.Json.Serialization.JsonConstructor]
        public WorldSectorDef(string id, bool enabledAtStart)
        {
            Id = id;
            EnabledAtStart = enabledAtStart;
        }

        public string Id { get; }
        public bool EnabledAtStart { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>A world sector was switched on or off (G3 World::SetSectorStatus).</summary>
    public readonly struct SectorStatusChanged : IGameEvent
    {
        public SectorStatusChanged(string sector, bool enabled)
        {
            Sector = sector;
            Enabled = enabled;
        }

        public string Sector { get; }
        public bool Enabled { get; }
    }

    /// <summary>
    /// Which switchable world sectors are on (G3 SetSectorStatus; Docs/story-coverage.md). An NPC whose
    /// <see cref="NpcDef.Sector"/> is off is not in the world: the presentation does not show it and the story treats it
    /// as absent. Unknown sectors (never listed in the content) are on. Saved by <see cref="WorldSectorsSaveSection"/>.
    /// </summary>
    public sealed class WorldSectors
    {
        readonly Dictionary<string, bool> _atStart = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool> _changed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly IEventBus _events;

        public WorldSectors(IEnumerable<WorldSectorDef> sectors, IEventBus events = null)
        {
            _events = events;
            foreach (var s in sectors ?? Array.Empty<WorldSectorDef>())
                if (!string.IsNullOrEmpty(s.Id)) _atStart[s.Id] = s.EnabledAtStart;
        }

        /// <summary>Raised after a sector changed (its name).</summary>
        public event Action<string> Changed;

        /// <summary>Sectors listed in the content.</summary>
        public int Count => _atStart.Count;

        public bool IsKnown(string sector) => sector != null && _atStart.ContainsKey(sector);

        /// <summary>Null or empty = no sector (always on).</summary>
        public bool IsEnabled(string sector)
        {
            if (string.IsNullOrEmpty(sector)) return true;
            if (_changed.TryGetValue(sector, out bool on)) return on;
            return !_atStart.TryGetValue(sector, out on) || on;
        }

        /// <summary>Switches the sector; false when nothing changed.</summary>
        public bool Set(string sector, bool enabled)
        {
            if (string.IsNullOrEmpty(sector) || IsEnabled(sector) == enabled) return false;
            bool atStart = !_atStart.TryGetValue(sector, out bool s) || s;
            if (atStart == enabled) _changed.Remove(sector);
            else _changed[sector] = enabled;
            Changed?.Invoke(sector);
            _events?.Publish(new SectorStatusChanged(sector, enabled));
            return true;
        }

        /// <summary>Sectors whose state differs from the new-game state, by name.</summary>
        public IReadOnlyList<KeyValuePair<string, bool>> Changes => _changed.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        internal void Restore(IEnumerable<KeyValuePair<string, bool>> changes)
        {
            var before = _changed.Keys.ToList();
            _changed.Clear();
            foreach (var kv in changes)
            {
                bool atStart = !_atStart.TryGetValue(kv.Key, out bool s) || s;
                if (atStart != kv.Value) _changed[kv.Key] = kv.Value;
            }
            foreach (var name in before.Concat(_changed.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) Changed?.Invoke(name);
        }
    }

    /// <summary>Save section "world.sectors": the sectors switched away from their new-game state.</summary>
    public sealed class WorldSectorsSaveSection : ISaveParticipant
    {
        readonly WorldSectors _sectors;

        public WorldSectorsSaveSection(WorldSectors sectors) => _sectors = sectors ?? throw new ArgumentNullException(nameof(sectors));

        public string Section => "world.sectors";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var changes = _sectors.Changes;
            w.WriteInt32(changes.Count);
            foreach (var kv in changes)
            {
                w.WriteString(kv.Key);
                w.WriteBool(kv.Value);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            int count = r.ReadInt32();
            var list = new List<KeyValuePair<string, bool>>(count);
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                list.Add(new KeyValuePair<string, bool>(name, r.ReadBool()));
            }
            _sectors.Restore(list);
        }
    }
}
