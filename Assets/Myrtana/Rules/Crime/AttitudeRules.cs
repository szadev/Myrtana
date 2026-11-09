using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>One cell of the political matrix: how members of <see cref="Self"/> regard members of <see cref="Toward"/>.</summary>
    public sealed class PoliticalRelation
    {
        [JsonConstructor]
        public PoliticalRelation(string self, string toward, Attitude attitude, int? chapter = null, IReadOnlyList<string> exceptNames = null)
        {
            Self = self;
            Toward = toward;
            Attitude = attitude;
            Chapter = chapter;
            ExceptNames = exceptNames ?? Array.Empty<string>();
        }

        public string Self { get; }
        public string Toward { get; }
        public Attitude Attitude { get; }
        /// <summary>Only in this story chapter (an override of the chapter-less cell); null = always.</summary>
        public int? Chapter { get; }
        /// <summary>Actors (name contains one of these) the chapter override does not apply to.</summary>
        public IReadOnlyList<string> ExceptNames { get; }
    }

    /// <summary>
    /// The data of the attitude rule chain (attitudes.json): the political matrix, the fame threshold of bosses and
    /// guards, and how long an NPC stays upset after a fight with the player. Species attitudes stay in
    /// <c>CombatAiRules</c> (the combat stub's table). Docs/perception-crime.md §5.
    /// </summary>
    public sealed class AttitudeRules : IDefinition
    {
        public const string DefinitionId = "attitudeRules";

        [JsonConstructor]
        public AttitudeRules(string id, IReadOnlyList<PoliticalRelation> political, IReadOnlyList<string> outlawAlignments,
                             string pirateAlignment, int fameThreshold, GrudgeRules grudges)
        {
            Id = id ?? DefinitionId;
            Political = political ?? Array.Empty<PoliticalRelation>();
            OutlawAlignments = outlawAlignments ?? Array.Empty<string>();
            PirateAlignment = pirateAlignment;
            FameThreshold = fameThreshold;
            Grudges = grudges ?? GrudgeRules.None;
        }

        public string Id { get; }
        public IReadOnlyList<PoliticalRelation> Political { get; }
        /// <summary>Alignments hostile to every humanoid outside them ("Outlaw").</summary>
        public IReadOnlyList<string> OutlawAlignments { get; }
        /// <summary>The alignment that takes its enclave's alignment (CP "Pirate"); null = none.</summary>
        public string PirateAlignment { get; }
        /// <summary>Fame (0–100) from which bosses and guards are Friendly (75).</summary>
        public int FameThreshold { get; }
        public GrudgeRules Grudges { get; }

        string IDefinition.Key => Id;

        /// <summary>The political attitude of <paramref name="self"/> toward <paramref name="toward"/>; null = no opinion.</summary>
        public Attitude? PoliticalAttitude(string self, string toward, int chapter, string towardName = null)
        {
            if (string.IsNullOrEmpty(self) || string.IsNullOrEmpty(toward)) return null;
            // The rows of self, in matrix order (an index built once: this runs for most pairs of NPCs an active scan looks
            // at; scanning the whole matrix through an enumerator cost an allocation and ~76 string tests per call, perf-9).
            if (!BySelf.TryGetValue(self, out var rows)) return null;
            PoliticalRelation always = null;
            foreach (var r in rows)
            {
                if (r.Toward != toward) continue;
                if (r.Chapter == null) { always = r; continue; }
                if (r.Chapter == chapter && !IsExcepted(r, towardName)) return r.Attitude;
            }
            return always?.Attitude;
        }

        Dictionary<string, PoliticalRelation[]> _bySelf;

        Dictionary<string, PoliticalRelation[]> BySelf => _bySelf ??= Political
            .Where(r => r != null && r.Self != null)
            .GroupBy(r => r.Self, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        static bool IsExcepted(PoliticalRelation r, string name) =>
            name != null && r.ExceptNames.Any(n => name.IndexOf(n, StringComparison.Ordinal) >= 0);

        public bool IsOutlaw(string alignment) => alignment != null && OutlawAlignments.Contains(alignment);

        /// <summary>No politics, no grudges (a pack without attitude rules).</summary>
        public static AttitudeRules Neutral() => new AttitudeRules(DefinitionId, null, null, null, 75, GrudgeRules.None);
    }

    /// <summary>
    /// How long an NPC stays upset after a fight with the player that ended without a result (the player ran off), by
    /// the fight's reason (attitudes.json grudges). Durations are game hours.
    /// </summary>
    public sealed class GrudgeRules
    {
        public static readonly GrudgeRules None = new GrudgeRules(null, 0, null, 0, null, 0);

        [JsonConstructor]
        public GrudgeRules(IReadOnlyList<AttackReason> minorReasons, double minorHours, IReadOnlyList<AttackReason> majorReasons,
                           double majorHours, IReadOnlyList<AttackReason> foreverReasons, double friendlyAfterDuelHours)
        {
            MinorReasons = minorReasons ?? Array.Empty<AttackReason>();
            MinorHours = minorHours;
            MajorReasons = majorReasons ?? Array.Empty<AttackReason>();
            MajorHours = majorHours;
            ForeverReasons = foreverReasons ?? Array.Empty<AttackReason>();
            FriendlyAfterDuelHours = friendlyAfterDuelHours;
        }

        public IReadOnlyList<AttackReason> MinorReasons { get; }
        public double MinorHours { get; }
        public IReadOnlyList<AttackReason> MajorReasons { get; }
        public double MajorHours { get; }
        public IReadOnlyList<AttackReason> ForeverReasons { get; }
        /// <summary>A friendly fight (duel) keeps the loser "temporarily friendly" this long.</summary>
        public double FriendlyAfterDuelHours { get; }

        /// <summary>How long a cancelled fight with this reason is held against the player: 0 = not at all.</summary>
        public double DurationOf(AttackReason reason)
        {
            if (ForeverReasons.Contains(reason)) return double.PositiveInfinity;
            if (MajorReasons.Contains(reason)) return MajorHours;
            if (MinorReasons.Contains(reason)) return MinorHours;
            return 0;
        }
    }
}
