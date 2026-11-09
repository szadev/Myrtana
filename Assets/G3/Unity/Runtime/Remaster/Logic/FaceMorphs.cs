using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// The 52 ARKit face blend shapes (Apple ARFaceAnchor names, the set MetaHuman's ARKit mapping and most DCC
    /// exporters bake as morph targets). Docs/remaster-metahuman.md §5: the MetaHuman hero head reaches Unity as a
    /// skinned mesh with these morph targets instead of the RigLogic joint rig.
    /// </summary>
    public static class ArkitShapes
    {
        public static readonly string[] Names =
        {
            "eyeBlinkLeft", "eyeLookDownLeft", "eyeLookInLeft", "eyeLookOutLeft", "eyeLookUpLeft", "eyeSquintLeft", "eyeWideLeft",
            "eyeBlinkRight", "eyeLookDownRight", "eyeLookInRight", "eyeLookOutRight", "eyeLookUpRight", "eyeSquintRight", "eyeWideRight",
            "jawForward", "jawLeft", "jawRight", "jawOpen",
            "mouthClose", "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight", "mouthSmileLeft", "mouthSmileRight",
            "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight", "mouthStretchLeft", "mouthStretchRight",
            "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper", "mouthPressLeft", "mouthPressRight",
            "mouthLowerDownLeft", "mouthLowerDownRight", "mouthUpperUpLeft", "mouthUpperUpRight",
            "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
            "cheekPuff", "cheekSquintLeft", "cheekSquintRight", "noseSneerLeft", "noseSneerRight", "tongueOut",
        };

        /// <summary>Shapes a lip-sync table may use (mouth, jaw, and the brows G3's visemes key).</summary>
        public static readonly string[] Speech =
        {
            "jawOpen", "jawForward", "mouthClose", "mouthFunnel", "mouthPucker", "mouthSmileLeft", "mouthSmileRight",
            "mouthStretchLeft", "mouthStretchRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
            "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthUpperUpLeft",
            "mouthUpperUpRight", "browInnerUp", "browDownLeft", "browDownRight",
        };

        /// <summary>
        /// Index of <paramref name="shape"/> among <paramref name="meshShapes"/> (a mesh's blend shape names), matching the
        /// ARKit name exactly or as the suffix after a namespace / prefix ("head_lod0_mesh.jawOpen", "ARKit_jawOpen",
        /// "CTRL_expressions_jawOpen" style exports), case-insensitive; -1 when missing.
        /// </summary>
        public static int Find(IReadOnlyList<string> meshShapes, string shape)
        {
            if (meshShapes == null || string.IsNullOrEmpty(shape)) return -1;
            int suffix = -1;
            for (int i = 0; i < meshShapes.Count; i++)
            {
                var n = meshShapes[i];
                if (string.IsNullOrEmpty(n)) continue;
                if (string.Equals(n, shape, StringComparison.OrdinalIgnoreCase)) return i;
                if (suffix < 0 && n.Length > shape.Length && n.EndsWith(shape, StringComparison.OrdinalIgnoreCase))
                {
                    char sep = n[n.Length - shape.Length - 1];
                    if (sep == '.' || sep == '_' || sep == ':' || sep == '|') suffix = i;
                }
            }
            return suffix;
        }
    }

    /// <summary>
    /// G3 visemes as blend shape weights: for every viseme (G3 1…13, 1 = closed) the weight 0…1 of each shape in
    /// <see cref="Shapes"/>. Evaluated like the bone kit (<c>G3TalkingFace.SetMouth</c>): viseme from → to by weight,
    /// applied by amount (0 = the face as animated, 1 = the full viseme).
    /// </summary>
    [Serializable]
    public sealed class VisemeMorphTable
    {
        public string[] Shapes = Array.Empty<string>();
        /// <summary>Row-major [viseme, shape].</summary>
        public float[] Weights = Array.Empty<float>();
        public int VisemeCount;

        public VisemeMorphTable() { }

        public VisemeMorphTable(string[] shapes, int visemeCount)
        {
            Shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
            VisemeCount = visemeCount;
            Weights = new float[visemeCount * shapes.Length];
        }

        public float Get(int viseme, int shape) => Weights[viseme * Shapes.Length + shape];
        public void Set(int viseme, int shape, float w) => Weights[viseme * Shapes.Length + shape] = Clamp01(w);

        /// <summary>Writes the blended weights (0…1) of all <see cref="Shapes"/> into <paramref name="result"/>.</summary>
        public void Evaluate(int from, int to, float weight, float amount, float[] result)
        {
            if (result == null || result.Length < Shapes.Length) throw new ArgumentException("result too short", nameof(result));
            int n = Shapes.Length;
            if (VisemeCount <= 0 || n == 0) { Array.Clear(result, 0, n); return; }
            int last = VisemeCount - 1;
            from = from < 0 ? 0 : from > last ? last : from;
            to = to < 0 ? 0 : to > last ? last : to;
            float t = Clamp01(weight), a = Clamp01(amount);
            for (int s = 0; s < n; s++)
            {
                float w0 = Weights[from * n + s], w1 = Weights[to * n + s];
                result[s] = (w0 + (w1 - w0) * t) * a;
            }
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Text form (<c>&lt;head&gt;.visemes.txt</c> beside the remaster .glb): <c>visemes 1</c>, <c>shapes a b c</c>, then
        /// one line per viseme <c>&lt;index&gt; w_a w_b w_c</c> (invariant culture, missing rows = 0, <c>#</c> comments).
        /// </summary>
        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("visemes 1\nshapes ").Append(string.Join(" ", Shapes)).Append('\n');
            for (int v = 0; v < VisemeCount; v++)
            {
                sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
                for (int s = 0; s < Shapes.Length; s++)
                    sb.Append(' ').Append(Get(v, s).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static VisemeMorphTable Parse(string text)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string[] shapes = null;
            var rows = new List<(int v, float[] w)>();
            int lineNo = 0;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                lineNo++;
                var line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                if (line.Length == 0) continue;
                var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f[0] == "visemes") { if (f.Length < 2 || f[1] != "1") throw new FormatException($"line {lineNo}: unknown version"); continue; }
                if (f[0] == "shapes") { shapes = new string[f.Length - 1]; Array.Copy(f, 1, shapes, 0, shapes.Length); continue; }
                if (shapes == null) throw new FormatException($"line {lineNo}: row before 'shapes'");
                if (f.Length != shapes.Length + 1) throw new FormatException($"line {lineNo}: {f.Length - 1} weights for {shapes.Length} shapes");
                int v = int.Parse(f[0], ci);
                if (v < 0 || v > 255) throw new FormatException($"line {lineNo}: viseme {v}");
                var w = new float[shapes.Length];
                for (int s = 0; s < w.Length; s++) w[s] = float.Parse(f[s + 1], System.Globalization.NumberStyles.Float, ci);
                rows.Add((v, w));
            }
            if (shapes == null) throw new FormatException("no 'shapes' line");
            int count = 0;
            foreach (var r in rows) count = Math.Max(count, r.v + 1);
            var t = new VisemeMorphTable(shapes, count);
            foreach (var r in rows) for (int s = 0; s < shapes.Length; s++) t.Set(r.v, s, r.w[s]);
            return t;
        }
    }

    /// <summary>
    /// Mouth features of one face pose, measured the same way on the G3 facial head (viseme poses of the face bones)
    /// and on the MetaHuman head (blend shape deltas), in head space, relative to the rest pose (Tools/Remaster/metahuman/mh_visemes.py).
    /// <see cref="VisemeFit"/> matches them. Scale differences between the two heads are taken out by the caller
    /// (divide by the mouth width at rest).
    /// </summary>
    public static class MouthFeatures
    {
        public const int JawOpen = 0;      // chin drop
        public const int LipGap = 1;       // vertical distance upper lip centre - lower lip centre
        public const int Width = 2;        // mouth corner to corner
        public const int Protrusion = 3;   // lip centres forward (pucker / funnel)
        public const int UpperRaise = 4;   // upper lip centre up
        public const int LowerDrop = 5;    // lower lip centre down, relative to the jaw
        public const int CornerRaise = 6;  // mouth corners up (smile) / down (frown)
        public const int BrowRaise = 7;    // brows up / down
        public const int Count = 8;

        /// <summary>Default weights of the features in the fit (jaw and lip gap carry speech; brows barely matter).</summary>
        public static readonly float[] Importance = { 1.0f, 1.5f, 1.0f, 1.0f, 0.6f, 0.6f, 0.5f, 0.25f };
    }

    /// <summary>
    /// Fits a <see cref="VisemeMorphTable"/>: per viseme, the shape weights in [0, 1] whose summed feature responses best
    /// match the G3 viseme's features (box-constrained ridge least squares, coordinate descent). Shapes are assumed to
    /// add linearly (true for morph targets).
    /// </summary>
    public static class VisemeFit
    {
        /// <param name="targets">[viseme][feature] of the G3 head.</param>
        /// <param name="responses">[shape][feature] of the MetaHuman head at weight 1.</param>
        /// <param name="importance">Per-feature weight (null = <see cref="MouthFeatures.Importance"/> when the length fits, else 1).</param>
        /// <param name="ridge">Small penalty that keeps unneeded shapes at 0.</param>
        public static VisemeMorphTable Fit(string[] shapes, float[][] targets, float[][] responses, float[] importance = null,
                                           float ridge = 1e-3f, int sweeps = 400)
        {
            if (shapes == null || targets == null || responses == null) throw new ArgumentNullException();
            if (responses.Length != shapes.Length) throw new ArgumentException("one response per shape");
            var table = new VisemeMorphTable(shapes, targets.Length);
            int f = targets.Length > 0 ? targets[0].Length : 0;
            var imp = importance ?? (f == MouthFeatures.Count ? MouthFeatures.Importance : null);
            var a = new float[f, shapes.Length];
            for (int s = 0; s < shapes.Length; s++)
                for (int k = 0; k < f; k++)
                    a[k, s] = responses[s][k] * (imp != null ? imp[k] : 1f);
            for (int v = 0; v < targets.Length; v++)
            {
                var b = new float[f];
                for (int k = 0; k < f; k++) b[k] = targets[v][k] * (imp != null ? imp[k] : 1f);
                var w = BoxLeastSquares(a, b, 0f, 1f, ridge, sweeps);
                for (int s = 0; s < shapes.Length; s++) table.Set(v, s, w[s]);
            }
            return table;
        }

        /// <summary>min |A w − b|² + ridge |w|², lo ≤ w ≤ hi, by cyclic coordinate descent (exact per coordinate).</summary>
        public static float[] BoxLeastSquares(float[,] a, float[] b, float lo, float hi, float ridge, int sweeps)
        {
            int m = a.GetLength(0), n = a.GetLength(1);
            var w = new float[n];
            var r = (float[])b.Clone(); // residual b − A w (w = 0)
            var col = new float[n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < m; i++) col[j] += a[i, j] * a[i, j];
            for (int it = 0; it < sweeps; it++)
            {
                float change = 0;
                for (int j = 0; j < n; j++)
                {
                    if (col[j] <= 0) continue;
                    float g = 0;
                    for (int i = 0; i < m; i++) g += a[i, j] * r[i];
                    float nw = (g + col[j] * w[j]) / (col[j] + ridge);
                    nw = nw < lo ? lo : nw > hi ? hi : nw;
                    float d = nw - w[j];
                    if (d == 0) continue;
                    for (int i = 0; i < m; i++) r[i] -= a[i, j] * d;
                    w[j] = nw;
                    change = Math.Max(change, Math.Abs(d));
                }
                if (change < 1e-6f) break;
            }
            return w;
        }
    }

    /// <summary>
    /// Eye blinks for a face with blend shapes (G3 heads never blink; the MetaHuman head would stare without it):
    /// a blink every <see cref="MinGap"/>…<see cref="MaxGap"/> s, sometimes a double blink, close fast, open slower.
    /// Deterministic for a seed, engine-free; <see cref="Sample"/> gives the closure 0…1 at time t (s, monotonic).
    /// </summary>
    public sealed class BlinkSchedule
    {
        public const float MinGap = 2.2f, MaxGap = 6.5f, DoubleChance = 0.15f, DoubleGap = 0.28f;
        public const float Close = 0.07f, Hold = 0.03f, Open = 0.13f;
        public const float Duration = Close + Hold + Open;

        uint _state;
        float _next, _second = -1f;

        public BlinkSchedule(uint seed, float start = 0f)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
            _next = start + Range(0.4f, MaxGap);
        }

        /// <summary>Lid closure (0 open, 1 closed) at <paramref name="time"/>; call with non-decreasing times.</summary>
        public float Sample(float time)
        {
            while (true)
            {
                float start = _second >= 0 ? Math.Min(_second, _next) : _next;
                if (time < start) return 0f;
                float u = time - start;
                if (u < Duration) return Shape(u);
                // that blink is over: schedule the following one
                if (_second >= 0 && start == _second) _second = -1f;
                else
                {
                    if (Range(0f, 1f) < DoubleChance) _second = _next + DoubleGap;
                    _next += Range(MinGap, MaxGap);
                }
            }
        }

        /// <summary>Closure over one blink, u in [0, <see cref="Duration"/>).</summary>
        public static float Shape(float u)
        {
            if (u < 0f) return 0f;
            if (u < Close) { float x = u / Close; return x * x * (3f - 2f * x); }
            if (u < Close + Hold) return 1f;
            if (u < Duration) { float x = 1f - (u - Close - Hold) / Open; return x * x * (3f - 2f * x); }
            return 0f;
        }

        float Range(float lo, float hi)
        {
            // xorshift32
            _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
            return lo + (hi - lo) * ((_state & 0xFFFFFF) / (float)0x1000000);
        }
    }
}
