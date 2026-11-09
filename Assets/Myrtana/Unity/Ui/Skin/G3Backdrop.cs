using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Skin
{
    /// <summary>
    /// Fills for the sides of 4:3 screens on wider panels: a small, heavily blurred and darkened copy of a sprite
    /// (stretched behind the picture) and a soft horizontal shadow ramp for the picture's edges. Built once per sprite
    /// at runtime: GPU halving blits down to <see cref="Width"/> pixels, then a CPU box blur on that tiny copy.
    /// </summary>
    public static class G3Backdrop
    {
        public const int Width = 64;
        /// <summary>Brightness of the blurred side fill (the loading screen and the menus).</summary>
        public const float SideBrightness = 0.4f;
        /// <summary>Width of the soft shadow outside each side of a 4:3 picture (reference pixels).</summary>
        public const int ShadowWidth = 48;
        const int BlurRadius = 2, BlurPasses = 3;

        static readonly Dictionary<(Sprite, float), Texture2D> Cache = new Dictionary<(Sprite, float), Texture2D>();
        static Texture2D _shadow;

        /// <summary>Blurred copy of <paramref name="sprite"/> (<see cref="Width"/> px wide) at
        /// <paramref name="brightness"/> (0…1); null without a sprite.</summary>
        public static Texture2D Blurred(Sprite sprite, float brightness)
        {
            if (sprite == null) return null;
            if (Cache.TryGetValue((sprite, brightness), out var cached) && cached != null) return cached;

            var tex = sprite.texture;
            var r = sprite.textureRect;
            int w = Mathf.Max(Width, Mathf.CeilToInt(r.width / 2f)), h = Mathf.Max(1, Mathf.RoundToInt(w * r.height / r.width));
            var src = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, src, new Vector2(r.width / tex.width, r.height / tex.height), new Vector2(r.x / tex.width, r.y / tex.height));
            while (w / 2 >= Width)
            {
                w /= 2;
                h = Mathf.Max(1, h / 2);
                var next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, next); // bilinear at half size = 2×2 box filter
                RenderTexture.ReleaseTemporary(src);
                src = next;
            }

            var small = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = sprite.name + " (blurred)" };
            var active = RenderTexture.active;
            RenderTexture.active = src;
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(src);

            var px = small.GetPixels32();
            for (int pass = 0; pass < BlurPasses; pass++) BoxBlur(px, w, h);
            byte k = (byte)Mathf.RoundToInt(Mathf.Clamp01(brightness) * 255f);
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)(px[i].r * k / 255), (byte)(px[i].g * k / 255), (byte)(px[i].b * k / 255), 255);
            small.SetPixels32(px);
            small.wrapMode = TextureWrapMode.Clamp;
            small.filterMode = FilterMode.Bilinear;
            small.Apply(false, true);
            Cache[(sprite, brightness)] = small;
            return small;
        }

        /// <summary>64×1 ramp from black (alpha 1, left) to transparent (right), smooth-stepped; mirror it for the
        /// other side.</summary>
        public static Texture2D ShadowRamp
        {
            get
            {
                if (_shadow != null) return _shadow;
                const int n = 64;
                var px = new Color32[n];
                for (int i = 0; i < n; i++)
                {
                    float t = 1f - i / (n - 1f);
                    px[i] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(t * t * (3f - 2f * t) * 255f));
                }
                _shadow = new Texture2D(n, 1, TextureFormat.RGBA32, false) { name = "G3 shadow ramp", wrapMode = TextureWrapMode.Clamp };
                _shadow.SetPixels32(px);
                _shadow.Apply(false, true);
                return _shadow;
            }
        }

        /// <summary>The soft shadows left and right outside a <paramref name="width"/>×<paramref name="height"/>
        /// picture; add them to the picture's frame before its content. They never take clicks.</summary>
        public static void AddEdgeShadows(VisualElement frame, int width, int height)
        {
            frame.Add(Shadow(-ShadowWidth, height, flip: true));
            frame.Add(Shadow(width, height, flip: false));
        }

        static VisualElement Shadow(int x, int height, bool flip)
        {
            var e = new VisualElement { name = "edge-shadow", pickingMode = PickingMode.Ignore };
            e.style.position = Position.Absolute;
            e.style.left = x;
            e.style.top = 0;
            e.style.width = ShadowWidth;
            e.style.height = height;
            e.style.backgroundImage = ShadowRamp;
            e.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
            if (flip) e.style.scale = new Scale(new Vector3(-1f, 1f, 1f));
            return e;
        }

        static void BoxBlur(Color32[] px, int w, int h)
        {
            var tmp = new Color32[px.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tmp[y * w + x] = Average(px, w, h, x, y, 1, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = Average(tmp, w, h, x, y, 0, 1);
        }

        static Color32 Average(Color32[] px, int w, int h, int x, int y, int dx, int dy)
        {
            int r = 0, g = 0, b = 0;
            for (int k = -BlurRadius; k <= BlurRadius; k++)
            {
                int sx = Mathf.Clamp(x + k * dx, 0, w - 1), sy = Mathf.Clamp(y + k * dy, 0, h - 1);
                var c = px[sy * w + sx];
                r += c.r; g += c.g; b += c.b;
            }
            const int n = 2 * BlurRadius + 1;
            return new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
        }
    }
}
