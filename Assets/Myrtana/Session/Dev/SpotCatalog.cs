#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// A named test location (Tools/Scenarios/spots*.json): world metres (Unity space), optional yaw, the NPC it is
    /// about, named extra points ("shore", "far", "stand") and measured values (depth, distances). Only coordinates and
    /// ids — no game-derived assets.
    /// </summary>
    public sealed class TestSpot
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new List<string>();
        /// <summary>x, y, z in metres. y may be approximate; teleports snap to the ground unless the spot says swim.</summary>
        [JsonPropertyName("pos")] public float[] Pos { get; set; }
        [JsonPropertyName("yaw")] public float? Yaw { get; set; }
        [JsonPropertyName("npc")] public string Npc { get; set; }
        [JsonPropertyName("points")] public Dictionary<string, float[]> Points { get; set; }
        [JsonPropertyName("values")] public Dictionary<string, float> Values { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; }
        /// <summary>"computed" (G3Cli spots) or "manual" (verified by a runner).</summary>
        [JsonPropertyName("source")] public string Source { get; set; }

        public bool HasTag(string tag) =>
            string.Equals(Kind, tag, StringComparison.OrdinalIgnoreCase) || Tags != null && Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

        public float Value(string name, float fallback = float.NaN) =>
            Values != null && Values.TryGetValue(name, out var v) ? v : fallback;

        /// <summary>The main position or a named point ("shore"); false when missing.</summary>
        public bool TryPoint(string point, out float[] xyz)
        {
            xyz = string.IsNullOrEmpty(point) ? Pos : Points != null && Points.TryGetValue(point, out var p) ? p : null;
            return xyz != null && xyz.Length >= 3;
        }
    }

    /// <summary>Spot selection: id, or tags that must all match (kind counts as a tag), nearest to a point if given.</summary>
    public sealed class SpotQuery
    {
        public string Id { get; set; }
        public List<string> Tags { get; } = new List<string>();
        public float[] Near { get; set; }
        public float MaxDistance { get; set; } = float.PositiveInfinity;

        /// <summary>"deep_water_bloodfly" (id), "?water+deep" (tags), optional ".point" suffix is returned separately.</summary>
        public static SpotQuery Parse(string text, out string point)
        {
            point = null;
            var q = new SpotQuery();
            if (string.IsNullOrWhiteSpace(text)) return q;
            string s = text.Trim();
            int dot = s.LastIndexOf('.');
            if (dot > 0 && dot < s.Length - 1 && !char.IsDigit(s[dot + 1])) { point = s.Substring(dot + 1); s = s.Substring(0, dot); }
            if (s.StartsWith("?")) q.Tags.AddRange(s.Substring(1).Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries));
            else q.Id = s;
            return q;
        }
    }

    public sealed class SpotCatalog
    {
        sealed class FileShape
        {
            [JsonPropertyName("version")] public int Version { get; set; } = 1;
            [JsonPropertyName("generated")] public string Generated { get; set; }
            [JsonPropertyName("spots")] public List<TestSpot> Spots { get; set; } = new List<TestSpot>();
        }

        readonly List<TestSpot> _spots = new List<TestSpot>();

        public IReadOnlyList<TestSpot> Spots => _spots;
        public string Generated { get; set; }

        static JsonSerializerOptions Options(bool indented) => new JsonSerializerOptions
        {
            WriteIndented = indented,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static SpotCatalog Parse(string json)
        {
            var shape = JsonSerializer.Deserialize<FileShape>(json, Options(false)) ?? new FileShape();
            var c = new SpotCatalog { Generated = shape.Generated };
            foreach (var s in shape.Spots ?? new List<TestSpot>())
                if (!string.IsNullOrEmpty(s?.Id)) c.Put(s);
            return c;
        }

        public string ToJson()
        {
            var shape = new FileShape { Generated = Generated, Spots = _spots.ToList() };
            return JsonSerializer.Serialize(shape, Options(true));
        }

        /// <summary>Adds or replaces (same id) a spot.</summary>
        public void Put(TestSpot spot)
        {
            int i = _spots.FindIndex(s => string.Equals(s.Id, spot.Id, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _spots[i] = spot;
            else _spots.Add(spot);
        }

        /// <summary>Spots of <paramref name="other"/> replace those with the same id (manual over computed).</summary>
        public void Merge(SpotCatalog other)
        {
            if (other == null) return;
            foreach (var s in other.Spots) Put(s);
        }

        public bool TryGet(string id, out TestSpot spot)
        {
            spot = _spots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            return spot != null;
        }

        /// <summary>Matching spots, nearest first when the query has a point, else by "score" (high first), then id.</summary>
        public IEnumerable<TestSpot> Select(SpotQuery q)
        {
            if (q == null) return _spots;
            IEnumerable<TestSpot> r = _spots.Where(s => s.Pos != null && s.Pos.Length >= 3);
            if (!string.IsNullOrEmpty(q.Id)) r = r.Where(s => string.Equals(s.Id, q.Id, StringComparison.OrdinalIgnoreCase));
            foreach (var t in q.Tags) r = r.Where(s => s.HasTag(t));
            if (q.Near != null)
            {
                r = r.Where(s => Distance(s.Pos, q.Near) <= q.MaxDistance).OrderBy(s => Distance(s.Pos, q.Near)).ThenBy(s => s.Id, StringComparer.Ordinal);
                return r;
            }
            return r.OrderByDescending(s => float.IsNaN(s.Value("score")) ? 0f : s.Value("score")).ThenBy(s => s.Id, StringComparer.Ordinal);
        }

        /// <summary>The first match of a spot reference ("id", "?tag+tag", with optional ".point") and its point.</summary>
        public bool TryResolve(string reference, float[] near, out TestSpot spot, out float[] point)
        {
            point = null;
            var q = SpotQuery.Parse(reference, out var pointName);
            q.Near = q.Id == null ? near : null;
            spot = Select(q).FirstOrDefault();
            if (spot == null && pointName != null)
            {
                // ids may contain dots: retry with the whole reference as the id
                q = SpotQuery.Parse(reference.Replace(".", "\u0001"), out _);
                q.Id = q.Id?.Replace("\u0001", ".");
                spot = Select(q).FirstOrDefault();
                pointName = null;
            }
            return spot != null && spot.TryPoint(pointName, out point);
        }

        /// <summary>Horizontal distance (x, z) in metres.</summary>
        public static float Distance(float[] a, float[] b)
        {
            float bx = b[0], bz = b.Length >= 3 ? b[2] : b[1];
            float dx = a[0] - bx, dz = a[2] - bz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public static string Format(float[] p) =>
            p == null ? "-" : string.Join(",", p.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)));
    }
}
#endif
