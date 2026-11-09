using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What one enclave (village, camp, city) knows about the player's crimes.</summary>
    public sealed class EnclaveCrimeRecord
    {
        internal readonly int[] Counts = new int[CrimeKinds];
        internal readonly int[] DiscussedCounts = new int[CrimeKinds];
        const int CrimeKinds = (int)CrimeType.Murder + 1;

        /// <summary>The worst crime a witness reported to the enclave (G3 KnownPlayerCrime).</summary>
        public CrimeType Known { get; internal set; }

        /// <summary>
        /// How often the crime happened in the enclave (G3 CrimeCount[crime]): thefts are counted even unseen (+1 per
        /// take, + stacks from a chest), murders of the enclave's political friends on every kill.
        /// </summary>
        public int CountOf(CrimeType crime) => Counts[(int)crime];

        /// <summary>The count the guards have already talked about with the player (G3 SuspectComment[crime]).</summary>
        public int DiscussedOf(CrimeType crime) => DiscussedCounts[(int)crime];

        public bool IsClean => Known == CrimeType.None && Counts.All(c => c == 0) && DiscussedCounts.All(c => c == 0);
    }

    /// <summary>
    /// How an NPC's last fight against the player ended (G3 LastFightAgainstPlayer). Named from the player's side, as in
    /// G3: a knocked-out NPC remembers <see cref="PlayerWon"/>, an NPC that beat the player <see cref="PlayerLost"/>.
    /// </summary>
    public enum FightResult
    {
        None,
        /// <summary>The player knocked the NPC out.</summary>
        PlayerWon,
        /// <summary>The NPC knocked out the player.</summary>
        PlayerLost,
        /// <summary>The fight stopped without a result (the player ran off, the NPC gave up the chase).</summary>
        Cancelled,
        /// <summary>A friendly fight (duel) ended.</summary>
        Friendly,
    }

    /// <summary>An NPC's memory of its last fight against the player (G3 LastFightAgainstPlayer + timestamp).</summary>
    public readonly struct FightMemory
    {
        public FightMemory(FightResult result, AttackReason reason, double gameHours)
        {
            Result = result;
            Reason = reason;
            GameHours = gameHours;
        }

        public FightResult Result { get; }
        public AttackReason Reason { get; }
        /// <summary>Game hours since the start of the game when the fight ended.</summary>
        public double GameHours { get; }
    }

    /// <summary>
    /// The player's criminal record: per enclave (reported crimes), per NPC (personal memory of a crime and of the last
    /// fight) and per political faction (liberated enclaves). Plain state, saved by <see cref="CrimeSaveSection"/>.
    /// Docs/perception-crime.md §3 and §6.
    /// </summary>
    public sealed class CrimeLedger : IActorStore
    {
        readonly Dictionary<string, EnclaveCrimeRecord> _enclaves = new Dictionary<string, EnclaveCrimeRecord>(StringComparer.Ordinal);
        readonly Dictionary<ActorId, CrimeType> _personal = new Dictionary<ActorId, CrimeType>();
        readonly Dictionary<ActorId, FightMemory> _fights = new Dictionary<ActorId, FightMemory>();
        readonly Dictionary<string, int> _political = new Dictionary<string, int>(StringComparer.Ordinal);

        static readonly EnclaveCrimeRecord Clean = new EnclaveCrimeRecord();

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            bool changed = _personal.Remove(actor);
            _fights.Remove(actor);
            if (changed) Changed?.Invoke();
        }

        public bool Holds(ActorId actor) => _personal.ContainsKey(actor) || _fights.ContainsKey(actor);

        public event Action Changed;

        /// <summary>The enclave's record (a shared empty record for an enclave that knows nothing).</summary>
        public EnclaveCrimeRecord Enclave(string enclave) =>
            enclave != null && _enclaves.TryGetValue(enclave, out var r) ? r : Clean;

        public IEnumerable<KeyValuePair<string, EnclaveCrimeRecord>> Enclaves =>
            _enclaves.Where(kv => !kv.Value.IsClean).OrderBy(kv => kv.Key, StringComparer.Ordinal);

        /// <summary>
        /// A politically friendly witness told its enclave (G3 MemorizeEnclaveCrime): the known crime escalates and the
        /// guards' news so far counts as discussed for that crime (they will bring up only what happens next).
        /// </summary>
        public void Report(string enclave, CrimeType crime)
        {
            if (string.IsNullOrEmpty(enclave) || crime == CrimeType.None) return;
            var r = RecordOf(enclave);
            if (crime < r.Known) return;
            r.Known = crime;
            r.DiscussedCounts[(int)crime] = r.Counts[(int)crime];
            Changed?.Invoke();
        }

        /// <summary>The enclave counts a crime (G3 CrimeCount, AddEnclaveTheftCount), seen or not.</summary>
        public void Count(string enclave, CrimeType crime, int count = 1)
        {
            if (string.IsNullOrEmpty(enclave) || crime == CrimeType.None || count <= 0) return;
            RecordOf(enclave).Counts[(int)crime] += count;
            Changed?.Invoke();
        }

        /// <summary>The guards told the player everything the enclave counted so far.</summary>
        public void MarkDiscussed(string enclave, CrimeType crime)
        {
            if (enclave == null || !_enclaves.TryGetValue(enclave, out var r)) return;
            if (r.DiscussedCounts[(int)crime] == r.Counts[(int)crime]) return;
            r.DiscussedCounts[(int)crime] = r.Counts[(int)crime];
            Changed?.Invoke();
        }

        EnclaveCrimeRecord RecordOf(string enclave)
        {
            if (!_enclaves.TryGetValue(enclave, out var r)) _enclaves[enclave] = r = new EnclaveCrimeRecord();
            return r;
        }

        /// <summary>
        /// The enclave forgives everything (G3 ChangeEnclaveStatus → Routine, info command ResetCrime): the record is
        /// cleared, its members forget their personal grievances, and the player's fame there becomes
        /// <paramref name="fameAfter"/> (null = unchanged).
        /// </summary>
        public void Amnesty(string enclave, IEnumerable<ActorId> members, FameLedger fame, int? fameAfter)
        {
            if (string.IsNullOrEmpty(enclave)) return;
            _enclaves.Remove(enclave);
            if (members != null)
                foreach (var m in members)
                    _personal.Remove(m);
            if (fame != null && fameAfter is int value) fame.Set(FameKind.Enclave, enclave, value);
            Changed?.Invoke();
        }

        /// <summary>The crime an NPC remembers personally (G3 LastPlayerCrime).</summary>
        public CrimeType PersonalOf(ActorId npc) => _personal.TryGetValue(npc, out var c) ? c : CrimeType.None;

        public IEnumerable<KeyValuePair<ActorId, CrimeType>> Personal => _personal.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal);

        public void RememberPersonally(ActorId npc, CrimeType crime)
        {
            if (npc.IsEmpty || npc.IsPlayer || crime == CrimeType.None) return;
            var before = PersonalOf(npc);
            var after = before.Escalate(crime);
            if (after == before) return;
            _personal[npc] = after;
            Changed?.Invoke();
        }

        /// <summary>The NPC forgets its personal grievance (bribe, excuse, or it settled the matter by beating the player).</summary>
        public void ForgivePersonally(ActorId npc)
        {
            if (_personal.Remove(npc)) Changed?.Invoke();
        }

        public FightMemory FightOf(ActorId npc) => _fights.TryGetValue(npc, out var f) ? f : default;

        public IEnumerable<KeyValuePair<ActorId, FightMemory>> Fights => _fights.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal);

        public void RecordFight(ActorId npc, FightResult result, AttackReason reason, double gameHours)
        {
            if (npc.IsEmpty || npc.IsPlayer) return;
            if (result == FightResult.None) _fights.Remove(npc);
            else _fights[npc] = new FightMemory(result, reason, gameHours);
            Changed?.Invoke();
        }

        /// <summary>Crimes against a whole political faction (G3 PoliticalCrimeCount: a liberated enclave).</summary>
        public int PoliticalCrimesAgainst(string faction) =>
            faction != null && _political.TryGetValue(faction, out var n) ? n : 0;

        public IEnumerable<KeyValuePair<string, int>> Political => _political.OrderBy(kv => kv.Key, StringComparer.Ordinal);

        public void AddPoliticalCrime(string faction, int count = 1)
        {
            if (string.IsNullOrEmpty(faction) || count <= 0) return;
            _political[faction] = PoliticalCrimesAgainst(faction) + count;
            Changed?.Invoke();
        }

        /// <summary>
        /// Dev (scenario op <c>safe</c>, fix-65 R32-4): every grudge from a fight the player got away from (Cancelled:
        /// G3 IsUpset makes the NPC attack again at once) and every crime short of murder are forgotten, personally and by
        /// the enclaves. Murders, won or lost fights, duels and political crimes stay (the story reads them). Returns how
        /// many entries went.
        /// </summary>
        public int ForgiveGrudges()
        {
            int n = 0;
            foreach (var kv in _fights.Where(kv => kv.Value.Result == FightResult.Cancelled).ToList())
            {
                _fights.Remove(kv.Key);
                n++;
            }
            foreach (var kv in _personal.Where(kv => kv.Value != CrimeType.Murder).ToList())
            {
                _personal.Remove(kv.Key);
                n++;
            }
            foreach (var kv in _enclaves.Where(kv => kv.Value.Known != CrimeType.Murder && kv.Value.CountOf(CrimeType.Murder) == 0).ToList())
            {
                if (kv.Value.IsClean) continue;
                _enclaves.Remove(kv.Key);
                n++;
            }
            if (n > 0) Changed?.Invoke();
            return n;
        }

        internal void Clear()
        {
            _enclaves.Clear();
            _personal.Clear();
            _fights.Clear();
            _political.Clear();
        }

        internal void Restore(string enclave, CrimeType known, int[] counts, int[] discussed)
        {
            var r = new EnclaveCrimeRecord { Known = known };
            Array.Copy(counts, r.Counts, Math.Min(counts.Length, r.Counts.Length));
            Array.Copy(discussed, r.DiscussedCounts, Math.Min(discussed.Length, r.DiscussedCounts.Length));
            _enclaves[enclave] = r;
        }

        internal void RestorePersonal(ActorId npc, CrimeType crime) => _personal[npc] = crime;
        internal void RestoreFight(ActorId npc, FightMemory memory) => _fights[npc] = memory;
        internal void RestorePolitical(string faction, int count) => _political[faction] = count;

        /// <summary>Takes back one crime against a faction (a save migration); false when there was none.</summary>
        public bool RemovePoliticalCrime(string faction)
        {
            int n = PoliticalCrimesAgainst(faction);
            if (n <= 0) return false;
            if (n == 1) _political.Remove(faction);
            else _political[faction] = n - 1;
            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>Save section "rules.crime".</summary>
    public sealed class CrimeSaveSection : ISaveParticipant
    {
        readonly CrimeLedger _ledger;

        public CrimeSaveSection(CrimeLedger ledger) => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

        public string Section => "rules.crime";
        /// <summary>2 (fix-261): a liberation counts only after the hero's strike; older saves are migrated on load.</summary>
        public int Version => 2;
        /// <summary>The version of the section last read (0 = none read).</summary>
        public int ReadVersion { get; private set; }

        public void Write(ISaveWriter w)
        {
            var enclaves = _ledger.Enclaves.ToList();
            w.WriteInt32(enclaves.Count);
            foreach (var kv in enclaves)
            {
                w.WriteString(kv.Key);
                w.WriteInt32((int)kv.Value.Known);
                w.WriteInt32(kv.Value.Counts.Length);
                for (int i = 0; i < kv.Value.Counts.Length; i++)
                {
                    w.WriteInt32(kv.Value.Counts[i]);
                    w.WriteInt32(kv.Value.DiscussedCounts[i]);
                }
            }
            var personal = _ledger.Personal.ToList();
            w.WriteInt32(personal.Count);
            foreach (var kv in personal)
            {
                w.WriteString(kv.Key.Name);
                w.WriteInt32((int)kv.Value);
            }
            var fights = _ledger.Fights.ToList();
            w.WriteInt32(fights.Count);
            foreach (var kv in fights)
            {
                w.WriteString(kv.Key.Name);
                w.WriteInt32((int)kv.Value.Result);
                w.WriteInt32((int)kv.Value.Reason);
                w.WriteDouble(kv.Value.GameHours);
            }
            var political = _ledger.Political.ToList();
            w.WriteInt32(political.Count);
            foreach (var kv in political)
            {
                w.WriteString(kv.Key);
                w.WriteInt32(kv.Value);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            ReadVersion = version;
            _ledger.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string enclave = r.ReadString();
                var known = (CrimeType)r.ReadInt32();
                int kinds = r.ReadInt32();
                var counts = new int[kinds];
                var discussed = new int[kinds];
                for (int k = 0; k < kinds; k++)
                {
                    counts[k] = r.ReadInt32();
                    discussed[k] = r.ReadInt32();
                }
                _ledger.Restore(enclave, known, counts, discussed);
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) _ledger.RestorePersonal(new ActorId(r.ReadString()), (CrimeType)r.ReadInt32());
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var npc = new ActorId(r.ReadString());
                _ledger.RestoreFight(npc, new FightMemory((FightResult)r.ReadInt32(), (AttackReason)r.ReadInt32(), r.ReadDouble()));
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) _ledger.RestorePolitical(r.ReadString(), r.ReadInt32());
        }
    }
}
