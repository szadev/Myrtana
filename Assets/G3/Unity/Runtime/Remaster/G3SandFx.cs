using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// remaster-sand (sand-2): draws the procedural sand effects (wisps <see cref="G3SandWisps"/>, dust puffs
    /// <see cref="G3SandDust"/>) in a render pass of the game camera after the transparents - one DrawProcedural per effect
    /// (vertices built from SV_VertexID). sand-2: Graphics.RenderPrimitives never reached the image (wisps and puffs were
    /// invisible in every capture); a pass enqueued per camera always does. Effects register their draw each frame
    /// (<see cref="Draw"/>); nothing is enqueued while none did.
    /// </summary>
    public static class G3SandFx
    {
        struct Item { public Material mat; public int vertices; }

        static readonly List<Item> _items = new List<Item>(4);
        static readonly List<Item> _frame = new List<Item>(4);
        static int _frameNo = -1;
        static bool _hooked;
        static FxPass _pass;

        /// <summary>This frame: draw <paramref name="vertices"/> procedural vertices with pass 0 of <paramref name="mat"/>.</summary>
        public static void Draw(Material mat, int vertices)
        {
            if (mat == null || vertices <= 0) return;
            if (_frameNo != Time.frameCount) { _frameNo = Time.frameCount; _items.Clear(); }
            _items.Add(new Item { mat = mat, vertices = vertices });
            if (!_hooked) { RenderPipelineManager.beginCameraRendering += OnBeginCamera; _hooked = true; }
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_frameNo != Time.frameCount && _frameNo != Time.frameCount - 1)
            {
                // nothing registered lately: unhook until an effect draws again
                RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
                _hooked = false;
                _items.Clear();
                return;
            }
            if (_items.Count == 0 || cam.cameraType != CameraType.Game || cam.cullingMask == 0) return;
            if (G3GpuSafe.Capturing) return;   // dx12-4: the save picture renders without the sand fx (dx12-3 capture rule)
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null || data.renderType != CameraRenderType.Base) return;
            _pass ??= new FxPass();
            data.scriptableRenderer.EnqueuePass(_pass);
        }

        internal static void Release()
        {
            if (_hooked) RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            _hooked = false;
            _items.Clear();
        }

        sealed class FxPass : ScriptableRenderPass
        {
            sealed class PassData { public List<Item> items; public Matrix4x4 view, proj; }

            public FxPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents - 2;   // before the heat haze (-1) and the grade
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                var camData = frameData.Get<UniversalCameraData>();
                _frame.Clear();
                // dx12-4: an effect released (material destroyed) after it registered this frame is skipped, not drawn
                foreach (var it in _items) if (it.mat != null && it.vertices > 0) _frame.Add(it);
                if (_frame.Count == 0 || !res.activeColorTexture.IsValid() || !res.activeDepthTexture.IsValid()) return;
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("G3 Sand Fx", out var pd))
                {
                    pd.items = _frame;
                    pd.view = camData.GetViewMatrix();
                    pd.proj = GL.GetGPUProjectionMatrix(camData.GetProjectionMatrix(), true);
                    builder.SetRenderAttachment(res.activeColorTexture, 0);
                    builder.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Read);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData d, RasterGraphContext c) =>
                    {
                        // the camera's matrices again: the full-screen copies before (atmosphere, light ratio) leave others set
                        RenderingUtils.SetViewAndProjectionMatrices(c.cmd, d.view, d.proj, true);
                        foreach (var it in d.items)
                            if (it.mat != null) c.cmd.DrawProcedural(Matrix4x4.identity, it.mat, 0, MeshTopology.Triangles, it.vertices);
                    });
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            _hooked = false; _items.Clear(); _frame.Clear(); _frameNo = -1; _pass = null;
        }
    }
}
