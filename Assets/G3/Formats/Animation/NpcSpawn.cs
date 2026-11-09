using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Genome;
using G3.Formats.Items;
using G3.Formats.World;

namespace G3.Formats.Animation
{
    /// <summary>One visible part of an NPC: a skinned actor (.FXA = .xact) or a static mesh (hair, beard, .xcmsh).</summary>
    public sealed class NpcPart
    {
        public string Resource;        // e.g. "G3_Hero_Body_Mercenary.FXA", "G3_Animation_Head_Beard_Myrtana_03_01.xcmsh"
        public int Switch;             // MaterialSwitch → texture variant _S<n> (0 = as authored)
        public bool IsMesh;            // static mesh attached to a Slot_* node of the head
        public string EntityName;
        /// <summary>eCVisualAnimation_PS.FacialAnimFilePath of a head: the same head with face bones for lip sync
        /// (<c>G3_Head_Hero_Myrtana_Animated_01.FXA</c>, played by <c>&lt;stem&gt;_LipSync.xmot</c>); null when none.</summary>
        public string FacialResource;

        public string Stem => Path.GetFileNameWithoutExtension(Resource);
        public bool IsBody => !IsMesh && Stem.IndexOf("_Body_", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool IsHead => !IsMesh && Stem.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 && !IsBody;

        /// <summary>Slot node a static part hangs on: Slot_Beard / Slot_Hair / Slot_Helmet (head actor nodes).</summary>
        public string Slot =>
            Stem.IndexOf("_Beard_", StringComparison.OrdinalIgnoreCase) >= 0 ? "Slot_Beard" :
            Stem.IndexOf("_Helmet", StringComparison.OrdinalIgnoreCase) >= 0 ? "Slot_Helmet" : "Slot_Hair";
    }

    /// <summary>
    /// An NPC or monster placed in a .lrentdat (Docs/formats-animation.md §8): the entity carries gCNPC_PS and
    /// eCVisualAnimation_PS inline; humans/orcs use a skeleton-only actor and get their visible parts from child
    /// entities (Body_*/Head_* items with their own eCVisualAnimation_PS, hair/beard eCVisualMeshDynamic_PS).
    /// </summary>
    public sealed class NpcSpawn
    {
        public string Name, Sector;
        public Guid Guid, Creator;
        public bool Enabled;
        /// <summary>Row-major world matrix, cm (Docs/formats-world.md §6).</summary>
        public float[] World;
        public string Actor;            // eCVisualAnimation_PS.ResourceFilePath of the NPC entity
        public int Switch;
        public readonly List<NpcPart> Parts = new List<NpcPart>();
        public int Species = -1, AniState = -1, Action = -1, AmbientAction = -1;
        public string Routine, Voice;
        /// <summary>gCScriptRoutine_PS.Routine: the AI routine script the NPC starts with ("Rtn_Dead" = a corpse from the start,
        /// G3 ZS_Dead: Ramirez in his pit near the Mora Sul temple); null when the entity has none.</summary>
        public string ScriptRoutine;
        /// <summary>gCNavigation_PS routine points (entity GUIDs of interaction objects / free points; Empty = none).</summary>
        public Guid WorkingPoint, RelaxingPoint, SleepingPoint;
        /// <summary>
        /// gCNavigation_PS further routines: RoutineNames with the parallel WorkingPoints / RelaxingPoints / SleepingPoints
        /// arrays (Empty = none); the story switches to one by name (info command SetRoutine).
        /// </summary>
        public readonly List<NamedRoutine> Routines = new List<NamedRoutine>();
        /// <summary>The entity's own matrix as stored by the level editor when <see cref="World"/> was moved to a routine
        /// point by <see cref="RoutinePoints.PlaceAtRoutineStart"/> (null = not moved).</summary>
        public float[] EditorWorld;
        /// <summary>Name of the routine point the NPC was placed at (null = editor position).</summary>
        public string StartPoint;
        /// <summary>gCNPC_PS combat and attitude facts: Level (offset above the player's level), LevelMax, gENPCType,
        /// gEClass, gEPoliticalAlignment, AttitudeToPlayer2 (gEAttitude) and whether an enclave is set.</summary>
        public int Level, LevelMax, NpcType, Class, Political, AttitudeToPlayer;
        public bool HasEnclave;
        /// <summary>gCNPC_PS.Enclave: the GUID of the enclave entity (gCEnclave_PS) the NPC belongs to; empty = none.</summary>
        public Guid EnclaveId;
        /// <summary>The CP 1.75 "Immortal" property (only present when the data sets it).</summary>
        public bool Immortal;
        /// <summary>gCNPC_PS.GuardPoint: the entity a gate guard keeps the player away from (empty = not a gate guard).</summary>
        public Guid GuardPoint;
        /// <summary>gCNPC_PS.GuardStatus (gEGuardStatus: 0 Active, 1 FirstWarnGiven, 2 SecondWarnGiven, 3 Inactive, 4 Behind).</summary>
        public int GuardStatus;
        /// <summary>gCInventory_PS: treasure sets (weaponry, plunder …) and equipped stacks; null when the entity has none.</summary>
        public Inventory Inventory;

        public float X => World[12];
        public float Y => World[13];
        public float Z => World[14];

        public string ActorStem => Path.GetFileNameWithoutExtension(Actor);

        /// <summary>Visual identity: actor + parts with their switches (the prefab cache key).</summary>
        public string VisualKey
        {
            get
            {
                var parts = Parts.Select(p => Short(p.Stem) + (p.Switch != 0 ? "_S" + p.Switch : "")).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
                string head = Short(ActorStem) + (Switch != 0 ? "_S" + Switch : "");
                return Parts.Count == 0 ? head : head + "__" + string.Join("__", parts);
            }
        }

        static string Short(string stem)
        {
            string s = stem;
            if (s.StartsWith("G3_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(3);
            if (s.StartsWith("Animation_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(10);
            return s;
        }

        /// <summary>
        /// Motion-name rig (Docs/formats-animation.md §5, §8.4): the actor family of the NPC's own actor —
        /// G3_Hero_Skeleton → Hero (also for fat/slave bodies: the Fat_/Slave_ motion sets animate the separate
        /// G3_Fat_Skeleton / G3_Slave_Skeleton rigs, whose bones are named Fat_*/Slave_*), G3_Wolf_Body_01 → Wolf.
        /// </summary>
        public List<string> RigCandidates()
        {
            var m = Regex.Match(ActorStem, @"^G3_([A-Za-z]+)_");
            var rigs = new List<string> { m.Success ? m.Groups[1].Value : ActorStem };
            if (!rigs.Contains("Hero") && Parts.Count > 0) rigs.Add("Hero");
            return rigs;
        }

        /// <summary>
        /// Idle loop for the NPC: <c>&lt;Rig&gt;_Stand_None_None_P0_Ambient_Loop_N_Fwd_00…</c> (flying birds: Fly_Loop),
        /// then any Stand ambient loop, then any Stand loop. <paramref name="motions"/> = all .xmot stems.
        /// </summary>
        public string PickIdle(IReadOnlyCollection<string> motions)
        {
            bool flying = Name != null && (Name.IndexOf("Sky", StringComparison.OrdinalIgnoreCase) >= 0 || Name.IndexOf("Flock", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var rig in RigCandidates())
            {
                string Find(string prefix) => motions.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                var hit = (flying ? Find($"{rig}_Stand_None_None_P0_Fly_Loop_") : null)
                          ?? Find($"{rig}_Stand_None_None_P0_Ambient_Loop_N_Fwd_00_")
                          ?? Find($"{rig}_Stand_None_None_P0_Ambient_Loop_")
                          ?? Find($"{rig}_Stand_None_Fist_P0_Ambient_Loop_")
                          ?? motions.Where(x => x.StartsWith(rig + "_Stand_", StringComparison.OrdinalIgnoreCase) && x.IndexOf("_Loop_", StringComparison.OrdinalIgnoreCase) > 0)
                              .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }
    }

    public static class NpcSpawns
    {
        /// <summary>
        /// NPC/monster spawns of one .lrentdat (read with keepRaw so the PS are available, and with parents).
        /// Every entity with gCNPC_PS and eCVisualAnimation_PS is one spawn; its direct children with
        /// eCVisualAnimation_PS / eCVisualMesh*_PS are its parts.
        /// </summary>
        public static List<NpcSpawn> FromArchive(List<WorldEntity> list, string sector, bool sectorEnabled = true)
        {
            var result = new List<NpcSpawn>();
            if (list == null) return result;
            var children = new Dictionary<int, List<int>>();
            for (int i = 0; i < list.Count; i++)
            {
                int p = list[i].Parent;
                if (p < 0) continue;
                if (!children.TryGetValue(p, out var l)) children[p] = l = new List<int>();
                l.Add(i);
            }
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var sets = e.RawPropertySets;
                if (sets == null) continue;
                PropertyObject npc = null, va = null, sr = null, nav = null, inv = null;
                long invPost = -1;
                for (int k = 0; k < sets.Count; k++)
                {
                    var ps = sets[k];
                    switch (ps.ClassName)
                    {
                        case "gCNPC_PS": npc = ps; break;
                        case "eCVisualAnimation_PS": va = ps; break;
                        case "gCScriptRoutine_PS": sr = ps; break;
                        case "gCNavigation_PS": nav = ps; break;
                        case "gCInventory_PS": inv = ps; invPost = e.RawPostDataStart != null && k < e.RawPostDataStart.Count ? e.RawPostDataStart[k] : -1; break;
                    }
                }
                if (npc == null || va == null) continue;
                var g = e.Source;
                var s = new NpcSpawn
                {
                    Name = e.Name, Sector = sector, Guid = e.Guid, Creator = e.Creator, Enabled = e.Enabled && e.RenderingEnabled,
                    World = (float[])e.World.Clone(),
                    Actor = g.StrFrom(va.Get("ResourceFilePath")?.Value),
                    Switch = va.Get("MaterialSwitch")?.AsInt() ?? 0,
                    Species = npc.Get("Species")?.AsEnum() ?? -1,
                    Voice = g.StrFrom(npc.Get("Voice")?.Value),
                    AniState = sr?.Get("AniState")?.AsEnum() ?? -1,
                    Action = sr?.Get("Action")?.AsEnum() ?? -1,
                    AmbientAction = sr?.Get("AmbientAction")?.AsEnum() ?? -1,
                    Routine = nav != null ? g.StrFrom(nav.Get("Routine")?.Value) : null,
                    ScriptRoutine = sr != null ? g.StrFrom(sr.Get("Routine")?.Value) : null,
                    WorkingPoint = PointId(nav, "WorkingPoint"),
                    RelaxingPoint = PointId(nav, "RelaxingPoint"),
                    SleepingPoint = PointId(nav, "SleepingPoint"),
                    Level = Props.Int(npc, "Level"),
                    LevelMax = Props.Int(npc, "LevelMax"),
                    NpcType = Props.Enum(npc, "Type"),
                    Class = Props.Enum(npc, "Class"),
                    Political = Props.Enum(npc, "PoliticalAlignment"),
                    AttitudeToPlayer = Props.Enum(npc, "AttitudeToPlayer2"),
                    HasEnclave = PointId(npc, "Enclave") != Guid.Empty,
                    EnclaveId = PointId(npc, "Enclave"),
                    Immortal = Props.Bool(npc, "Immortal"),
                    GuardPoint = Props.Proxy(npc, "GuardPoint"),
                    GuardStatus = Props.Enum(npc, "GuardStatus"),
                    Inventory = inv != null && invPost >= 0 ? Inventory.Read(g, inv, invPost) : null,
                };
                if (string.IsNullOrEmpty(s.Actor)) continue;
                ReadNamedRoutines(g, nav, s.Routines);
                if (children.TryGetValue(i, out var kids))
                    foreach (int k in kids)
                    {
                        var c = list[k];
                        if ((!c.Enabled && sectorEnabled) || c.RawPropertySets == null) continue; // a switched-off sector's parts are off too
                        var cva = c.RawPropertySets.FirstOrDefault(p => p.ClassName == "eCVisualAnimation_PS");
                        if (cva != null)
                        {
                            var res = c.Source.StrFrom(cva.Get("ResourceFilePath")?.Value);
                            if (!string.IsNullOrEmpty(res))
                            {
                                string facial = c.Source.StrFrom(cva.Get("FacialAnimFilePath")?.Value);
                                s.Parts.Add(new NpcPart { Resource = res, Switch = cva.Get("MaterialSwitch")?.AsInt() ?? 0, EntityName = c.Name,
                                                          FacialResource = string.IsNullOrEmpty(facial) ? null : facial });
                            }
                        }
                        else if (c.Visual == VisualKind.Mesh && !string.IsNullOrEmpty(c.Resource) &&
                                 c.Resource.IndexOf("_Head_", StringComparison.OrdinalIgnoreCase) >= 0)
                            // Hair / beard (G3_Animation_Head_Hair_*, _Beard_*). Food/bowls held in hands are skipped.
                            s.Parts.Add(new NpcPart { Resource = c.Resource, Switch = c.MaterialSwitch, IsMesh = true, EntityName = c.Name });
                    }
                result.Add(s);
            }
            return result;
        }

        static void ReadNamedRoutines(GenomeReader g, PropertyObject nav, List<NamedRoutine> into)
        {
            var names = StringArray(g, nav?.Get("RoutineNames")?.Value);
            if (names.Count == 0) return;
            var working = PointIds(nav.Get("WorkingPoints")?.Value);
            var relaxing = PointIds(nav.Get("RelaxingPoints")?.Value);
            var sleeping = PointIds(nav.Get("SleepingPoints")?.Value);
            for (int i = 0; i < names.Count; i++)
                if (!string.IsNullOrEmpty(names[i]))
                    into.Add(new NamedRoutine(names[i], At(working, i), At(relaxing, i), At(sleeping, i)));
        }

        static Guid At(List<Guid> list, int i) => i < list.Count ? list[i] : Guid.Empty;

        /// <summary>bTValArray&lt;bCPropertyID&gt;: u8 version, u32 count, count × (16-byte GUID + u32).</summary>
        static List<Guid> PointIds(byte[] v)
        {
            var list = new List<Guid>();
            if (v == null || v.Length < 5) return list;
            int n = BitConverter.ToInt32(v, 1);
            for (int i = 0; i < n && 5 + (i + 1) * 20 <= v.Length; i++)
            {
                var b = new byte[16];
                Array.Copy(v, 5 + i * 20, b, 0, 16);
                list.Add(new Guid(b));
            }
            return list;
        }

        /// <summary>bTObjArray&lt;bCString&gt;: u8 version, u32 count, count × string (u16 index into the file's string table).</summary>
        static List<string> StringArray(GenomeReader g, byte[] v)
        {
            var list = new List<string>();
            if (v == null || v.Length < 5) return list;
            int n = BitConverter.ToInt32(v, 1);
            for (int i = 0; i < n && 5 + (i + 1) * 2 <= v.Length; i++) list.Add(g.StrFrom(new[] { v[5 + i * 2], v[6 + i * 2] }));
            return list;
        }

        /// <summary>bCPropertyID (16-byte GUID + u32): a gCNavigation_PS point property or the gCNPC_PS enclave.</summary>
        static Guid PointId(PropertyObject nav, string name)
        {
            var v = nav?.Get(name)?.Value;
            if (v == null || v.Length < 16) return Guid.Empty;
            var b = new byte[16];
            Array.Copy(v, b, 16);
            return new Guid(b);
        }

        /// <summary>Developer test sectors that are enabled in .wrldatasc but are not part of the game (e.g. Mario_Ghost_Test).</summary>
        public static bool IsTestSector(string sector) => sector != null && sector.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>All spawns of the enabled sectors' .lrentdat files. read(path) returns the bytes of a Projects_compiled path.</summary>
        public static List<NpcSpawn> LoadWorld(WorldLayout layout, Func<string, byte[]> read, List<string> errors = null)
        {
            var all = new List<NpcSpawn>();
            foreach (var sector in layout.Sectors)
            foreach (var path in sector.Lrentdats)
            {
                try
                {
                    var bytes = read(path);
                    var list = bytes != null ? EntityArchive.Read(bytes, decodeVegetation: false, keepRaw: true) : null;
                    all.AddRange(FromArchive(list, sector.Name, sector.Enabled));
                }
                catch (Exception ex) { errors?.Add($"{path}: {ex.Message}"); }
            }
            return all;
        }
    }

    /// <summary>A named routine of an NPC (gCNavigation_PS RoutineNames entry) and its three points.</summary>
    public readonly struct NamedRoutine
    {
        public NamedRoutine(string name, Guid working, Guid relaxing, Guid sleeping)
        {
            Name = name;
            Working = working;
            Relaxing = relaxing;
            Sleeping = sleeping;
        }

        public string Name { get; }
        public Guid Working { get; }
        public Guid Relaxing { get; }
        public Guid Sleeping { get; }
    }

    /// <summary>
    /// Routine points of the world (Docs/formats-animation.md §8.6): every entity with gCInteraction_PS — free points
    /// (gCAIHelper_FreePoint_PS: Guard, Smalltalk, Sit_Ground …) and interaction objects (anvil, bench, barrel …) whose
    /// gCNavOffset_PS.OffsetPose lists where a user stands. NPCs reference them by GUID from gCNavigation_PS.
    /// </summary>
    public sealed class RoutinePoints
    {
        public sealed class Point
        {
            public string Name;
            public float[] World;                        // row-major, cm
            /// <summary>gCNavOffset_PS.OffsetPose: user position (cm, object space) + rotation (x, y, z, w).</summary>
            public readonly List<float[]> Offsets = new List<float[]>();
            /// <summary>gCNavOffset_PS.AniDirection per OffsetPose (gEDirection: 0 forward, 2 back, 3 left, 4 right) — e.g. the
            /// side a bed is entered from.</summary>
            public readonly List<int> AniDirections = new List<int>();
            /// <summary>gCInteraction_PS.UseType (gEUseType, see <see cref="G3.Formats.Items.UseType"/>): Anvil, Bed, Guard …</summary>
            public int UseType;
            /// <summary>gCInteraction_PS.AnchorPoint: the anchor grouping this point with others (Empty = none).</summary>
            public Guid Anchor;
            /// <summary>gCInteraction_PS.Owner: only this NPC may use the point (Empty = anyone).</summary>
            public Guid Owner;

            /// <summary>Number of NPCs that can use the point at once (one per OffsetPose, at least one).</summary>
            public int SlotCount => Math.Max(1, Offsets.Count);
        }

        /// <summary>gEAnchorType: how the points of an anchor are used (gameplay-logic.md §5.5).</summary>
        public enum AnchorType { Local = 0, Roam = 1, Patrol = 2, Event = 3 }

        /// <summary>An entity with gCAnchor_PS: groups interaction points (a smithy, a roam area, a patrol path).</summary>
        public sealed class Anchor
        {
            public string Name;
            public float[] World;                        // row-major, cm
            public AnchorType Type;
            /// <summary>gCAnchor_PS.MaxUsers (0 = as many as the anchor has points).</summary>
            public int MaxUsers;
        }

        /// <summary>Where a user of a point stands: position (cm) and horizontal facing (unit x/z).</summary>
        public readonly struct UserPose
        {
            public UserPose(float x, float y, float z, float forwardX, float forwardZ)
            {
                X = x; Y = y; Z = z; ForwardX = forwardX; ForwardZ = forwardZ;
            }

            public float X { get; }
            public float Y { get; }
            public float Z { get; }
            public float ForwardX { get; }
            public float ForwardZ { get; }
        }

        public readonly Dictionary<Guid, Point> Points = new Dictionary<Guid, Point>();
        public readonly Dictionary<Guid, Anchor> Anchors = new Dictionary<Guid, Anchor>();
        /// <summary>Named AI helpers (entities with gCAIHelper_Label_PS, e.g. the teleport targets <c>Teleport_Faring</c>):
        /// name → world matrix (row-major, cm). The first entity of a name wins.</summary>
        public readonly Dictionary<string, float[]> Labels = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every named entity of the enabled sectors (free points, AI helpers, zones, objects …): name → world matrix
        /// (row-major, cm). The first entity of a name wins, as G3's eCSceneAdmin::GetEntityByName returns one entity.</summary>
        public readonly Dictionary<string, float[]> Named = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Points farther than this (cm, horizontal) from the editor position are ignored: 98 % of the moves are
        /// below 20 m; the few long ones (300 m and more) belong to story NPCs whose routines are switched by quests.</summary>
        public static float MaxMove = 5000f;

        /// <summary>World matrices of the entities asked for by GUID in <see cref="LoadWorld"/> (e.g. gate guards' guard points).</summary>
        public readonly Dictionary<Guid, float[]> Tracked = new Dictionary<Guid, float[]>();

        /// <summary>Scans the enabled sectors' .node and .lrentdat files (~4 s for the whole world).</summary>
        /// <param name="track">Entities (any kind) whose matrices to keep in <see cref="Tracked"/>.</param>
        public static RoutinePoints LoadWorld(WorldLayout layout, Func<string, byte[]> read, List<string> errors = null, ISet<Guid> track = null)
        {
            var rp = new RoutinePoints();
            foreach (var sector in layout.Sectors)
            foreach (var path in sector.Nodes.Concat(sector.Lrentdats))
            {
                List<WorldEntity> list;
                try
                {
                    var bytes = read(path);
                    list = bytes != null ? EntityArchive.Read(bytes, decodeVegetation: false, keepRaw: true) : null;
                }
                catch (Exception ex) { errors?.Add($"{path}: {ex.Message}"); continue; }
                if (list == null) continue;
                foreach (var e in list)
                {
                    if (!string.IsNullOrEmpty(e.Name) && e.World != null && !rp.Named.ContainsKey(e.Name))
                        rp.Named[e.Name] = (float[])e.World.Clone();
                    if (track != null && e.World != null && track.Contains(e.Guid) && !rp.Tracked.ContainsKey(e.Guid))
                        rp.Tracked[e.Guid] = (float[])e.World.Clone();
                    var sets = e.RawPropertySets;
                    if (sets == null) continue;
                    if (e.Name != null && !rp.Labels.ContainsKey(e.Name) && sets.Any(p => p.ClassName == "gCAIHelper_Label_PS"))
                        rp.Labels[e.Name] = (float[])e.World.Clone();
                    var anchor = sets.FirstOrDefault(p => p.ClassName == "gCAnchor_PS");
                    if (anchor != null && !rp.Anchors.ContainsKey(e.Guid))
                        rp.Anchors[e.Guid] = new Anchor
                        {
                            Name = e.Name, World = (float[])e.World.Clone(),
                            Type = (AnchorType)Props.Enum(anchor, "AnchorType"), MaxUsers = Props.Int(anchor, "MaxUsers"),
                        };
                    var interaction = sets.FirstOrDefault(p => p.ClassName == "gCInteraction_PS");
                    if (interaction == null || rp.Points.ContainsKey(e.Guid) || sets.Any(p => p.ClassName == "gCNPC_PS")) continue;
                    var pt = new Point
                    {
                        Name = e.Name, World = (float[])e.World.Clone(),
                        UseType = Props.Enum(interaction, "UseType"),
                        Anchor = Props.Proxy(interaction, "AnchorPoint"),
                        Owner = Props.Proxy(interaction, "Owner"),
                    };
                    // bTValArray<bCMotion>: u8 version + u32 count + count × (bCVector position, bCQuaternion x y z w).
                    var v = sets.FirstOrDefault(p => p.ClassName == "gCNavOffset_PS")?.Get("OffsetPose")?.Value;
                    if (v != null && v.Length >= 5)
                    {
                        int n = BitConverter.ToInt32(v, 1);
                        for (int i = 0; i < n && 5 + (i + 1) * 28 <= v.Length; i++)
                        {
                            var o = new float[7];
                            for (int k = 0; k < 7; k++) o[k] = BitConverter.ToSingle(v, 5 + i * 28 + k * 4);
                            pt.Offsets.Add(o);
                        }
                    }
                    // bTValArray<gEDirection>: u8 version + u32 count + count × u32.
                    var d = sets.FirstOrDefault(p => p.ClassName == "gCNavOffset_PS")?.Get("AniDirection")?.Value;
                    if (d != null && d.Length >= 5)
                    {
                        int n = BitConverter.ToInt32(d, 1);
                        for (int i = 0; i < n && 5 + (i + 1) * 4 <= d.Length; i++) pt.AniDirections.Add(BitConverter.ToInt32(d, 5 + i * 4));
                    }
                    rp.Points[e.Guid] = pt;
                }
            }
            return rp;
        }

        /// <summary>
        /// Moves every NPC to where its routine puts it at game start (Docs/formats-animation.md §8.6): the level editor
        /// left many NPCs parked in rows beside their area; Gothic 3 starts the "Start" routine at once, which walks
        /// (off-screen: teleports) each NPC to the point of the current phase. The working point is used (daytime,
        /// 6–20 h), else the relaxing / sleeping point. At an interaction object the NPC stands at a free OffsetPose,
        /// facing along its −Z (the object); at a free point it takes the point's position and heading. A point holds one
        /// NPC (one per OffsetPose); NPCs whose points are missing, taken or farther than <see cref="MaxMove"/> keep their editor matrix.
        /// Returns the number of NPCs moved.
        /// </summary>
        public int PlaceAtRoutineStart(IEnumerable<NpcSpawn> spawns)
        {
            var taken = new HashSet<(Guid, int)>();
            int moved = 0;
            foreach (var s in spawns)
            {
                if (s.EditorWorld != null) continue;
                foreach (var id in new[] { s.WorkingPoint, s.RelaxingPoint, s.SleepingPoint })
                {
                    if (id == Guid.Empty || !Points.TryGetValue(id, out var pt)) continue;
                    float dx = pt.World[12] - s.X, dz = pt.World[14] - s.Z;
                    if (dx * dx + dz * dz > MaxMove * MaxMove) continue;
                    int slots = pt.SlotCount, slot = -1;
                    for (int i = 0; i < slots && slot < 0; i++) if (!taken.Contains((id, i))) slot = i;
                    if (slot < 0) continue;
                    taken.Add((id, slot));
                    s.EditorWorld = s.World;
                    s.World = Upright(UserPoseOf(pt, slot), s.EditorWorld);
                    s.StartPoint = pt.Name;
                    moved++;
                    break;
                }
            }
            return moved;
        }

        /// <summary>
        /// Where the user of <paramref name="slot"/> stands: at an interaction object the OffsetPose (object space → world)
        /// facing the pose's −Z, i.e. the object; at a free point the point itself with its heading (formats-animation §8.6).
        /// </summary>
        public static UserPose UserPoseOf(Point pt, int slot)
        {
            var m = pt.World;
            float[] Row(int r) => new[] { m[r * 4], m[r * 4 + 1], m[r * 4 + 2] };
            float[] Norm(float[] a) { float l = (float)Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); return l > 1e-6f ? new[] { a[0] / l, a[1] / l, a[2] / l } : a; }
            float[] ax = Norm(Row(0)), ay = Norm(Row(1)), az = Norm(Row(2));
            float px = m[12], py = m[13], pz = m[14];
            float[] fwd = az;
            var off = slot >= 0 && slot < pt.Offsets.Count ? pt.Offsets[slot] : null;
            if (off != null)
            {
                // Position: object space → world (row vectors: p × M, axes without scale — OffsetPose is in cm).
                px += off[0] * ax[0] + off[1] * ay[0] + off[2] * az[0];
                py += off[0] * ax[1] + off[1] * ay[1] + off[2] * az[1];
                pz += off[0] * ax[2] + off[1] * ay[2] + off[2] * az[2];
                // The user faces the pose's −Z: rotate (0,0,−1) by q, then into world space.
                float qx = off[3], qy = off[4], qz = off[5], qw = off[6];
                float lx = -(2 * (qx * qz + qw * qy)), ly = -(2 * (qy * qz - qw * qx)), lz = -(1 - 2 * (qx * qx + qy * qy));
                fwd = new[] { lx * ax[0] + ly * ay[0] + lz * az[0], lx * ax[1] + ly * ay[1] + lz * az[1], lx * ax[2] + ly * ay[2] + lz * az[2] };
            }
            float hl = (float)Math.Sqrt(fwd[0] * fwd[0] + fwd[2] * fwd[2]);
            return new UserPose(px, py, pz, hl > 1e-4f ? fwd[0] / hl : 0, hl > 1e-4f ? fwd[2] / hl : 1);
        }

        /// <summary>Upright NPC matrix (row-major, cm) at a user pose; keeps the NPC's own scale.</summary>
        static float[] Upright(UserPose p, float[] own)
        {
            float fx = p.ForwardX, fz = p.ForwardZ;
            float Len(int r) => (float)Math.Sqrt(own[r * 4] * own[r * 4] + own[r * 4 + 1] * own[r * 4 + 1] + own[r * 4 + 2] * own[r * 4 + 2]);
            float sx = Len(0), sy = Len(1), sz = Len(2);
            // Left-handed, Y up: right = up × forward = (fz, 0, −fx).
            return new[]
            {
                fz * sx, 0, -fx * sx, 0,
                0, sy, 0, 0,
                fx * sz, 0, fz * sz, 0,
                p.X, p.Y, p.Z, 1,
            };
        }
    }
}
