using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Genome;
using G3.Formats.Items;
using G3Inventory = G3.Formats.Items.Inventory;
using G3.Formats.World;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The hero's interactive objects of the world (Docs/interactive-objects.md): chests (gCInventory_PS + UseType Chest),
    /// doors (gCDoor_PS), beds, crafting/reading stations, orc boulders, seats (Bench/Stool/Throne), water barrels, ore veins
    /// (gCInventory_PS + UseType PickOre) and treasure spots (gCInventory_PS + UseType PirateTreasure) with a visible mesh, each
    /// with its lock (gCLock_PS) and owner (gCInteraction_PS.Owner). Reads every .lrentdat and .node of the load set.
    /// Invisible AI points (SleepGround, Roam …) are not interactive for the hero and are left to the routine importer.
    /// </summary>
    public sealed class InteractiveObjectImporter
    {
        const float CentimetresToMetres = 0.01f;
        static readonly HashSet<UseType> Stations = new HashSet<UseType>
        {
            UseType.Anvil, UseType.Forge, UseType.GrindStone, UseType.Cauldron, UseType.Barbecue, UseType.Alchemy,
            UseType.Bookshelf, UseType.Bookstand, UseType.Stoneplate, UseType.Campfire,
        };

        static readonly HashSet<UseType> Seats = new HashSet<UseType> { UseType.Bench, UseType.Stool, UseType.Throne };
        /// <summary>
        /// Looping hero uses (CP 1.75 world, `G3Cli objects usetypes`: Waterpipe 30, SawLog 44, OrcDrum 11, all with a mesh).
        /// Forge 19, SleepGround 326 and the CarryStone points (TakeStone 34) have no mesh, so the hero never focuses them.
        /// </summary>
        static readonly HashSet<UseType> Activities = new HashSet<UseType> { UseType.Waterpipe, UseType.SawLog, UseType.OrcDrum };
        /// <summary>Use types of inventory-carrying objects that are no chest (their inventory holds the treasure sets).</summary>
        static readonly HashSet<UseType> TreasureObjects = new HashSet<UseType> { UseType.PickOre, UseType.PirateTreasure };

        readonly ItemImporter _items;
        readonly Func<Guid, ActorId> _actorOf;
        readonly ImportReport _report;

        public InteractiveObjectImporter(ItemImporter items, Func<Guid, ActorId> actorOf, ImportReport report)
        {
            _items = items;
            _actorOf = actorOf;
            _report = report;
        }

        public List<InteractiveObjectDef> Objects { get; } = new List<InteractiveObjectDef>();

        /// <summary>The sectors to read (null = the enabled ones only).</summary>
        public SwitchableSectors Sectors { get; set; }

        /// <summary>
        /// Names that own dialog entries (the infos' owners): a world entity with such a name and a gCDialog_PS that is no
        /// animated NPC becomes a <see cref="InteractiveKind.Speaker"/> (story-4). Null = none.
        /// </summary>
        public ISet<string> DialogOwners { get; set; }

        public void Import(G3Install install)
        {
            const string volume = "Projects_compiled";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var meshes = install.Open("_compiledMesh"))
            using (var fs = install.Open(volume))
            {
                _meshes = meshes;
                var sectors = Sectors ?? new SwitchableSectors(null);
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(volume + "/" + p)?.Read(), includeDisabled: true);
                foreach (var sector in layout.Sectors.Where(sectors.Reads))
                foreach (var path in sector.Lrentdats.Concat(sector.Nodes))
                {
                    List<WorldEntity> list;
                    try { list = EntityArchive.Read(fs.Get(volume + "/" + path)?.Read(), false, keepRaw: true); }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is ArgumentException)
                    {
                        _report.Count("unreadable world entity file", path + ": " + ex.Message);
                        continue;
                    }
                    if (list == null) continue;
                    var containers = new List<WorldContainer>();
                    var interactables = new List<WorldInteractable>();
                    WorldItemScanner.Scan(list, null, containers, interactables);
                    string tag = sectors.TagOf(sector);
                    foreach (var c in containers.Where(c => (c.Kind == ContainerKind.Chest || c.Kind == ContainerKind.Other &&
                                                             TreasureObjects.Contains(c.Interaction?.UseType ?? UseType.None)) &&
                                                            SwitchableSectors.Counts(c.Entity, sector)))
                        Add(c.Entity, c.Interaction, c.Lock, null, c.Inventory, seen, tag);
                    foreach (var i in interactables.Where(i => SwitchableSectors.Counts(i.Entity, sector)))
                        Add(i.Entity, i.Interaction, i.Lock, i.Door, null, seen, tag);
                    foreach (var e in list.Where(e => SwitchableSectors.Counts(e, sector)))
                        AddCookFire(e, seen, tag); // fix-88: lit fireplaces, fire cages and basins ([owner] option)
                    if (DialogOwners != null)
                        foreach (var e in list.Where(e => SwitchableSectors.Counts(e, sector) && IsSpeaker(e)))
                            AddSpeaker(e, seen, tag);
                }
                _meshes = null;
            }
            _report.Note($"{Objects.Count} interactive objects: " +
                         string.Join(", ", Objects.GroupBy(o => o.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        }

        void Add(WorldEntity e, InteractionInfo interaction, LockInfo lockInfo, DoorInfo door, G3Inventory inventory, HashSet<string> seen,
                 string sector)
        {
            var useType = interaction?.UseType ?? UseType.None;
            InteractiveKind kind;
            if (useType == UseType.PickOre) kind = InteractiveKind.OreVein;              // PS_Interact_PickOre (par-interact)
            else if (useType == UseType.PirateTreasure) kind = InteractiveKind.Treasure; // PS_Interact_PirateTreasure
            else if (inventory != null) kind = InteractiveKind.Chest;
            else if (door != null || useType == UseType.Door) kind = InteractiveKind.Door;
            else if (useType == UseType.Bed) kind = InteractiveKind.Bed;
            else if (Stations.Contains(useType)) kind = InteractiveKind.Station;
            else if (useType == UseType.OrcBoulder) kind = InteractiveKind.Boulder; // OB_Light/Medium/Heavy/Faring_Boulder (fix-13)
            else if (Seats.Contains(useType)) kind = InteractiveKind.Seat;          // PS_Interact_SitBench/SitStool/SitThrone
            else if (useType == UseType.WaterBarrel) kind = InteractiveKind.WaterBarrel; // PS_Interact_DrinkWaterBarrel (barrels, fountains)
            else if (Activities.Contains(useType)) kind = InteractiveKind.Activity;    // PS_Interact_UseWaterpipe / SawLog / OrcDrum (par-misc)
            else if (IsSignpost(e, interaction)) kind = InteractiveKind.Sign;           // focus name only (fix-80)
            else return;
            string visual = VisualOf(e);
            if (visual == null)
            {
                _report.Count("interactive object without a visual (skipped)", $"{kind} {e.Name}");
                return;
            }
            string id = e.Guid.ToString("D");
            if (!seen.Add(id)) return;
            var m = e.World;
            Objects.Add(new InteractiveObjectDef(
                id, e.Name, kind, useType.ToString(), visual,
                new[] { m[12] * CentimetresToMetres, m[13] * CentimetresToMetres, m[14] * CentimetresToMetres },
                Normalized(m[8], m[9], m[10]), Normalized(m[4], m[5], m[6]),
                (float)Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]),
                interaction != null && interaction.Owner != Guid.Empty ? _actorOf(interaction.Owner) : default,
                LockOf(e, lockInfo), ContainerOf(e, inventory),
                kind == InteractiveKind.Door ? new DoorDef(door?.Closed ?? true, Stem(door?.OpenAni), Stem(door?.CloseAni)) : null,
                null, string.IsNullOrWhiteSpace(interaction?.ScriptUseFunc) ? null : interaction.ScriptUseFunc.Trim(), sector,
                kind == InteractiveKind.Sign ? MeshHeight(visual) : 0f));
        }

        /// <summary>
        /// A focus-only object: gCInteraction_PS with UseType None and a focus name (FocusNameType not Disable) on a static
        /// mesh, no dialog (CP 1.75 world, `G3Cli objects usetypes None`: the 146 signpost boards Sign_&lt;Place&gt; with
        /// G3_Object_Sign_Board_01.xcmsh; the two temple door testers are actors and stay out). G3 focuses it (FocusPriority
        /// 1) and shows <c>FO_&lt;Name&gt;</c>; Use does nothing.
        /// </summary>
        static bool IsSignpost(WorldEntity e, InteractionInfo interaction) =>
            interaction != null && interaction.UseType == UseType.None && interaction.FocusNameType != FocusNameDisabled &&
            !string.IsNullOrEmpty(e.Name) && e.RawPropertySets != null &&
            e.RawPropertySets.Any(p => p.ClassName == "eCVisualMeshStatic_PS" || p.ClassName == "eCVisualMeshDynamic_PS") &&
            !e.RawPropertySets.Any(p => p.ClassName == "gCDialog_PS" || p.ClassName == "eCVisualAnimation_PS");

        /// <summary>gEFocusNameType Disable.</summary>
        const int FocusNameDisabled = 3;

        global::G3.Formats.Pak.G3FileSystem _meshes;
        readonly Dictionary<string, float> _meshHeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Height of a static mesh's bounds in metres (0 when it cannot be read).</summary>
        float MeshHeight(string mesh)
        {
            if (string.IsNullOrEmpty(mesh)) return 0f;
            if (_meshHeights.TryGetValue(mesh, out float h)) return h;
            h = 0f;
            var entry = _meshes?.GetByName(mesh);
            if (entry != null)
            {
                try
                {
                    var m = global::G3.Formats.Mesh.Xcmsh.Read(entry.Read(), mesh);
                    h = Math.Max(0f, (m.BoundsMax[1] - m.BoundsMin[1]) * CentimetresToMetres);
                }
                catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is ArgumentException || ex is System.IO.EndOfStreamException)
                {
                    _report.Count("unreadable focus mesh", mesh + ": " + ex.Message);
                }
            }
            else _report.Count("focus mesh not found", mesh);
            return _meshHeights[mesh] = h;
        }

        /// <summary>
        /// A burning decorative fire (fix-88, <see cref="CookFires.Classify"/>): a static mesh with a gCEffect_PS and no
        /// gCInteraction_PS. It becomes a <see cref="InteractiveKind.Station"/> of use type <see cref="CookFires.UseType"/>
        /// named after its mesh (the focus name <c>FO_&lt;mesh&gt;</c> is a port text, <c>PatchStrings</c>; G3 has none).
        /// </summary>
        void AddCookFire(WorldEntity e, HashSet<string> seen, string sector)
        {
            if (e.RawPropertySets == null || e.IsNpc || e.IsItem || !e.Enabled) return;
            PropertyObject effect = null;
            foreach (var ps in e.RawPropertySets)
            {
                if (ps.ClassName == "gCInteraction_PS") return;
                if (ps.ClassName == "gCEffect_PS") effect = ps;
            }
            if (effect == null || !e.RawPropertySets.Any(p => p.ClassName == "eCVisualMeshStatic_PS")) return;
            string visual = VisualOf(e);
            string stem = Stem(visual);
            if (stem == null || stem.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!MeshBounds(visual, out var min, out var max)) return;
            var o = effect.Get("Offset");
            var offset = o != null && o.Value != null && o.Value.Length >= 12
                ? new[] { BitConverter.ToSingle(o.Value, 0), BitConverter.ToSingle(o.Value, 4), BitConverter.ToSingle(o.Value, 8) }
                : new float[3];
            var fire = CookFires.Classify(stem, Props.Str(e.Source, effect, "Effect"), offset, min, max);
            if (fire == null) return;
            string id = e.Guid.ToString("D");
            if (!seen.Add(id)) return;
            var m = e.World;
            Objects.Add(new InteractiveObjectDef(
                id, stem, InteractiveKind.Station, CookFires.UseType, visual,
                new[] { m[12] * CentimetresToMetres, m[13] * CentimetresToMetres, m[14] * CentimetresToMetres },
                Normalized(m[8], m[9], m[10]), Normalized(m[4], m[5], m[6]),
                (float)Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]), default, null, null, null, null, null, sector,
                0f, fire));
            _report.Count("cooking fire (fix-88)", stem);
        }

        readonly Dictionary<string, (float[] min, float[] max)> _meshBounds = new Dictionary<string, (float[], float[])>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A static mesh's bounds in cm (false when it cannot be read).</summary>
        bool MeshBounds(string mesh, out float[] min, out float[] max)
        {
            min = max = null;
            if (string.IsNullOrEmpty(mesh)) return false;
            if (!_meshBounds.TryGetValue(mesh, out var b))
            {
                var entry = _meshes?.GetByName(mesh);
                if (entry != null)
                {
                    try
                    {
                        var r = global::G3.Formats.Mesh.Xcmsh.Read(entry.Read(), mesh);
                        b = (r.BoundsMin, r.BoundsMax);
                    }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is ArgumentException || ex is System.IO.EndOfStreamException)
                    {
                        _report.Count("unreadable fire mesh", mesh + ": " + ex.Message);
                    }
                }
                _meshBounds[mesh] = b;
            }
            min = b.min;
            max = b.max;
            return min != null;
        }

        bool IsSpeaker(WorldEntity e) =>
            !string.IsNullOrEmpty(e.Name) && DialogOwners.Contains(e.Name) && e.RawPropertySets != null &&
            e.RawPropertySets.Any(p => p.ClassName == "gCDialog_PS") && !e.RawPropertySets.Any(p => p.ClassName == "eCVisualAnimation_PS");

        /// <summary>
        /// A talking object (statue, altar, furnace …); its visual may be none (an invisible helper next to a mesh). A shrine
        /// (UseType Shrine: the Living_Ancestor testers) keeps its use type: the hero prays before he talks to it.
        /// </summary>
        void AddSpeaker(WorldEntity e, HashSet<string> seen, string sector)
        {
            string id = e.Guid.ToString("D");
            if (!seen.Add(id)) return;
            var m = e.World;
            var useType = InteractionInfo.From(e.Source, e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCInteraction_PS"))?.UseType ?? UseType.None;
            Objects.Add(new InteractiveObjectDef(
                id, e.Name, InteractiveKind.Speaker, (useType == UseType.Shrine ? useType : UseType.None).ToString(), VisualOf(e),
                new[] { m[12] * CentimetresToMetres, m[13] * CentimetresToMetres, m[14] * CentimetresToMetres },
                Normalized(m[8], m[9], m[10]), Normalized(m[4], m[5], m[6]),
                (float)Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]), default, null, null, null, null, null, sector));
        }

        LockDef LockOf(WorldEntity e, LockInfo info)
        {
            if (info == null) return null;
            string key = info.Key != Guid.Empty ? _items.NameOf(info.Key) : null;
            if (info.Key != Guid.Empty && key == null) _report.Count("lock key template not found", e.Name);
            var ps = e.RawPropertySets?.FirstOrDefault(p => p.ClassName == "gCLock_PS");
            return new LockDef(info.Locked, info.Difficulty, key, Props.Int(ps, "KeyAmount", 1));
        }

        ContainerDef ContainerOf(WorldEntity e, G3Inventory inventory)
        {
            if (inventory == null) return null;
            var sets = inventory.TreasureSets.Where(s => !string.IsNullOrEmpty(s)).ToList();
            var stacks = new List<TreasureStack>();
            foreach (var s in inventory.Stacks)
            {
                string item = _items.NameOf(s.Template);
                if (item == null || !_items.IsItem(item))
                {
                    _report.Count("chest stack of an unknown item", e.Name);
                    continue;
                }
                stacks.Add(new TreasureStack(item, Math.Max(1, s.Amount), s.Quality));
            }
            return new ContainerDef(sets, stacks);
        }

        static string VisualOf(WorldEntity e)
        {
            if (e.RawPropertySets == null) return null;
            foreach (var ps in e.RawPropertySets)
            {
                if (ps.ClassName == "eCVisualAnimation_PS") return Props.Str(e.Source, ps, "ResourceFilePath");
                if (ps.ClassName == "eCVisualMeshStatic_PS" || ps.ClassName == "eCVisualMeshDynamic_PS") return Props.Str(e.Source, ps, "ResourceFileName");
            }
            return null;
        }

        static string Stem(string file) => string.IsNullOrEmpty(file) ? null : System.IO.Path.GetFileNameWithoutExtension(file);

        static float[] Normalized(float x, float y, float z)
        {
            float length = (float)Math.Sqrt(x * x + y * y + z * z);
            return length > 1e-6f ? new[] { x / length, y / length, z / length } : new float[] { 0, 0, 1 };
        }
    }
}
