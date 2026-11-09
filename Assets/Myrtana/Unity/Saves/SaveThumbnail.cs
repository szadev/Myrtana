using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Myrtana.Unity
{
    /// <summary>
    /// The picture of a save game: G3 renders the view into a 128×128 image when it saves
    /// (gCWorld::CreateSaveGameImageFromBB, A8R8G8B8) and shows it in the save/load list. Here the main camera renders the
    /// world without the HUD and the menu into a texture of the screen's aspect, 128 px high, whose centre square is kept
    /// and stored as a JPEG next to the save (<see cref="SaveSlots.ThumbnailPathOf"/>).
    /// </summary>
    public static class SaveThumbnail
    {
        public const int Size = 128;

        /// <summary>The JPEG bytes of the camera's view, or null when there is no camera or rendering fails.</summary>
        public static byte[] Capture(Camera camera) => Encode(CapturePixels(camera, out _));

        /// <summary>JPEG quality of the pictures.</summary>
        public const int Quality = 85;

        /// <summary>
        /// fix-251: the main-thread half: the camera's view as raw RGB24 pixels (<see cref="Size"/>², rows bottom-up, as
        /// Texture2D data), or null; <paramref name="renderMs"/> = the render and read-back time. <see cref="Encode"/> may
        /// run on a worker (the JPEG encode was most of the picture's 35-90 ms on the main thread).
        /// </summary>
        public static byte[] CapturePixels(Camera camera, out double renderMs)
        {
            renderMs = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { return CaptureRaw(camera); }
            finally { renderMs = watch.Elapsed.TotalMilliseconds; }
        }

        /// <summary>The JPEG of <see cref="CapturePixels"/>' pixels (any thread), or null.</summary>
        public static byte[] Encode(byte[] rgb24)
        {
            if (rgb24 == null) return null;
            try { return ImageConversion.EncodeArrayToJPG(rgb24, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8_SRGB, Size, Size, 0, Quality); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Myrtana] save picture encode failed: {ex.Message}");
                return null;
            }
        }

        static byte[] CaptureRaw(Camera camera)
        {
            if (camera == null || !camera.isActiveAndEnabled) return null;
            int width = Mathf.Max(Size, Mathf.RoundToInt(Size * Mathf.Max(1f, camera.aspect)));
            var target = RenderTexture.GetTemporary(width, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var active = RenderTexture.active;
            Texture2D picture = null;
            // dx12-3: the per-camera GPU extras (grass v2 / scatter indirect culls, GI, exposure, temporal histories) sit
            // this render out: run from Update they would run twice per frame at this size and queue indirect draws that
            // outlive the capture (D3D12 DEVICE_HUNG right after a save in build 25b)
            G3.Unity.G3GpuSafe.BeginCapture("save picture");
            try
            {
                var request = new RenderPipeline.StandardRequest { destination = target };
                if (RenderPipeline.SupportsRenderRequest(camera, request)) RenderPipeline.SubmitRenderRequest(camera, request);
                else
                {
                    var before = camera.targetTexture;
                    camera.targetTexture = target;
                    camera.Render();
                    camera.targetTexture = before;
                }
                RenderTexture.active = target;
                picture = new Texture2D(Size, Size, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect((width - Size) / 2, 0, Size, Size), 0, 0);
                return picture.GetRawTextureData();   // the CPU copy ReadPixels filled (no GPU upload needed)
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Myrtana] save picture failed: {ex.Message}");
                return null;
            }
            finally
            {
                G3.Unity.G3GpuSafe.EndCapture("save picture");
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                if (picture != null) UnityEngine.Object.Destroy(picture);
            }
        }
    }
}
