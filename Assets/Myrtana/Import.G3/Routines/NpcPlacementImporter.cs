using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Items;
using Myrtana.Core;
using Myrtana.Simulation;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// What the simulation needs to place and run a G3 NPC (Docs/npc-simulation.md): species, visual key, the editor
    /// pose and the three routine points from gCNavigation_PS. Collects the routine points the NPCs use — plus every other
    /// point of their anchors — as <see cref="RoutinePointDef"/>s with their user slots in world space, and the anchors.
    /// Roam and patrol anchors are grouped by name (G3 finds roaming points by the anchor's name); local anchors by entity.
    /// </summary>
    public sealed class NpcPlacementImporter
    {
        /// <summary>Points farther than this (metres, horizontal) from the spawn are not used: 98 % of the NPCs move less
        /// than 20 m; the few long ones are story NPCs whose routines quests switch (formats-animation.md §8.6).</summary>
        public const float MaxPointDistance = 50f;
        const float Cm = 0.01f;

        readonly RoutinePoints _world;
        readonly ImportReport _report;
        readonly Dictionary<Guid, RoutinePointDef> _points = new Dictionary<Guid, RoutinePointDef>();
        readonly Dictionary<string, AnchorDef> _anchors = new Dictionary<string, AnchorDef>(StringComparer.Ordinal);
        Dictionary<string, List<Guid>> _membersByAnchor;

        public NpcPlacementImporter(RoutinePoints world, ImportReport report)
        {
            _world = world;
            _report = report;
        }

        public IEnumerable<RoutinePointDef> Points => _points.Values.OrderBy(p => p.Id.Name, StringComparer.Ordinal);
        public IEnumerable<AnchorDef> Anchors => _anchors.Values.OrderBy(a => a.Id.Name, StringComparer.Ordinal);

        /// <summary>
        /// G3 SendPerceptionPreach sends listeners to the free Listener points (use type 97) near the preacher; nobody's
        /// routine names them, so they are added here: every Listener point within <see cref="Preaching.Range"/>
        /// of an imported Preach point (call after all NPCs are defined). Returns the points added.
        /// </summary>
        public int AddListenerPoints()
        {
            var preach = _points.Values.Where(p => p.Interaction == UseType.Preach.ToString() && p.Slots.Count > 0).Select(p => p.Slots[0]).ToList();
            if (preach.Count == 0) return 0;
            int added = 0;
            foreach (var kv in _world.Points)
            {
                if (kv.Value.UseType != (int)UseType.Listener || _points.ContainsKey(kv.Key)) continue;
                float x = kv.Value.World[12] * Cm, y = kv.Value.World[13] * Cm, z = kv.Value.World[14] * Cm;
                if (!preach.Any(p => new WorldPose(x, y, z, 0f).DistanceTo(p) <= Preaching.Range)) continue;
                Add(kv.Key, kv.Value);
                _report.Count("Listener point near a Preach point (sermon listeners)", kv.Value.Name);
                added++;
            }
            return added;
        }

        /// <summary>Id of a spawn whose name is shared by other spawns ("Orc_Warrior_01#3f2a91c0"): stable across imports.</summary>
        public static ActorId SharedNameId(NpcSpawn spawn) => new ActorId($"{spawn.Name}#{spawn.Guid.ToString("N").Substring(0, 8)}");

        public NpcDef Define(NpcSpawn spawn, ActorId id, TextId displayName)
        {
            var pose = SpawnPose(spawn.World);
            var routine = new NpcRoutineDef(Use(spawn.WorkingPoint, pose), Use(spawn.RelaxingPoint, pose), Use(spawn.SleepingPoint, pose));
            return new NpcDef(id, displayName, spawn.Voice, G3RoutineTables.SpeciesName(spawn.Species), spawn.VisualKey, pose,
                              routine.IsEmpty ? null : routine, routines: NamedRoutines(spawn));
        }

        /// <summary>
        /// The NPC's further routines (story SetRoutine); the entry of its own routine (usually "Start") repeats the start
        /// points and is skipped. Their points may lie anywhere in the world (an ambush, another town), so the distance
        /// limit of the start routine does not apply.
        /// </summary>
        Dictionary<string, NpcRoutineDef> NamedRoutines(NpcSpawn spawn)
        {
            Dictionary<string, NpcRoutineDef> routines = null;
            foreach (var named in spawn.Routines)
            {
                if (named.Name == spawn.Routine) continue; // the NPC's own routine (the start points above)
                var routine = new NpcRoutineDef(Use(named.Working, null), Use(named.Relaxing, null), Use(named.Sleeping, null));
                if (routine.IsEmpty)
                {
                    _report.Count("named routine without points in the world (SetRoutine to it keeps the own routine)", $"{spawn.Name}: {named.Name}");
                    continue;
                }
                routines ??= new Dictionary<string, NpcRoutineDef>(StringComparer.Ordinal);
                routines[named.Name] = routine;
            }
            return routines;
        }

        /// <summary>Registers a referenced point (and its anchor's other points); empty when missing or too far away.</summary>
        /// <param name="spawn">The NPC's spawn for the distance limit; null = no limit.</param>
        Id<RoutinePointDef> Use(Guid guid, WorldPose? spawn)
        {
            if (guid == Guid.Empty) return default;
            if (!_world.Points.TryGetValue(guid, out var point))
            {
                _report.Count("routine point missing in the world (NPC keeps no point for that phase)");
                return default;
            }
            float dx = point.World[12] * Cm - (spawn?.X ?? 0), dz = point.World[14] * Cm - (spawn?.Z ?? 0);
            if (spawn.HasValue && dx * dx + dz * dz > MaxPointDistance * MaxPointDistance)
            {
                _report.Count($"routine point farther than {MaxPointDistance} m from the NPC (not used)", point.Name);
                return default;
            }
            var def = Add(guid, point);
            if (!def.Anchor.IsEmpty)
                foreach (var member in Members(def.Anchor.Name))
                    Add(member, _world.Points[member]);
            return def.Id;
        }

        RoutinePointDef Add(Guid guid, RoutinePoints.Point point)
        {
            if (_points.TryGetValue(guid, out var existing)) return existing;
            var slots = Enumerable.Range(0, point.SlotCount).Select(i => ToPose(RoutinePoints.UserPoseOf(point, i))).ToList();
            var directions = Enumerable.Range(0, point.SlotCount).Select(i => Direction(point, i)).ToList();
            var def = new RoutinePointDef(new Id<RoutinePointDef>(guid.ToString("D")), point.Name, ((UseType)point.UseType).ToString(),
                                          slots, AnchorOf(point), slotDirections: directions.All(d => d == "Fwd") ? null : directions);
            _points[guid] = def;
            return def;
        }

        Id<AnchorDef> AnchorOf(RoutinePoints.Point point)
        {
            if (point.Anchor == Guid.Empty || !_world.Anchors.TryGetValue(point.Anchor, out var anchor)) return default;
            string key = AnchorKey(point.Anchor, anchor);
            if (_anchors.TryGetValue(key, out var def))
            {
                if (anchor.MaxUsers > def.MaxUsers) _anchors[key] = new AnchorDef(def.Id, def.Kind, anchor.MaxUsers);
            }
            else _anchors[key] = new AnchorDef(new Id<AnchorDef>(key), (AnchorKind)(int)anchor.Type, anchor.MaxUsers);
            return new Id<AnchorDef>(key);
        }

        /// <summary>Roam and patrol anchors are named sets; a local anchor is one entity (a smithy).</summary>
        static string AnchorKey(Guid guid, RoutinePoints.Anchor anchor) =>
            anchor.Type == RoutinePoints.AnchorType.Roam || anchor.Type == RoutinePoints.AnchorType.Patrol
                ? anchor.Name
                : $"{anchor.Name}#{guid.ToString("N").Substring(0, 8)}";

        IEnumerable<Guid> Members(string anchorKey)
        {
            if (_membersByAnchor == null)
            {
                _membersByAnchor = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
                foreach (var kv in _world.Points)
                {
                    if (kv.Value.Anchor == Guid.Empty || !_world.Anchors.TryGetValue(kv.Value.Anchor, out var anchor)) continue;
                    string key = AnchorKey(kv.Value.Anchor, anchor);
                    if (!_membersByAnchor.TryGetValue(key, out var list)) _membersByAnchor[key] = list = new List<Guid>();
                    list.Add(kv.Key);
                }
            }
            return _membersByAnchor.TryGetValue(anchorKey, out var members) ? members : Enumerable.Empty<Guid>();
        }

        /// <summary>gEDirection of a slot as a motion-name direction (0 and unknown values = forward).</summary>
        static string Direction(RoutinePoints.Point point, int slot)
        {
            int d = slot < point.AniDirections.Count ? point.AniDirections[slot] : 0;
            return d switch { 2 => "Back", 3 => "Left", 4 => "Right", _ => "Fwd" };
        }

        /// <summary>Position and heading of a G3 world matrix (row-major, cm; the entity faces its +Z row).</summary>
        static WorldPose SpawnPose(float[] m) =>
            new WorldPose(m[12] * Cm, m[13] * Cm, m[14] * Cm, (float)(Math.Atan2(m[8], m[10]) * 180 / Math.PI));

        static WorldPose ToPose(RoutinePoints.UserPose p) =>
            new WorldPose(p.X * Cm, p.Y * Cm, p.Z * Cm, (float)(Math.Atan2(p.ForwardX, p.ForwardZ) * 180 / Math.PI));
    }
}
