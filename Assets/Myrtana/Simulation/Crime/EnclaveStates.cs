using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>G3 gEEnclaveStatus: what a town or camp is doing as a whole.</summary>
    public enum EnclaveStatus
    {
        /// <summary>Daily life.</summary>
        Routine,
        /// <summary>The enclave fights (G3 NotifyEnclave → ChangeEnclaveStatus Attack).</summary>
        Attack,
        /// <summary>The enclave gave up and flees (liberation).</summary>
        Flee,
    }

    /// <summary>The run-time state of one enclave (G3 gCEnclave_PS Status, Raid, Revolution).</summary>
    public readonly struct EnclaveState : IEquatable<EnclaveState>
    {
        public EnclaveState(EnclaveStatus status, bool raid, bool revolution)
        {
            Status = status;
            Raid = raid;
            Revolution = revolution;
        }

        public EnclaveStatus Status { get; }
        /// <summary>The enclave is raiding (G3 Raid): its members turn on the player's side when the enclave is alarmed.</summary>
        public bool Raid { get; }
        /// <summary>The oppressed side rises when the enclave fights (G3 Revolution).</summary>
        public bool Revolution { get; }

        /// <summary>Routine without raid or revolution: the revolution rules have no opinion.</summary>
        public bool IsQuiet => Status == EnclaveStatus.Routine && !Raid && !Revolution;

        public EnclaveState WithStatus(EnclaveStatus status) => new EnclaveState(status, Raid, Revolution);

        public bool Equals(EnclaveState other) => Status == other.Status && Raid == other.Raid && Revolution == other.Revolution;
        public override bool Equals(object obj) => obj is EnclaveState s && Equals(s);
        public override int GetHashCode() => ((int)Status * 4) + (Raid ? 2 : 0) + (Revolution ? 1 : 0);
        public override string ToString() => $"{Status}{(Raid ? " raid" : "")}{(Revolution ? " revolution" : "")}";
    }

    /// <summary>Read access to the enclaves' run-time states (attitudes ask it).</summary>
    public interface IEnclaveStates
    {
        /// <summary>The enclave's state; quiet routine for an unknown or null enclave.</summary>
        EnclaveState Of(string enclave);
    }

    /// <summary>
    /// The enclaves' run-time states. Most enclaves stay quiet routine all game; the new-game script raises Ardea's raid
    /// and revolution and sends it into Attack (Docs/game-start.md). Saved by <see cref="EnclaveStatesSaveSection"/>.
    /// </summary>
    public sealed class EnclaveStates : IEnclaveStates
    {
        readonly Dictionary<string, EnclaveState> _states = new Dictionary<string, EnclaveState>(StringComparer.Ordinal);

        /// <summary>Raised after any change (enclave name).</summary>
        public event Action<string> Changed;

        public EnclaveState Of(string enclave) =>
            enclave != null && _states.TryGetValue(enclave, out var s) ? s : default;

        public void Set(string enclave, EnclaveState state)
        {
            if (string.IsNullOrEmpty(enclave)) return;
            if (Of(enclave).Equals(state)) return;
            if (state.IsQuiet) _states.Remove(enclave);
            else _states[enclave] = state;
            Changed?.Invoke(enclave);
        }

        public void SetStatus(string enclave, EnclaveStatus status) => Set(enclave, Of(enclave).WithStatus(status));

        /// <summary>Every enclave that is not quiet routine, by name.</summary>
        public IReadOnlyList<KeyValuePair<string, EnclaveState>> NotQuiet =>
            _states.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        internal void Clear()
        {
            _states.Clear();
            _alignments.Clear();
            _switched.Clear();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Story switches of an enclave's side (G3 PSEnclave PoliticalAlignment / Switched; SetSectorStatus *_NPC_02,
        // the liberation world switch).

        readonly Dictionary<string, string> _alignments = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _switched = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>An alignment override meaning "no political alignment" (G3 gEPoliticalAlignment None: liberated Faring, Ishtar).</summary>
        public const string NoAlignment = "None";

        /// <summary>The alignment the story gave the enclave ("Reb", "Nom", <see cref="NoAlignment"/>); null = the content's.</summary>
        public string AlignmentOverride(string enclave) =>
            enclave != null && _alignments.TryGetValue(enclave, out var a) ? a : null;

        public void SetAlignment(string enclave, string alignment)
        {
            if (string.IsNullOrEmpty(enclave) || AlignmentOverride(enclave) == alignment) return;
            if (string.IsNullOrEmpty(alignment)) _alignments.Remove(enclave);
            else _alignments[enclave] = alignment;
            Changed?.Invoke(enclave);
        }

        /// <summary>The enclave changed sides for good (G3 Switched: the liberated town's people were exchanged).</summary>
        public bool IsSwitched(string enclave) => enclave != null && _switched.Contains(enclave);

        public void MarkSwitched(string enclave)
        {
            if (string.IsNullOrEmpty(enclave) || !_switched.Add(enclave)) return;
            Changed?.Invoke(enclave);
        }

        internal IReadOnlyList<KeyValuePair<string, string>> Alignments => _alignments.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        internal IReadOnlyList<string> Switched => _switched.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>Save section "world.enclaves": the enclaves that are not quiet routine.</summary>
    public sealed class EnclaveStatesSaveSection : ISaveParticipant
    {
        readonly EnclaveStates _states;

        public EnclaveStatesSaveSection(EnclaveStates states) => _states = states ?? throw new ArgumentNullException(nameof(states));

        public string Section => "world.enclaves";
        /// <summary>2: + story alignments and switched enclaves.</summary>
        public int Version => 2;

        public void Write(ISaveWriter w)
        {
            var all = _states.NotQuiet;
            w.WriteInt32(all.Count);
            foreach (var kv in all)
            {
                w.WriteString(kv.Key);
                w.WriteInt32((int)kv.Value.Status);
                w.WriteBool(kv.Value.Raid);
                w.WriteBool(kv.Value.Revolution);
            }
            var alignments = _states.Alignments;
            w.WriteInt32(alignments.Count);
            foreach (var kv in alignments)
            {
                w.WriteString(kv.Key);
                w.WriteString(kv.Value);
            }
            var switched = _states.Switched;
            w.WriteInt32(switched.Count);
            foreach (var name in switched) w.WriteString(name);
        }

        public void Read(ISaveReader r, int version)
        {
            _states.Clear();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                var status = (EnclaveStatus)r.ReadInt32();
                bool raid = r.ReadBool(), revolution = r.ReadBool();
                _states.Set(name, new EnclaveState(status, raid, revolution));
            }
            if (version < 2) return;
            int alignments = r.ReadInt32();
            for (int i = 0; i < alignments; i++)
            {
                string name = r.ReadString();
                _states.SetAlignment(name, r.ReadString());
            }
            int switched = r.ReadInt32();
            for (int i = 0; i < switched; i++) _states.MarkSwitched(r.ReadString());
        }
    }
}
