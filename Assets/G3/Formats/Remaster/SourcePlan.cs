using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using G3.Formats.Animation;

namespace G3.Formats.Remaster
{
    public enum SourceKind { Body, Head, Facial, Hair, Beard }

    /// <summary>One Source glb to write: a part of a rig built from <see cref="Spawn"/>.</summary>
    public sealed class SourceJob
    {
        public SourceKind Kind;
        /// <summary>Actor or mesh stem (the part).</summary>
        public string Stem;
        /// <summary>Source file stem (<see cref="Stem"/>, or <c>&lt;stem&gt;__talk</c> for a body from the talking-head rig).</summary>
        public string FileStem;
        /// <summary>The rig the part is exported from (as worn; the talking-head rig for facial heads).</summary>
        public NpcSpawn Spawn;
        public bool IsMesh;
        /// <summary>Spawns wearing the part (for a facial head: those wearing its static head).</summary>
        public readonly List<NpcSpawn> Wearers = new List<NpcSpawn>();
        /// <summary>Static head of a facial actor / facial actor of a static head.</summary>
        public string Partner;
        public override string ToString() => $"{Kind} {FileStem} <- {Spawn?.Name}";
    }

    /// <summary>
    /// What the offline exporter writes for "all" (Docs/remaster-pipeline.md §12): every human body actor, static head,
    /// facial head (<c>…_Animated_NN</c>) and hair/beard mesh worn by a world NPC (switched-off sectors included), each
    /// from the first spawn that wears it (the face-kit importer's choice of sample), as worn (MaterialSwitch applied).
    /// Facial heads: the talking-head rig (sample's body + facial actor, no switch), like G3RemasterExporter.ExportHero
    /// and G3FaceKitImporter.
    /// </summary>
    public static class SourcePlan
    {
        /// <summary>Human looks: the G3 human skeleton (Hero_*; fat and slave bodies are skinned on it too) with a body part.</summary>
        public static bool IsHuman(NpcSpawn s) =>
            s.Parts.Any(p => p.IsBody && Regex.IsMatch(p.Stem, @"^(G3|GFG)_(Hero|Fat|Slave|Babe)_Body_", RegexOptions.IgnoreCase));

        /// <summary>
        /// Human part files in the data that no spawn wears: <c>(G3|GFG)_Hero_Body_*</c> (not the undead monster bodies, not
        /// LoD actors) and <c>G3_Animation_Head_(Hair|Beard)_*</c> meshes. G3_Fat_/G3_Slave_Body_ actors use their own,
        /// unused skeletons and are left out.
        /// </summary>
        public static bool IsUnwornCandidate(string stem, bool isMesh) => isMesh
            ? Regex.IsMatch(stem, @"^G3_Animation_Head_(Hair|Beard)_", RegexOptions.IgnoreCase)
            : Regex.IsMatch(stem, @"^(G3|GFG)_Hero_Body_", RegexOptions.IgnoreCase) && !Regex.IsMatch(stem, @"_LOD\d+$", RegexOptions.IgnoreCase) &&
              !Regex.IsMatch(stem, @"_Body_(Mummy|Skeleton|SlaveZombie|UndeadPriest)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Adds jobs for candidate parts no spawn wears, on the hero's rig (<paramref name="hero"/>): a body replaces his
        /// body, a hair/beard mesh hangs on his head's slot. They have no wearers.
        /// </summary>
        public static void AddUnworn(List<SourceJob> jobs, IEnumerable<(string stem, bool isMesh)> candidates, NpcSpawn hero, ICollection<SourceKind> kinds)
        {
            var have = new HashSet<string>(jobs.Select(j => j.Stem), StringComparer.OrdinalIgnoreCase);
            foreach (var (stem, isMesh) in candidates.OrderBy(c => c.stem, StringComparer.OrdinalIgnoreCase))
            {
                if (have.Contains(stem) || !IsUnwornCandidate(stem, isMesh)) continue;
                var part = new NpcPart { Resource = stem + (isMesh ? ".xcmsh" : ".xact"), IsMesh = isMesh, EntityName = stem };
                var kind = KindOf(part);
                if (kind == null || !kinds.Contains(kind.Value)) continue;
                var s = new NpcSpawn { Name = hero.Name, Actor = hero.Actor, Enabled = true, World = Identity() };
                foreach (var p in hero.Parts.Where(p => !(kind == SourceKind.Body && p.IsBody) && !p.IsMesh))
                    s.Parts.Add(new NpcPart { Resource = p.Resource, Switch = p.Switch, EntityName = p.EntityName, FacialResource = p.FacialResource });
                s.Parts.Add(part);
                jobs.Add(new SourceJob { Kind = kind.Value, Stem = stem, FileStem = stem, Spawn = s, IsMesh = isMesh });
                have.Add(stem);
            }
        }

        public static SourceKind? KindOf(NpcPart p)
        {
            if (p.IsBody) return SourceKind.Body;
            if (p.IsHead) return SourceKind.Head;
            if (!p.IsMesh) return null;
            if (p.Stem.IndexOf("_Beard_", StringComparison.OrdinalIgnoreCase) >= 0) return SourceKind.Beard;
            if (p.Stem.IndexOf("_Hair_", StringComparison.OrdinalIgnoreCase) >= 0) return SourceKind.Hair;
            return null; // helmets etc.
        }

        /// <summary>
        /// The facial (talking) actor of a static head: its FacialAnimFilePath, else named after the head
        /// (<c>G3_Head_Hero_Hero_01</c> → <c>G3_Head_Hero_Hero_Animated_01</c>, <c>X</c> → <c>X_Animated</c>) when that actor exists.
        /// </summary>
        public static string FacialOf(NpcPart head, Func<string, bool> actorExists)
        {
            if (head.FacialResource != null)
            {
                string f = Path.GetFileNameWithoutExtension(head.FacialResource);
                if (actorExists(f)) return f;
            }
            var numbered = Regex.Match(head.Stem, @"^(.*)_(\d+)$");
            if (numbered.Success && actorExists($"{numbered.Groups[1].Value}_Animated_{numbered.Groups[2].Value}"))
                return $"{numbered.Groups[1].Value}_Animated_{numbered.Groups[2].Value}";
            return actorExists(head.Stem + "_Animated") ? head.Stem + "_Animated" : null;
        }

        /// <summary>The talking-head rig of a sample spawn: its body part(s) + the facial actor (no switch), named after the facial actor.</summary>
        public static NpcSpawn TalkSpawn(NpcSpawn sample, string facial, string name = null)
        {
            var s = new NpcSpawn { Name = name ?? facial, Actor = sample.Actor, Switch = sample.Switch, Enabled = true, World = Identity() };
            foreach (var p in sample.Parts.Where(p => p.IsBody))
                s.Parts.Add(new NpcPart { Resource = p.Resource, Switch = p.Switch, EntityName = p.EntityName });
            s.Parts.Add(new NpcPart { Resource = facial + ".xact", EntityName = facial });
            return s;
        }

        /// <summary>The spawns with the named ones first (in the given order), the rest in their order: preferred samples.</summary>
        public static List<NpcSpawn> Preferred(IEnumerable<NpcSpawn> spawns, IList<string> names)
        {
            int Rank(NpcSpawn s) { for (int i = 0; i < names.Count; i++) if (string.Equals(s.Name, names[i], StringComparison.OrdinalIgnoreCase)) return i; return names.Count; }
            return spawns.Select((s, i) => (s, i)).OrderBy(x => Rank(x.s)).ThenBy(x => x.i).Select(x => x.s).ToList();
        }

        public static bool IsHero(IEnumerable<NpcSpawn> wearers) => wearers.Any(w => string.Equals(w.Name, "PC_Hero", StringComparison.OrdinalIgnoreCase));

        public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        /// <param name="spawns">world spawns in importer order (the first wearer is the sample)</param>
        /// <param name="kinds">kinds to plan (Head includes Facial)</param>
        public static List<SourceJob> Make(IEnumerable<NpcSpawn> spawns, ICollection<SourceKind> kinds, Func<string, bool> actorExists)
        {
            var jobs = new Dictionary<string, SourceJob>(StringComparer.OrdinalIgnoreCase);
            var order = new List<SourceJob>();
            foreach (var s in spawns.Where(IsHuman))
                foreach (var p in s.Parts)
                {
                    var kind = KindOf(p);
                    if (kind == null || !kinds.Contains(kind.Value)) continue;
                    if (!jobs.TryGetValue(p.Stem, out var job))
                    {
                        if (!p.IsMesh && !actorExists(p.Stem)) continue;
                        job = new SourceJob { Kind = kind.Value, Stem = p.Stem, FileStem = p.Stem, Spawn = s, IsMesh = p.IsMesh };
                        jobs[p.Stem] = job;
                        order.Add(job);
                        if (kind == SourceKind.Head && kinds.Contains(SourceKind.Facial))
                        {
                            string facial = FacialOf(p, actorExists);
                            if (facial != null && !jobs.ContainsKey(facial))
                            {
                                job.Partner = facial;
                                var fj = new SourceJob { Kind = SourceKind.Facial, Stem = facial, FileStem = facial, Spawn = TalkSpawn(s, facial), Partner = p.Stem };
                                jobs[facial] = fj;
                                order.Add(fj);
                            }
                        }
                    }
                    if (!job.Wearers.Contains(s)) job.Wearers.Add(s);
                    if (job.Partner != null && jobs.TryGetValue(job.Partner, out var partner) && partner.Kind == SourceKind.Facial && !partner.Wearers.Contains(s))
                        partner.Wearers.Add(s);
                }
            return order;
        }

        static readonly Dictionary<SourceKind, string> KindNames = new Dictionary<SourceKind, string>
        {
            [SourceKind.Body] = "body", [SourceKind.Head] = "head", [SourceKind.Facial] = "facial", [SourceKind.Hair] = "hair", [SourceKind.Beard] = "beard",
        };

        public static string Name(SourceKind k) => KindNames[k];
    }

    /// <summary>One line of <c>Source/manifest.json</c>.</summary>
    public sealed class SourceManifestEntry
    {
        public string Actor, Kind, File, Spawn, Partner;
        public int Switch, Vertices, Triangles, Nodes;
        /// <summary>World spawns wearing the part (0 = unworn, exported on the hero's rig).</summary>
        public int Spawns;
        public long Bytes;
        public bool LipSync;
        /// <summary>Material name → base colour image of the exported file.</summary>
        public readonly List<(string material, string image)> Materials = new List<(string, string)>();
        /// <summary>Base colour images over every switch the wearers use (the R1 texture sets this part needs).</summary>
        public readonly List<string> TextureSets = new List<string>();
        public readonly List<int> Switches = new List<int>();
        /// <summary>Content-pack NPC ids wearing the part.</summary>
        public readonly List<string> Npcs = new List<string>();
        public string Error;
    }

    public static class SourceManifest
    {
        public static string ToJson(IEnumerable<SourceManifestEntry> entries, string generator)
        {
            var list = entries.ToList();
            var sb = new StringBuilder();
            sb.Append("{\n  \"generator\": ").Append(Q(generator)).Append(",\n");
            sb.Append("  \"counts\": {");
            sb.Append(string.Join(", ", list.GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{Q(g.Key)}: {g.Count()}")));
            sb.Append("},\n  \"bytes\": ").Append(list.Sum(e => e.Bytes)).Append(",\n  \"parts\": [\n");
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                sb.Append("    {");
                sb.Append($"\"actor\": {Q(e.Actor)}, \"kind\": {Q(e.Kind)}, \"file\": {Q(e.File)}, \"spawn\": {Q(e.Spawn)}, \"switch\": {e.Switch}, \"spawns\": {e.Spawns}, ");
                if (e.Partner != null) sb.Append($"\"partner\": {Q(e.Partner)}, ");
                if (e.Kind == "facial") sb.Append($"\"lipSync\": {(e.LipSync ? "true" : "false")}, ");
                sb.Append($"\"vertices\": {e.Vertices}, \"tris\": {e.Triangles}, \"nodes\": {e.Nodes}, \"bytes\": {e.Bytes}, ");
                sb.Append("\"materials\": [").Append(string.Join(", ", e.Materials.Select(m => $"{{\"name\": {Q(m.material)}, \"image\": {Q(m.image)}}}"))).Append("], ");
                sb.Append("\"textureSets\": [").Append(string.Join(", ", e.TextureSets.Select(Q))).Append("], ");
                sb.Append("\"switches\": [").Append(string.Join(", ", e.Switches)).Append("], ");
                if (e.Error != null) sb.Append($"\"error\": {Q(e.Error)}, ");
                sb.Append("\"npcs\": [").Append(string.Join(", ", e.Npcs.Select(Q))).Append("]}");
                sb.Append(i < list.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
