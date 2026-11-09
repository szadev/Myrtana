#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session.Dev
{
    /// <summary>Why an NPC keeps the hero "in combat" (fix-151, console <c>enemies</c>).</summary>
    [Flags]
    public enum ThreatReason
    {
        None = 0,
        /// <summary>The save / object-use block "inCombat" (<see cref="NpcCombatSystem.InBattleWithPlayer"/>).</summary>
        Battle = 1,
        /// <summary>Fight music: it fights the hero or a party member within <see cref="MusicSituationRule.RadiusMetres"/>.</summary>
        Music = 2,
        /// <summary>Its combat target is the hero (any phase but flee).</summary>
        Hero = 4,
        /// <summary>Its combat target is a party member (companion, summon).</summary>
        Party = 8,
        /// <summary>A party member fights it (the companion runs off to it).</summary>
        Companion = 16,
    }

    /// <summary>One combat brain as the threat scan sees it (plain data, filled by the Unity side).</summary>
    public readonly struct ThreatFighter
    {
        public ThreatFighter(ActorId id, CombatPhase phase, ActorId target, bool holdsToken, bool atCoordinator, float distanceMetres)
        {
            Id = id;
            Phase = phase;
            Target = target;
            HoldsToken = holdsToken;
            AtCoordinator = atCoordinator;
            DistanceMetres = distanceMetres;
        }

        public ActorId Id { get; }
        public CombatPhase Phase { get; }
        public ActorId Target { get; }
        public bool HoldsToken { get; }
        public bool AtCoordinator { get; }
        public float DistanceMetres { get; }
    }

    /// <summary>An NPC keeping the hero in combat, with the reasons and what it does.</summary>
    public sealed class Threat
    {
        public Threat(ActorId id) { Id = id; }
        public ActorId Id { get; }
        public ThreatReason Reasons { get; internal set; }
        public CombatPhase Phase { get; internal set; }
        public ActorId Target { get; internal set; }
        public float DistanceMetres { get; internal set; } = float.NaN;
        public bool HoldsToken { get; internal set; }
    }

    /// <summary>
    /// fix-151 (owner: "highlight the enemies the fight is about, to find the ones behind textures"): which NPCs keep the
    /// hero in combat, from the same rules the game uses: the save / use block (<see cref="NpcCombatSystem.IsPlayerInCombat"/>),
    /// fight music (<see cref="MusicSituationRule"/>: a fighter, not fleeing, targeting the hero or a party member within
    /// 30 m) and the party's own fights (a companion runs to its target). Pure; the console op is ScenarioRunner.Fix151.
    /// </summary>
    public static class CombatThreats
    {
        /// <param name="fighters">Every combat brain (living NPCs; the Unity side leaves out dead ones).</param>
        /// <param name="isParty">True for the hero's party members.</param>
        /// <param name="distanceOf">Distance of an NPC from the hero (for a companion's target without a brain row), m.</param>
        /// <param name="ignore">Actors to leave out (dead, despawned).</param>
        public static List<Threat> Collect(IEnumerable<ThreatFighter> fighters, Func<ActorId, bool> isParty,
                                           Func<ActorId, float> distanceOf = null, Func<ActorId, bool> ignore = null)
        {
            isParty = isParty ?? (_ => false);
            var rows = (fighters ?? Enumerable.Empty<ThreatFighter>()).ToList();
            var byId = new Dictionary<ActorId, Threat>();
            Threat Of(ActorId id)
            {
                if (!byId.TryGetValue(id, out var t)) byId[id] = t = new Threat(id);
                return t;
            }

            foreach (var f in rows)
            {
                if (f.Id.IsEmpty || f.Id.IsPlayer || (ignore != null && ignore(f.Id))) continue;
                if (f.Phase == CombatPhase.Idle || f.Target.IsEmpty) continue;
                if (isParty(f.Id))
                {
                    // a companion fighting someone: its target (unless that is the hero or another member: a duel, a brawl)
                    if (f.Phase == CombatPhase.Flee || f.Target.IsPlayer || isParty(f.Target) || (ignore != null && ignore(f.Target))) continue;
                    Of(f.Target).Reasons |= ThreatReason.Companion;
                    continue;
                }
                var why = ThreatReason.None;
                if (NpcCombatSystem.InBattleWithPlayer(f.Phase, f.Target, f.HoldsToken, f.AtCoordinator)) why |= ThreatReason.Battle;
                bool fights = f.Phase != CombatPhase.Flee;
                if (fights && f.Target.IsPlayer) why |= ThreatReason.Hero;
                if (fights && !f.Target.IsPlayer && isParty(f.Target)) why |= ThreatReason.Party;
                if (fights && (f.Target.IsPlayer || isParty(f.Target)) && f.DistanceMetres <= MusicSituationRule.RadiusMetres) why |= ThreatReason.Music;
                if (why == ThreatReason.None) continue;
                Of(f.Id).Reasons |= why;
            }
            foreach (var f in rows)
                if (byId.TryGetValue(f.Id, out var t))
                {
                    t.Phase = f.Phase;
                    t.Target = f.Target;
                    t.DistanceMetres = f.DistanceMetres;
                    t.HoldsToken = f.HoldsToken;
                }
            foreach (var t in byId.Values)
                if (float.IsNaN(t.DistanceMetres) && distanceOf != null) t.DistanceMetres = distanceOf(t.Id);
            return byId.Values.OrderBy(t => Rank(t.Reasons)).ThenBy(t => float.IsNaN(t.DistanceMetres) ? float.MaxValue : t.DistanceMetres)
                              .ThenBy(t => t.Id.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>Sort key: the save block first, then the music, then the rest.</summary>
        static int Rank(ThreatReason r) =>
            (r & ThreatReason.Battle) != 0 ? 0 : (r & ThreatReason.Music) != 0 ? 1 : (r & (ThreatReason.Hero | ThreatReason.Party)) != 0 ? 2 : 3;

        /// <summary>"battle,music,hero" (lower case, in flag order; "-" for none).</summary>
        public static string ReasonText(ThreatReason r)
        {
            if (r == ThreatReason.None) return "-";
            var parts = new List<string>();
            foreach (ThreatReason v in Enum.GetValues(typeof(ThreatReason)))
                if (v != ThreatReason.None && (r & v) != 0) parts.Add(v.ToString().ToLowerInvariant());
            return string.Join(",", parts);
        }

        static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        /// <summary>8-point compass direction of the offset (dx, dz) from the hero: north = world +Z (the HUD compass), east = +X.</summary>
        public static string Compass(float dx, float dz)
        {
            if (Math.Abs(dx) < 1e-4f && Math.Abs(dz) < 1e-4f) return "-";
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI; // clockwise from north
            if (deg < 0) deg += 360.0;
            return Points[(int)Math.Floor((deg + 22.5) / 45.0) % 8];
        }

        /// <summary>What the console's <c>enemies</c> argument asks for.</summary>
        public enum Mode { List, On, Off, Fix, Kill }

        /// <summary>
        /// <c>enemies</c> (list + markers on), <c>on</c>, <c>off</c>, <c>list</c> (markers unchanged), <c>fix</c>, <c>kill</c>.
        /// False for anything else.
        /// </summary>
        public static bool TryParseMode(string arg, out Mode mode)
        {
            switch ((arg ?? "").Trim().ToLowerInvariant())
            {
                case "": case "on": case "1": case "show": mode = Mode.On; return true;
                case "off": case "0": case "hide": mode = Mode.Off; return true;
                case "list": case "log": mode = Mode.List; return true;
                case "fix": case "unstuck": case "rescue": mode = Mode.Fix; return true;
                case "kill": mode = Mode.Kill; return true;
                default: mode = Mode.List; return false;
            }
        }

        /// <summary>Where the NPC stands, as the cave / placement checks judged it (Unity side fills it in).</summary>
        public struct Place
        {
            /// <summary>The static collider its capsule is in (null: none).</summary>
            public string Inside;
            /// <summary>The cave judge's verdict (CaveFloorRules.Stand name; "Open" outside caves).</summary>
            public string Cave;
            /// <summary>The cave judge lets a body stand there.</summary>
            public bool CaveOk;
            /// <summary>NavMesh path from it to the hero: complete | partial | none | offmesh.</summary>
            public string Path;
            /// <summary>Distance from its feet to the nearest NavMesh point, m (NaN: none within reach).</summary>
            public float OffMesh;
        }

        /// <summary>The place problems, "" when none: "in:Rock_02", "cave:RoofTop", "nopath", "offmesh:3.2".</summary>
        public static string Flags(in Place p)
        {
            var f = new List<string>();
            if (!string.IsNullOrEmpty(p.Inside)) f.Add("in:" + p.Inside);
            if (!p.CaveOk) f.Add("cave:" + (p.Cave ?? "?"));
            if (p.Path == "none") f.Add("nopath");
            if (p.Path == "offmesh") f.Add(float.IsNaN(p.OffMesh) ? "offmesh" : "offmesh:" + p.OffMesh.ToString("0.0", CultureInfo.InvariantCulture));
            return string.Join(" ", f);
        }

        /// <summary>The NPC needs <c>enemies fix</c>: inside a collider, on a cave roof / in the rock, or off the NavMesh / no path.</summary>
        public static bool Stuck(in Place p) => !string.IsNullOrEmpty(p.Inside) || !p.CaveOk || p.Path == "none" || p.Path == "offmesh";

        /// <summary>One list line: <c>Wolf#8c25 "Wolf" 12.3m NE (x,y,z) battle,music Fight->hero [in:Rock_02]</c>.</summary>
        public static string Line(Threat t, string displayName, float dx, float dz, float x, float y, float z, in Place place)
        {
            var inv = CultureInfo.InvariantCulture;
            string flags = Flags(place);
            string name = string.IsNullOrEmpty(displayName) || displayName == t.Id.Name ? "" : $" \"{displayName}\"";
            string dist = float.IsNaN(t.DistanceMetres) ? "?" : t.DistanceMetres.ToString("0.0", inv);
            string target = t.Target.IsEmpty ? "" : $" {t.Phase}->{(t.Target.IsPlayer ? "hero" : t.Target.Name)}{(t.HoldsToken ? " token" : "")}";
            return $"{t.Id}{name} {dist}m {Compass(dx, dz)} ({x.ToString("0.0", inv)},{y.ToString("0.0", inv)},{z.ToString("0.0", inv)}) " +
                   $"{ReasonText(t.Reasons)}{target} path:{place.Path ?? "?"}{(flags.Length > 0 ? " [" + flags + "]" : "")}";
        }
    }
}
#endif
