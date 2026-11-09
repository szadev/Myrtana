using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Genome;
using G3.Formats.Items;
using G3.Formats.World;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// What the world's entity archives say about items and NPC roles: loose items and herbs lying in the world →
    /// <see cref="WorldItemDef"/>s; NPCs with trade sets or teach lists → <see cref="MerchantDef"/>s and
    /// <see cref="TeacherDef"/>s (gCDialog_PS TradeEnabled/TradeCategory/TeachEnabled, gCNPC_PS TeachAttribs/TeachSkills,
    /// gCInventory_PS treasure sets).
    /// </summary>
    public sealed class WorldContentImporter
    {
        const float CentimetresToMetres = 0.01f;
        /// <summary>gETradeCategory values with a specialist price (trading.json "specialists"; Game.dll enum: 1 Valuable, 2 Trophy).</summary>
        static readonly string[] Specialties = { null, "Valuable", "Trophy" };
        static readonly HashSet<TreasureUse> StockUses = new HashSet<TreasureUse> { TreasureUse.TradeGenerate, TreasureUse.TradeRefresh, TreasureUse.NotRandom };

        readonly ItemImporter _items;
        readonly IReadOnlyDictionary<string, TreasureSetDef> _sets;
        readonly IReadOnlyDictionary<Guid, ActorId> _actors;
        readonly ImportReport _report;

        /// <summary>The sectors to read (null = the enabled ones only).</summary>
        public SwitchableSectors Sectors { get; set; }

        public List<WorldItemDef> WorldItems { get; } = new List<WorldItemDef>();
        public List<MerchantDef> Merchants { get; } = new List<MerchantDef>();
        public List<TeacherDef> Teachers { get; } = new List<TeacherDef>();

        /// <param name="spawnOf">The NPC importer's actor ids (NpcImporter.SpawnOf); null = derived from <paramref name="spawns"/>.</param>
        public WorldContentImporter(ItemImporter items, IEnumerable<TreasureSetDef> sets, IEnumerable<NpcSpawn> spawns, ImportReport report,
                                    IReadOnlyDictionary<ActorId, NpcSpawn> spawnOf = null)
        {
            _items = items;
            _sets = sets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            // The NPC importer's own ids: a name shared only with switchable sectors keeps its plain id for the one
            // always-present spawn, which the derived "Name#guid" ids would miss.
            _actors = spawnOf != null ? spawnOf.GroupBy(kv => kv.Value.Guid).ToDictionary(g => g.Key, g => g.First().Key) : ActorIds(spawns);
            _report = report;
        }

        /// <summary>The actor ids the NPC importer gives: unique names as they are, shared names "Name#guid8".</summary>
        static Dictionary<Guid, ActorId> ActorIds(IEnumerable<NpcSpawn> spawns)
        {
            var map = new Dictionary<Guid, ActorId>();
            foreach (var group in spawns.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool shared = group.Count() > 1;
                foreach (var s in group) map[s.Guid] = shared ? NpcPlacementImporter.SharedNameId(s) : new ActorId(s.Name);
            }
            return map;
        }

        public void Import(G3Install install)
        {
            const string volume = "Projects_compiled";
            using (var fs = install.Open(volume))
            {
                var sectors = Sectors ?? new SwitchableSectors(null);
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(volume + "/" + p)?.Read(), includeDisabled: true);
                foreach (var sector in layout.Sectors.Where(sectors.Reads))
                foreach (var path in sector.Lrentdats)
                {
                    List<WorldEntity> list;
                    try { list = EntityArchive.Read(fs.Get(volume + "/" + path)?.Read(), false, keepRaw: true); }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is ArgumentException)
                    {
                        _report.Count("unreadable world entity file", path + ": " + ex.Message);
                        continue;
                    }
                    if (list == null) continue;
                    var items = new List<WorldItem>();
                    var containers = new List<WorldContainer>();
                    WorldItemScanner.Scan(list, items, containers, null);
                    foreach (var item in items) AddWorldItem(item, sector, sectors.TagOf(sector));
                    foreach (var npc in containers.Where(c => c.Kind == ContainerKind.NpcInventory)) AddRoles(npc, sector);
                }
            }
        }

        void AddWorldItem(WorldItem w, WorldLayout.Sector sector, string tag)
        {
            if ((w.Kind != WorldItemKind.Loose && w.Kind != WorldItemKind.Plant) || !SwitchableSectors.Counts(w.Entity, sector)) return;
            string item = _items.NameOf(w.Template);
            if (item == null || !_items.IsItem(item))
            {
                _report.Count("world item of a template that is not an inventory item", w.Entity.Name);
                return;
            }
            var m = w.Entity.World;
            var owner = OwnerOf(w.Entity);
            WorldItems.Add(new WorldItemDef(
                w.Entity.Guid.ToString("D"), item, Math.Max(1, w.Item.Amount),
                new[] { m[12] * CentimetresToMetres, m[13] * CentimetresToMetres, m[14] * CentimetresToMetres },
                Normalized(m[8], m[9], m[10]), Normalized(m[4], m[5], m[6]),
                (float)Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]), owner, tag));
        }

        /// <summary>The actor id of an NPC entity GUID (empty when it is not an imported NPC).</summary>
        public ActorId ActorOf(Guid npc) => _actors.TryGetValue(npc, out var actor) ? actor : default;

        ActorId OwnerOf(WorldEntity e)
        {
            var interaction = e.RawPropertySets?.FirstOrDefault(p => p.ClassName == "gCInteraction_PS");
            var owner = interaction == null ? Guid.Empty : Props.Proxy(interaction, "Owner");
            return owner != Guid.Empty && _actors.TryGetValue(owner, out var actor) ? actor : default;
        }

        void AddRoles(WorldContainer npc, WorldLayout.Sector sector)
        {
            // Merchants and teachers of switched-off sectors too (a liberated town's new traders).
            if (!SwitchableSectors.Counts(npc.Entity, sector)) return;
            if (!_actors.TryGetValue(npc.Entity.Guid, out var actor))
            {
                // A talking object (Innos/Beliar statue: gCNPC_PS + gCDialog_PS, no animated visual) teaches under its
                // name, the dialog owner its infos use; every copy of the statue shares it.
                if (!IsTalkingObject(npc.Entity)) return;
                actor = new ActorId(npc.Entity.Name);
                if (Teachers.Any(t => t.Actor == actor)) return;
            }
            var g = npc.Entity.Source;
            var dialog = npc.Entity.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCDialog_PS");
            var data = npc.Entity.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCNPC_PS");

            var stock = (npc.Inventory?.TreasureSets ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s) && _sets.TryGetValue(s, out var set) && StockUses.Contains(set.Use))
                .Select(s => _sets[s].Id).ToList();
            bool trades = Props.Bool(dialog, "TradeEnabled");
            if (stock.Count > 0 || trades)
            {
                int category = Props.Enum(dialog, "TradeCategory");
                var faction = (global::G3.Formats.Story.PoliticalAlignment)Props.Enum(data, "PoliticalAlignment");
                Merchants.Add(new MerchantDef(actor, faction.ToString(), category >= 0 && category < Specialties.Length ? Specialties[category] : null,
                                              stock, trades));
            }

            var attributes = StringArray(g, data?.Get("TeachAttribs")?.Value);
            var skills = ProxyArray(data?.Get("TeachSkills")?.Value).Select(_items.NameOf).Where(n => n != null).ToList();
            if (attributes.Count > 0 || skills.Count > 0)
                Teachers.Add(new TeacherDef(actor, attributes, skills, Props.Bool(dialog, "TeachEnabled")));
        }

        static bool IsTalkingObject(WorldEntity e) =>
            !string.IsNullOrEmpty(e.Name) && e.RawPropertySets != null && e.RawPropertySets.Any(p => p.ClassName == "gCDialog_PS") &&
            !e.RawPropertySets.Any(p => p.ClassName == "eCVisualAnimation_PS");

        /// <summary>bTObjArray&lt;bCString&gt;: u8 version, u32 count, count × u16 string index.</summary>
        static List<string> StringArray(GenomeReader g, byte[] value)
        {
            var list = new List<string>();
            if (value == null || value.Length < 5) return list;
            int n = BitConverter.ToInt32(value, 1);
            for (int i = 0; i < n && 5 + 2 * i + 2 <= value.Length; i++)
            {
                var s = g.StrFrom(new[] { value[5 + 2 * i], value[6 + 2 * i] });
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

        /// <summary>bTObjArray&lt;eCTemplateEntityProxy&gt;: u8 version, u32 count, count × (u16 version, bool valid, GUID, u32).</summary>
        static List<Guid> ProxyArray(byte[] value)
        {
            const int Header = 5, Entry = 23;
            var list = new List<Guid>();
            if (value == null || value.Length < Header) return list;
            int n = BitConverter.ToInt32(value, 1);
            for (int i = 0; i < n && Header + Entry * (i + 1) <= value.Length; i++)
            {
                var guid = Props.ProxyValue(value, Header + Entry * i);
                if (guid != Guid.Empty) list.Add(guid);
            }
            return list;
        }

        static float[] Normalized(float x, float y, float z)
        {
            float l = (float)Math.Sqrt(x * x + y * y + z * z);
            return l < 1e-6f ? new float[] { 0, 0, 1 } : new[] { x / l, y / l, z / l };
        }
    }
}
