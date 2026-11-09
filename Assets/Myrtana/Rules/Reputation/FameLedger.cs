using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    public enum FameKind
    {
        /// <summary>Standing with a political faction (G3 PoliticalFame: Orcs, Rebels, Hashishin …).</summary>
        Political,
        /// <summary>Standing with one enclave (a village, camp or city: "Faring").</summary>
        Enclave,
    }

    /// <summary>
    /// The player's fame values, 0–100 (quests.json: rewards add, failures subtract, capped and floored). Until
    /// enclave membership of NPCs is imported (slice 8), dialog fame gates still use <see cref="IReputation"/>.
    /// </summary>
    public sealed class FameLedger
    {
        public const int Minimum = 0, Maximum = 100;

        readonly Dictionary<(FameKind, string), int> _fame = new Dictionary<(FameKind, string), int>();

        public event Action Changed;

        public int Of(FameKind kind, string group) => _fame.TryGetValue((kind, group), out var v) ? v : 0;

        public IEnumerable<KeyValuePair<string, int>> All(FameKind kind) =>
            _fame.Where(kv => kv.Key.Item1 == kind)
                 .Select(kv => new KeyValuePair<string, int>(kv.Key.Item2, kv.Value))
                 .OrderBy(kv => kv.Key, StringComparer.Ordinal);

        public void Add(FameKind kind, string group, int delta)
        {
            if (string.IsNullOrEmpty(group) || delta == 0) return;
            Set(kind, group, Of(kind, group) + delta);
        }

        internal void Set(FameKind kind, string group, int value)
        {
            _fame[(kind, group)] = Math.Max(Minimum, Math.Min(Maximum, value));
            Changed?.Invoke();
        }
    }

    /// <summary>Save section "rules.fame".</summary>
    public sealed class FameSaveSection : ISaveParticipant
    {
        readonly FameLedger _ledger;

        public FameSaveSection(FameLedger ledger) => _ledger = ledger;

        public string Section => "rules.fame";
        /// <summary>2 (fix-144): written since G3's ChangeEnclaveStatus(Routine) sets the enclave's fame to 100.</summary>
        public int Version => 2;

        /// <summary>The version of the section last read (0 = none read yet).</summary>
        public int ReadVersion { get; private set; }

        public void Write(ISaveWriter w)
        {
            foreach (FameKind kind in Enum.GetValues(typeof(FameKind)))
            {
                var entries = _ledger.All(kind).ToList();
                w.WriteInt32(entries.Count);
                foreach (var kv in entries)
                {
                    w.WriteString(kv.Key);
                    w.WriteInt32(kv.Value);
                }
            }
        }

        public void Read(ISaveReader r, int version)
        {
            ReadVersion = version;
            foreach (FameKind kind in Enum.GetValues(typeof(FameKind)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) _ledger.Set(kind, r.ReadString(), r.ReadInt32());
            }
        }
    }
}
