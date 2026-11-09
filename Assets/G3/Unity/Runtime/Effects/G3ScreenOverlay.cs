using System.Collections.Generic;
using G3.Effects;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Draws the sprites of screen parts (<see cref="G3Vfx.screen"/>: soul travel ghost/load, Dusk to Dawn, Time Bubble,
    /// Heaven Zap) as G3 shows them: camera-facing sprites without depth test or fog, each projected onto a plane just behind
    /// the near plane so the camera inside a 20–140 m sprite sees its texture window over the whole view (a full-screen tint;
    /// <see cref="ScreenSpriteRules"/>), rolled by their spin. Uses the part's own imported material (texture, Brighten =
    /// additive) with the screen colour gain (<see cref="ScreenSpriteRules.ScreenGain"/>) and its colour/size-over-life curves. One mesh per material, rebuilt each frame (a few dozen quads at most).
    /// </summary>
    public sealed class G3ScreenOverlay
    {
        sealed class Batch
        {
            public Mesh mesh;
            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Color> colors = new List<Color>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> indices = new List<int>();
        }

        readonly Dictionary<Material, Batch> _batches = new Dictionary<Material, Batch>();
        static readonly int TintId = Shader.PropertyToID("_Tint");
        MaterialPropertyBlock _props; // the screen colour gain instead of the material's additive gain

        public void Draw(Camera cam, List<G3Vfx> parts)
        {
            if (cam == null || parts == null || parts.Count == 0) return;
            foreach (var b in _batches.Values) { b.vertices.Clear(); b.colors.Clear(); b.uvs.Clear(); b.indices.Clear(); }
            var camT = cam.transform;
            float plane = Mathf.Min(cam.nearClipPlane * 2f, cam.farClipPlane * 0.5f);
            foreach (var p in parts)
            {
                var system = p.system;
                var renderer = system != null ? system.GetComponent<ParticleSystemRenderer>() : null;
                var mat = renderer != null ? renderer.sharedMaterial : null;
                if (mat == null) continue;
                if (!_batches.TryGetValue(mat, out var batch)) _batches[mat] = batch = new Batch();
                var main = system.main;
                var col = system.colorOverLifetime;
                var size = system.sizeOverLifetime;
                var sheet = system.textureSheetAnimation;
                int tilesX = sheet.enabled ? Mathf.Max(1, sheet.numTilesX) : 1, tilesY = sheet.enabled ? Mathf.Max(1, sheet.numTilesY) : 1;
                Color start = main.startColor.color;
                var t = p.transform;
                foreach (var sp in p.ScreenSprites)
                {
                    float life01 = Mathf.Clamp01(sp.age / sp.life);
                    var c = start;
                    if (col.enabled) c *= col.color.Evaluate(life01);
                    if (c.a <= 0.001f) continue;
                    float s = sp.size * (size.enabled ? size.size.Evaluate(life01) : 1f);
                    var world = sp.local ? t.TransformPoint(sp.position) : sp.position;
                    var q = camT.InverseTransformPoint(world);
                    if (!ScreenSpriteRules.ProjectToPlane(q.x, q.y, q.z, s, plane, out float cx, out float cy, out float half)) continue;
                    int frame = Mathf.Min(tilesX * tilesY - 1, (int)(sp.seed * tilesX * tilesY));
                    float u0 = (frame % tilesX) / (float)tilesX, v0 = (frame / tilesX) / (float)tilesY;
                    float du = 1f / tilesX, dv = 1f / tilesY;
                    int i0 = batch.vertices.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        ScreenSpriteRules.Corner(cx, cy, half, sp.spin, k, out float vx, out float vy);
                        batch.vertices.Add(new Vector3(vx, vy, plane));
                    }
                    batch.uvs.Add(new Vector2(u0, v0));
                    batch.uvs.Add(new Vector2(u0, v0 + dv));
                    batch.uvs.Add(new Vector2(u0 + du, v0 + dv));
                    batch.uvs.Add(new Vector2(u0 + du, v0));
                    for (int k = 0; k < 4; k++) batch.colors.Add(c);
                    batch.indices.Add(i0); batch.indices.Add(i0 + 1); batch.indices.Add(i0 + 2);
                    batch.indices.Add(i0); batch.indices.Add(i0 + 2); batch.indices.Add(i0 + 3);
                }
            }
            var toWorld = camT.localToWorldMatrix;
            var bounds = new Bounds(camT.position, Vector3.one * (plane * 4f + 1f));
            foreach (var kv in _batches)
            {
                var b = kv.Value;
                if (b.vertices.Count == 0) continue;
                if (b.mesh == null) b.mesh = new Mesh { name = "G3 screen overlay" };
                b.mesh.MarkDynamic();
                b.mesh.Clear();
                b.mesh.SetVertices(b.vertices);
                b.mesh.SetColors(b.colors);
                b.mesh.SetUVs(0, b.uvs);
                b.mesh.SetTriangles(b.indices, 0, false);
                b.mesh.bounds = new Bounds(Vector3.forward * plane, Vector3.one * (plane * 4f + 1f));
                if (_props == null)
                {
                    _props = new MaterialPropertyBlock();
                    float g = ScreenSpriteRules.ScreenGain;
                    _props.SetColor(TintId, new Color(g, g, g, 1f));
                }
                var rp = new RenderParams(kv.Key)
                {
                    camera = cam, worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                    layer = 0, matProps = _props,
                };
                Graphics.RenderMesh(rp, b.mesh, 0, toWorld);
            }
        }

        public void Dispose()
        {
            foreach (var b in _batches.Values) if (b.mesh != null) Object.Destroy(b.mesh);
            _batches.Clear();
        }
    }
}
