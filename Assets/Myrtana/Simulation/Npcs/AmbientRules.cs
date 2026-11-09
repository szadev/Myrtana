using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// G3 rules for creatures without routine points (ZS_Ambient) and for the travel gait (GetTravellingWalkMode), CP 1.75
    /// Script_Game:
    /// <list type="bullet">
    /// <item>A non-humanoid NPC without a routine point wanders: it walks to a random point around its start position
    /// (ambient creatures 2–4 m, others 3–5 m), plays one ambient action there (EatGround 80 % for Scavenger, Bison,
    /// Rhino, Pig; 50 % for Boar, Deer, Cow; otherwise the plain Ambient idle) and walks on. Pigs and cows do not walk,
    /// they only play the action. In the sleep phase the creature sleeps where it is (ZS_Ambient_Sleep, checked every
    /// 60 s).</item>
    /// <item>Routine travel runs for Minecrawler, Scavenger, Wolf, Bison, Rhino and Snapper and walks for everyone else.</item>
    /// </list>
    /// </summary>
    public static class AmbientRules
    {
        /// <summary>Interaction names of the ambient actions (<see cref="NpcSimulation.InteractionOf"/>).</summary>
        public const string EatGround = "EatGround", Ambient = "Ambient", SleepGround = "SleepGround";

        /// <summary>Game seconds between ZS_Ambient_Sleep's checks for the end of the sleep phase.</summary>
        public const double SleepCheckSeconds = 60;

        /// <summary>Real seconds one ambient action lasts (G3 plays the action clip once) [interp].</summary>
        public const float ActionMinSeconds = 5f, ActionMaxSeconds = 12f;

        static readonly HashSet<string> Runners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Minecrawler", "Scavenger", "Wolf", "Bison", "Rhino", "Snapper" };
        static readonly HashSet<string> Grazers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Scavenger", "Bison", "Rhino", "Pig" };
        static readonly HashSet<string> HalfGrazers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Boar", "Deer", "Cow" };
        static readonly HashSet<string> StayPut = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pig", "Cow" };

        /// <summary>G3 GetTravellingWalkMode: the species runs to its routine points.</summary>
        public static bool RunsWhenTravelling(string species) => species != null && Runners.Contains(species);

        /// <summary>G3 IsHumanoid: humans and orcs (from the combat facts when the NPC has them).</summary>
        public static bool IsHumanoid(NpcDef def)
        {
            if (def.Combat != null) return def.Combat.Humanoid;
            string species = def.Species ?? "Human";
            return string.Equals(species, "Human", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(species, "Orc", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>SelectRoutineTask: a non-humanoid without routine points runs ZS_Ambient.</summary>
        public static bool Wanders(NpcDef def, NpcRoutineDef routine) => routine == null && !IsHumanoid(def);

        /// <summary>ZS_Ambient_Loop: pigs and cows play their action without walking.</summary>
        public static bool StaysPut(string species) => species != null && StayPut.Contains(species);

        /// <summary>Distance of the next wander point from the start position, m (G3: 200–400 / 300–500 cm).</summary>
        public static float WanderRadius(bool ambientCreature, IRandom random) =>
            (ambientCreature ? 2f : 3f) + random.Next(200) / 100f;

        /// <summary>GetAmbientAction: EatGround with the species' chance, otherwise the plain Ambient idle.</summary>
        public static string ActionOf(string species, IRandom random)
        {
            int eatPercent = species == null ? 0 : Grazers.Contains(species) ? 80 : HalfGrazers.Contains(species) ? 50 : 0;
            return eatPercent > 0 && random.Next(100) < eatPercent ? EatGround : Ambient;
        }

        /// <summary>Real seconds of one ambient action.</summary>
        public static float ActionSeconds(IRandom random) =>
            ActionMinSeconds + random.NextFloat() * (ActionMaxSeconds - ActionMinSeconds);

        /// <summary>The next wander point: <paramref name="radius"/> m from <paramref name="home"/> in a random direction, facing the way.</summary>
        public static WorldPose WanderPoint(WorldPose home, WorldPose from, float radius, IRandom random)
        {
            double angle = random.NextFloat() * 2 * Math.PI;
            var point = new WorldPose(home.X + (float)(Math.Sin(angle) * radius), home.Y, home.Z + (float)(Math.Cos(angle) * radius), 0);
            return new WorldPose(point.X, point.Y, point.Z, from.YawTowards(point));
        }
    }
}
