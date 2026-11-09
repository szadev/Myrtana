using System;
using G3.Formats.Image;
using G3.Formats.Material;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Bakes a composite SelfIllumination recipe (<see cref="SelfIlluminationMap"/>, e.g. the fire golem's
    /// lerp(lava, rock diffuse, rock alpha)) into one sRGB texture for the URP emission slot (fix-260).
    /// </summary>
    public static class SelfIlluminationTextures
    {
        /// <param name="image">Image stem → decoded game image, null if missing.</param>
        public static Texture2D Bake(SelfIlluminationMap recipe, Func<string, ImageResource> image, out string error)
        {
            string missing = null;
            SelfIlluminationMap.Rgba Load(TextureSlot s)
            {
                if (s == null) return null;
                var img = image(s.Image);
                if (img == null) { missing ??= $"image not found: {s.Image}"; return null; }
                return new SelfIlluminationMap.Rgba(Dxt.DecodeRgba(img.Mips[0], img.Width, img.Height, img.Format), img.Width, img.Height);
            }
            var glow = Load(recipe.Glow);
            var baseImg = Load(recipe.Base);
            var weight = Load(recipe.Weight);
            error = missing;
            if (error != null) return null;
            int w = SelfIlluminationMap.BakeSize(glow.Width, baseImg?.Width ?? 0), h = SelfIlluminationMap.BakeSize(glow.Height, baseImg?.Height ?? 0);
            var px = recipe.Bake(glow, baseImg, weight, w, h);
            // G3 images are stored top row first and imported as such (the meshes' V matches); keep the same order.
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = recipe.BakedStem };
            tex.SetPixelData(px, 0);
            tex.Apply(true, false);
            EditorUtility.CompressTexture(tex, TextureFormat.DXT1, TextureCompressionQuality.Normal);
            return tex;
        }
    }
}
