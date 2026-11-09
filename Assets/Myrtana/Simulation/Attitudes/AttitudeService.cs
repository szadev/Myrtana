using System;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// G3 GetAttitude as one rule chain (attitudes.json ruleOrder; Docs/perception-crime.md §5): friend-type mirror,
    /// flight, outlaws, party, the player rules (party, political crimes, fame of bosses and guards, enclave and
    /// personal crime memory, lost and cancelled fights), the political matrix and the species table. Replaces the
    /// combat stub CombatAttitudes (deleted). The first rule with an opinion wins.
    /// </summary>
    public sealed class AttitudeService
    {
        readonly AttitudeRules _rules;
        readonly CombatAiRules _species;
        readonly IAttitudeFacts _facts;
        readonly CrimeLedger _crimes;
        readonly FameLedger _fame;
        readonly Func<int> _chapter;
        readonly Func<double> _gameHours;
        Func<string, string, Attitude?> _alignmentToward; // the method group made once (a delegate per Of call otherwise, perf-9)

        /// <param name="species">The species table (the same data the combat stub used).</param>
        /// <param name="chapter">The story chapter (the orc–Hashishin alliance breaks in chapter 2).</param>
        /// <param name="gameHours">Game hours since the start (grudges and duels expire).</param>
        public AttitudeService(AttitudeRules rules, CombatAiRules species, IAttitudeFacts facts, CrimeLedger crimes, FameLedger fame,
                               Func<int> chapter, Func<double> gameHours)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _species = species ?? throw new ArgumentNullException(nameof(species));
            _facts = facts ?? throw new ArgumentNullException(nameof(facts));
            _crimes = crimes ?? throw new ArgumentNullException(nameof(crimes));
            _fame = fame ?? throw new ArgumentNullException(nameof(fame));
            _chapter = chapter ?? (() => 1);
            _gameHours = gameHours ?? (() => 0);
        }

        public AttitudeRules Rules => _rules;
        public IAttitudeFacts Facts => _facts;
        /// <summary>Raiding, fighting or fleeing enclaves for the revolution rule (null: the rule has no opinion).</summary>
        public IEnclaveStates Enclaves { get; set; }

        /// <summary>Political attitude of one alignment toward another; the same alignment is Friendly.</summary>
        Attitude? AlignmentToward(string self, string toward)
        {
            if (string.IsNullOrEmpty(self) || string.IsNullOrEmpty(toward)) return null;
            return self == toward ? Attitude.Friendly : _rules.PoliticalAttitude(self, toward, _chapter()) ?? Attitude.Neutral;
        }

        /// <summary>
        /// The attitude of <paramref name="self"/> toward <paramref name="other"/>. The player has no attitude of its
        /// own; it mirrors the other side's (as the combat presenter did with the stub) <b>[interp]</b>.
        /// </summary>
        public Attitude Of(ActorId self, ActorId other)
        {
            if (self.IsEmpty || other.IsEmpty) return Attitude.Neutral;
            if (self == other) return Attitude.Friendly;
            if (self.IsPlayer) return Of(other, self);
            if (!_facts.TryGetProfile(self, out var s) || !_facts.TryGetProfile(other, out var o)) return Attitude.Neutral;
            return Of(s, o);
        }

        public Attitude Of(in AttitudeProfile self, in AttitudeProfile other)
        {
            if (self.FriendType)
            {
                if (other.PartyLeader == self.Id || other.IsPlayer) return Attitude.Friendly;
                var mirrored = Of(other.Id, ActorId.Player);
                if (mirrored == Attitude.Hostile || mirrored == Attitude.Friendly) return mirrored;
            }
            if (self.Fleeing && self.FleeingFrom == other.Id) return Attitude.Panic;
            if (IsOutlawPair(self, other)) return Attitude.Hostile;
            if (RevolutionAttitude.Of(self, other, Enclaves, _alignmentToward ??= AlignmentToward) is Attitude revolution) return revolution;
            if (Leads(self, other) || Leads(other, self)) return Attitude.Friendly;
            if (other.PartyRole == PartyRole.Controlled && other.InPlayerParty && self.Humanoid) return Attitude.Neutral;
            if (other.IsPlayer && TowardPlayer(self, other) is Attitude toPlayer) return toPlayer;
            if (Political(self, other) is Attitude political) return political;
            return Species(self, other);
        }

        /// <summary>G3 GetRevolutionAttitude of <paramref name="self"/> toward <paramref name="other"/> (null = no opinion).</summary>
        public Attitude? RevolutionOf(in AttitudeProfile self, in AttitudeProfile other) =>
            RevolutionAttitude.Of(self, other, Enclaves, _alignmentToward ??= AlignmentToward);

        /// <summary>
        /// The NPC holds a grudge against the player (G3 IsUpset): its last fight against the player was cancelled
        /// and not long enough ago for the fight's reason. Perception step 2 then attacks with that reason.
        /// </summary>
        public bool IsUpset(ActorId npc, out AttackReason reason)
        {
            var fight = _crimes.FightOf(npc);
            reason = fight.Reason;
            if (fight.Result != FightResult.Cancelled || PlayerTransformed()) return false;
            return _gameHours() - fight.GameHours < _rules.Grudges.DurationOf(fight.Reason);
        }

        /// <summary>The enclave's or faction's fame gate (75) used by bosses, guards and zone owners.</summary>
        public bool MeetsFame(FameKind kind, string group) => group != null && _fame.Of(kind, group) >= _rules.FameThreshold;

        /// <summary>
        /// Whether the NPC is politically friendly to its own enclave (its guards and witnesses report to it, it defends it,
        /// gives up or dies with it): G3 GetPoliticalAttitude(npc, enclave) == Friendly, read from the NPC's own alignment.
        /// An NPC without one is never loyal (fix-94: before, every unaligned member counted as loyal). An enclave without a
        /// known alignment keeps every member loyal [interp: incomplete data].
        /// </summary>
        public bool LoyalToEnclave(in AttitudeProfile npc)
        {
            if (npc.Enclave == null || npc.Alignment == null) return false;
            return npc.EnclaveAlignment == null || PoliticallyFriendly(npc.Alignment, npc.EnclaveAlignment);
        }

        /// <summary>Same alignment, or friendly in the political matrix (this chapter); false when either is null.</summary>
        public bool PoliticallyFriendly(string self, string toward) =>
            self != null && toward != null && (self == toward || _rules.PoliticalAttitude(self, toward, _chapter()) == Attitude.Friendly);

        Attitude? TowardPlayer(in AttitudeProfile self, in AttitudeProfile player)
        {
            if (self.InPlayerParty) return Attitude.Friendly;
            if (self.PartyRole == PartyRole.Controlled) return Attitude.Neutral;
            if (self.PartyRole == PartyRole.Summoned) return Attitude.Hostile;
            if (player.Transformed) return null;
            if (self.Alignment != null && _crimes.PoliticalCrimesAgainst(self.Alignment) > 0) return Attitude.Hostile;
            if (RecentDuel(self.Id)) return Attitude.Friendly;
            if (self.Boss) return MeetsFame(FameKind.Political, self.Alignment) ? Attitude.Friendly : Attitude.Neutral;
            if (!self.Guard)
            {
                if (_crimes.Enclave(self.Enclave).Known == CrimeType.Murder && LoyalToEnclave(self)) return Attitude.Angry;
                var personal = _crimes.PersonalOf(self.Id);
                if (personal == CrimeType.Murder) return Attitude.Hostile;
                var fight = _crimes.FightOf(self.Id).Result;
                if (personal == CrimeType.Theft || fight == FightResult.PlayerLost || fight == FightResult.Cancelled) return Attitude.Angry;
                return self.StoredAttitudeToPlayer;
            }
            if (!LoyalToEnclave(self)) return null;
            var known = _crimes.Enclave(self.Enclave).Known;
            if (known == CrimeType.Murder) return Attitude.Hostile;
            if (known == CrimeType.Theft) return Attitude.Angry;
            return MeetsFame(FameKind.Enclave, self.Enclave) ? Attitude.Friendly : Attitude.Neutral;
        }

        bool RecentDuel(ActorId npc)
        {
            var fight = _crimes.FightOf(npc);
            return fight.Result == FightResult.Friendly && _gameHours() - fight.GameHours < _rules.Grudges.FriendlyAfterDuelHours;
        }

        bool PlayerTransformed() => _facts.TryGetProfile(ActorId.Player, out var p) && p.Transformed;

        /// <summary>One of the two leads the other's party (a companion of the player, or the guide leading the hero).</summary>
        public bool PartyBond(ActorId a, ActorId b) =>
            !a.IsEmpty && !b.IsEmpty && _facts.TryGetProfile(a, out var pa) && _facts.TryGetProfile(b, out var pb) &&
            (Leads(pa, pb) || Leads(pb, pa));

        static bool Leads(in AttitudeProfile leader, in AttitudeProfile member) => !member.PartyLeader.IsEmpty && member.PartyLeader == leader.Id;

        bool IsOutlawPair(in AttitudeProfile a, in AttitudeProfile b) =>
            (IsOutlaw(a) && b.Humanoid && !IsOutlaw(b) && b.Alignment != _rules.PirateAlignment) ||
            (IsOutlaw(b) && a.Humanoid && !IsOutlaw(a) && a.Alignment != _rules.PirateAlignment);

        bool IsOutlaw(in AttitudeProfile p) => _rules.IsOutlaw(p.Alignment);

        /// <summary>
        /// The political matrix between the two sides' alignments. An NPC's political alignment is its enclave's, else its
        /// own <b>[interp]</b>, except a slave's own view: G3 GetPoliticalAttitude reads an NPC's own alignment (the enclave
        /// PS only of an enclave entity) <b></b>, and the Slave row is Neutral to everyone, so the slaves of an orc town
        /// never side with their masters nor count as owners of their zones (fix-18). Taking the own alignment everywhere,
        /// as G3 does, would also change how the orcs see their slaves (they defend them now) and turn the Hashishin of
        /// Montera and the orcs of the Mora Sul temple against their hosts in chapter 2; that is left as it was.
        /// </summary>
        Attitude? Political(in AttitudeProfile self, in AttitudeProfile other) =>
            _rules.PoliticalAttitude(self.Alignment == CrimeSystem.SlaveAlignment ? self.Alignment : PoliticalAlignmentOf(self),
                                     PoliticalAlignmentOf(other), _chapter(), other.Id.Name);

        /// <remarks>
        /// An NPC without a political alignment of its own (gCNPC_PS PoliticalAlignment None) has none at all, whatever its
        /// enclave: G3 GetPoliticalAttitude reads the NPC's own alignment and returns "no opinion" for None. Kliff,
        /// the smith the orc patrol keeps on the farm east of Reddock, belongs to the rebel enclave Reddock but has no
        /// alignment, so the orcs leave him alone until the hero frees him (fix-91; before, the enclave's Reb made the
        /// orcs and him enemies on sight).
        /// </remarks>
        /// <remarks>
        /// fix-94: an alignment the story set (SetPAL: Knut and the Bakaresh rebels to Nom, Rufus, Sanford and the Okara
        /// rangers to Reb, Wenzel to Reb …) counts over the enclave's, as G3 reads only the NPC's own: before, the four of
        /// Bakaresh stayed Hashishin and the revolution there could not start a fight.
        /// </remarks>
        /// <remarks>
        /// fix-129: an NPC whose own alignment is Hostile to its enclave's (chapter-1 matrix) keeps its own, as G3 does: the
        /// rebels of the camps outside Montera (Daryl, Mason, Otis and their men) belong to the orc enclave Montera for its
        /// liberation but are Reb, so the orcs Daryl's ambush quest lures to their campfire are their enemies on sight; the
        /// nomads outside Al Shedim and Lago likewise. Before, the enclave's Orc made them friends of the orcs: the lured orc
        /// sat at the rebels' fire and a hit of the hero on it called the rebels against the hero. Allies of their enclave
        /// (Hashishin in the orc towns, orcs of the Mora Sul temple) keep the enclave's view, also after the chapter-2 break.
        /// </remarks>
        string PoliticalAlignmentOf(in AttitudeProfile p)
        {
            if (p.Alignment == null) return null;
            if (p.AlignmentFromStory || p.EnclaveAlignment == null) return p.Alignment;
            return AtOddsWithEnclave(p.Alignment, p.EnclaveAlignment) ? p.Alignment : p.EnclaveAlignment;
        }

        /// <summary>The own alignment is Hostile to the enclave's in the chapter-1 matrix (a rebel camp of an orc town).</summary>
        public bool AtOddsWithEnclave(string own, string enclave) =>
            own != null && enclave != null && own != enclave && _rules.PoliticalAttitude(own, enclave, 1) == Attitude.Hostile;

        /// <summary>The species table (attitudes.json speciesTable, kept in CombatAiRules).</summary>
        Attitude Species(in AttitudeProfile self, in AttitudeProfile other)
        {
            if (self.Humanoid)
            {
                if (other.Evil || _species.HumanoidEnemies.Contains(other.Species)) return Attitude.Hostile;
                // [interp] Until enclave and alignment data are imported, two unaligned humanoids of one species are
                // friends (the combat stub's rule), so friends still defend each other.
                bool unaligned = PoliticalAlignmentOf(self) == null && PoliticalAlignmentOf(other) == null && !other.IsPlayer;
                return unaligned && self.Species == other.Species ? Attitude.Friendly : Attitude.Neutral;
            }
            if (other.AmbientCreature) return Attitude.Neutral;
            if (string.Equals(self.Species, other.Species, StringComparison.Ordinal))
                return other.IsPlayer ? Attitude.Neutral : Attitude.Friendly;
            if (self.Evil)
            {
                if (other.Evil) return Attitude.Friendly;
                if (other.Humanoid) return Attitude.Hostile;
            }
            foreach (var row in _species.Attitudes)
                if (row.Self.Contains(self.Species) && row.Toward.Contains(other.Species))
                    return row.Attitude;
            return Attitude.Neutral;
        }
    }
}
