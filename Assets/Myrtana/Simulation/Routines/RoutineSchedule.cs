using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>Part of the day an NPC's routine is in; each phase has its own routine point.</summary>
    public enum DayPhase { Work, Relax, Sleep }

    /// <summary>A phase from <see cref="From"/> to <see cref="To"/> o'clock (From &lt; To, both 0–24).</summary>
    public sealed class PhaseSpan
    {
        [JsonConstructor]
        public PhaseSpan(DayPhase phase, float from, float to)
        {
            Phase = phase;
            From = from;
            To = to;
        }

        public DayPhase Phase { get; }
        public float From { get; }
        public float To { get; }

        public bool Contains(float hour) => hour >= From && hour < To;
    }

    /// <summary>The daily schedule of a group of species. Hours not covered by a span are work hours.</summary>
    public sealed class SpeciesSchedule
    {
        [JsonConstructor]
        public SpeciesSchedule(IReadOnlyList<string> species, IReadOnlyList<PhaseSpan> spans)
        {
            Species = species ?? Array.Empty<string>();
            Spans = spans ?? Array.Empty<PhaseSpan>();
        }

        public IReadOnlyList<string> Species { get; }
        public IReadOnlyList<PhaseSpan> Spans { get; }
    }

    /// <summary>
    /// When NPCs work, relax and sleep, by species (gameplay-logic.md §5.5; G3: humans and orcs sleep 0–6 h, work 6–20 h,
    /// relax 20–24 h; grazers, wolves and shadowbeasts have their own hours; everything else works all day).
    /// <see cref="StationStaySeconds"/> is how long (game seconds) an NPC stays at one station of a roam or local anchor
    /// before it moves on (G3 re-plans off-screen NPCs after 2160 s).
    /// </summary>
    public sealed class RoutineScheduleDef : IDefinition
    {
        public const string DefaultId = "default";

        [JsonConstructor]
        public RoutineScheduleDef(Id<RoutineScheduleDef> id, IReadOnlyList<SpeciesSchedule> groups, double stationStaySeconds = 2160)
        {
            Id = id;
            Groups = groups ?? Array.Empty<SpeciesSchedule>();
            StationStaySeconds = stationStaySeconds;
        }

        public Id<RoutineScheduleDef> Id { get; }
        public IReadOnlyList<SpeciesSchedule> Groups { get; }
        public double StationStaySeconds { get; }

        string IDefinition.Key => Id.Name;
    }

    /// <summary>Answers "which phase is this species in at this hour" from a <see cref="RoutineScheduleDef"/>.</summary>
    public sealed class RoutineSchedule
    {
        readonly Dictionary<string, SpeciesSchedule> _bySpecies = new Dictionary<string, SpeciesSchedule>(StringComparer.OrdinalIgnoreCase);

        public RoutineSchedule(RoutineScheduleDef definition)
        {
            Definition = definition ?? new RoutineScheduleDef(new Id<RoutineScheduleDef>(RoutineScheduleDef.DefaultId), null);
            foreach (var group in Definition.Groups)
            foreach (var species in group.Species)
                _bySpecies[species] = group;
        }

        public RoutineScheduleDef Definition { get; }
        public double StationStaySeconds => Definition.StationStaySeconds;

        /// <summary>The schedule from the content (<see cref="RoutineScheduleDef.DefaultId"/>), or "always work" without one.</summary>
        public static RoutineSchedule From(IContentDatabase content) =>
            new RoutineSchedule(content.TryGet(new Id<RoutineScheduleDef>(RoutineScheduleDef.DefaultId), out var def) ? def : null);

        public DayPhase PhaseOf(string species, float hourOfDay)
        {
            if (species == null || !_bySpecies.TryGetValue(species, out var group)) return DayPhase.Work;
            float hour = hourOfDay % 24f;
            if (hour < 0) hour += 24f;
            var spans = group.Spans; // a loop, not FirstOrDefault: per NPC per frame, no closure (perf-9)
            for (int i = 0; i < spans.Count; i++)
                if (spans[i] != null && spans[i].Contains(hour)) return spans[i].Phase;
            return DayPhase.Work;
        }
    }
}
