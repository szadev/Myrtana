using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// Gothic 3 constants the routine import needs: the gESpecies names and the daily schedule per species
    /// (gameplay-logic.md §5.5, Docs/gameplay-data/routine_schedule.json; same in CP 1.75).
    /// </summary>
    public static class G3RoutineTables
    {
        /// <summary>gESpecies by value (0 Human … 52 Dragon).</summary>
        static readonly string[] Species =
        {
            "Human", "Skeleton", "Zombie", "Golem", "Demon", "Orc", "Goblin", "Troll", "Minecrawler", "Scavenger", "Wolf", "Boar",
            "Sabertooth", "Shadowbeast", "Bison", "Rhino", "Ripper", "Lurker", "Varan", "Snapper", "Alligator", "Trex", "FireVaran",
            "Bloodfly", "Lizard", "Fish", "Meatbug", "Vulture", "Rabbit", "Deer", "Chicken", "Molerat", "Snake", "Pig", "Cow",
            "Spider", "Turtle", "Bird", "Gargoyle", "SwampLurker", "EMPTY_B", "Ogre", "Ray", "Scorpion", "Seagull", "Walrus",
            "Whale", "Eagle", "FireGolem", "IceGolem", "ScorpionKing", "Stalker", "Dragon",
        };

        /// <summary>Species name of a gESpecies value (null when unknown).</summary>
        public static string SpeciesName(int value) => value >= 0 && value < Species.Length ? Species[value] : null;

        /// <summary>The G3 schedule. Hours not listed are work (G3: "active") hours; species not listed work all day.</summary>
        public static RoutineScheduleDef Schedule() => new RoutineScheduleDef(
            new Id<RoutineScheduleDef>(RoutineScheduleDef.DefaultId),
            new List<SpeciesSchedule>
            {
                Group(new[] { "Human", "Orc" }, Span(DayPhase.Sleep, 0, 6), Span(DayPhase.Relax, 20, 24)),
                Group(new[] { "Scavenger", "Boar", "Bison", "Rhino", "Pig", "Cow" }, Span(DayPhase.Sleep, 0, 5), Span(DayPhase.Sleep, 23, 24)),
                Group(new[] { "Wolf", "Sabertooth", "Ripper" }, Span(DayPhase.Sleep, 4, 6)),
                Group(new[] { "Shadowbeast" }, Span(DayPhase.Sleep, 6, 20)),
            },
            stationStaySeconds: 2160);

        static SpeciesSchedule Group(IReadOnlyList<string> species, params PhaseSpan[] spans) => new SpeciesSchedule(species, spans);

        static PhaseSpan Span(DayPhase phase, float from, float to) => new PhaseSpan(phase, from, to);
    }
}
