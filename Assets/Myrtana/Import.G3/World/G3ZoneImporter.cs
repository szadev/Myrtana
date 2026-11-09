using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Genome;
using G3.Formats.Items;
using G3.Formats.World;
using Myrtana.Rules;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// The enclaves (gCEnclave_PS: towns, camps, clans) and the owned AI zones (gCNavZone_PS + gCAIZone_PS: houses, camps,
    /// quarters) of the enabled world sectors (Docs/perception-crime.md §3.3). Zone outlines are the nav zone's points,
    /// local to the entity, converted to world metres; the owner proxy is resolved to the enclave's name.
    /// </summary>
    public sealed class G3ZoneImporter
    {
        const string Volume = "Projects_compiled";
        const float CentimetresToMetres = 0.01f;
        /// <summary>gEZoneType: 0 Outdoor, 1 Indoor.</summary>
        const int IndoorZoneType = 1;

        readonly Dictionary<Guid, EnclaveDef> _enclaves = new Dictionary<Guid, EnclaveDef>();
        readonly List<(WorldEntity Entity, PropertyObject Nav, PropertyObject Ai)> _zones = new List<(WorldEntity, PropertyObject, PropertyObject)>();

        public List<EnclaveDef> Enclaves { get; } = new List<EnclaveDef>();
        public List<AiZoneDef> Zones { get; } = new List<AiZoneDef>();

        /// <summary>The name of the enclave entity <paramref name="id"/> (null when unknown or empty).</summary>
        public string EnclaveNameOf(Guid id) => id != Guid.Empty && _enclaves.TryGetValue(id, out var e) ? e.Id : null;

        public void Import(G3Install install, ImportReport report)
        {
            using (var fs = install.Open(Volume))
            {
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(Volume + "/" + p)?.Read());
                foreach (var sector in layout.Sectors.Where(s => !NpcSpawns.IsTestSector(s.Name)))
                foreach (var path in sector.Lrentdats.Concat(sector.Nodes))
                {
                    List<WorldEntity> list;
                    try { list = EntityArchive.Read(fs.Get(Volume + "/" + path)?.Read(), false, keepRaw: true); }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is ArgumentException)
                    {
                        report.Count("unreadable world entity file (zones)", path + ": " + ex.Message);
                        continue;
                    }
                    if (list != null) Scan(list);
                }
            }
            foreach (var z in _zones) AddZone(z.Entity, z.Nav, z.Ai, report);
            report.Note($"{Enclaves.Count} enclaves, {Zones.Count} AI zones ({Zones.Count(z => z.Owner != null)} owned, {Zones.Count(z => z.Indoor)} indoor)");
        }

        void Scan(List<WorldEntity> entities)
        {
            foreach (var e in entities)
            {
                if (e.RawPropertySets == null || !e.Enabled) continue;
                var enclave = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCEnclave_PS");
                if (enclave != null && !string.IsNullOrEmpty(e.Name) && !_enclaves.ContainsKey(e.Guid))
                {
                    var alignment = (global::G3.Formats.Story.PoliticalAlignment)Props.Enum(enclave, "PoliticalAlignment");
                    var def = new EnclaveDef(e.Name, alignment == global::G3.Formats.Story.PoliticalAlignment.None ? null : alignment.ToString(),
                                             new[] { e.X * CentimetresToMetres, e.Y * CentimetresToMetres, e.Z * CentimetresToMetres });
                    _enclaves[e.Guid] = def;
                    Enclaves.Add(def);
                }
                var ai = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCAIZone_PS");
                var nav = e.RawPropertySets.FirstOrDefault(p => p.ClassName == "gCNavZone_PS");
                if (ai != null && nav != null) _zones.Add((e, nav, ai));
            }
        }

        void AddZone(WorldEntity e, PropertyObject nav, PropertyObject ai, ImportReport report)
        {
            var local = Points(nav.Get("Point")?.Value);
            if (local.Count < 3)
            {
                report.Count("AI zone without an outline", e.Name);
                return;
            }
            var m = e.World;
            var outline = new float[local.Count * 2];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < local.Count; i++)
            {
                var (x, y, z) = local[i];
                outline[2 * i] = (x * m[0] + y * m[4] + z * m[8] + m[12]) * CentimetresToMetres;
                outline[2 * i + 1] = (x * m[2] + y * m[6] + z * m[10] + m[14]) * CentimetresToMetres;
                float wy = (x * m[1] + y * m[5] + z * m[9]) * CentimetresToMetres;
                minY = Math.Min(minY, wy);
                maxY = Math.Max(maxY, wy);
            }
            var owner = Props.Proxy(ai, "Owner");
            string enclave = EnclaveNameOf(owner);
            if (owner != Guid.Empty && enclave == null) report.Count("AI zone owned by an unknown entity", e.Name);
            int security = Props.Enum(ai, "SecurityLevel");
            Zones.Add(new AiZoneDef(
                e.Guid.ToString("D"), e.Name, enclave,
                security >= 0 && security <= (int)ZoneSecurity.Political ? (ZoneSecurity)security : ZoneSecurity.None,
                Props.Enum(ai, "Type") == IndoorZoneType,
                outline, m[13] * CentimetresToMetres,
                Props.Float(nav, "TopToleranz") * CentimetresToMetres + maxY,
                Props.Float(nav, "BottomToleranz") * CentimetresToMetres - minY));
        }

        /// <summary>bTValArray&lt;bCVector&gt;: u8 version, u32 count, then x, y, z per point (centimetres, local).</summary>
        static List<(float, float, float)> Points(byte[] v)
        {
            var points = new List<(float, float, float)>();
            if (v == null || v.Length < 5) return points;
            int n = BitConverter.ToInt32(v, 1);
            for (int i = 0; i < n && 5 + 12 * i + 12 <= v.Length; i++)
                points.Add((BitConverter.ToSingle(v, 5 + 12 * i), BitConverter.ToSingle(v, 9 + 12 * i), BitConverter.ToSingle(v, 13 + 12 * i)));
            return points;
        }
    }
}
