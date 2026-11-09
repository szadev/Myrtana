using System;
using System.IO;
using System.Linq;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Pak;

/// `G3Cli selfillum [filter] [pngDir]` (fix-260): the SelfIllumination glow recipe of every material whose graph is a
/// supported glow (SelfIlluminationMap), the weight channel's coverage and the baked map's mean (gamma); pngDir also
/// writes the baked maps.
static partial class Program
{
    static int SelfIllumCommand(string data, string[] args)
    {
        string filter = args.Length > 1 ? args[1] : null, pngDir = args.Length > 2 ? args[2] : null;
        using var fs = new G3FileSystem(data, "_compiledMaterial", "_compiledImage");
        SelfIlluminationMap.Rgba Load(TextureSlot s)
        {
            if (s == null) return null;
            var e = fs.GetByName(s.Image + ".ximg");
            if (e == null) return null;
            var img = Ximg.Read(e.Read());
            return new SelfIlluminationMap.Rgba(Dxt.DecodeRgba(img.Mips[0], img.Width, img.Height, img.Format), img.Width, img.Height);
        }
        foreach (var e in fs.Files.Where(e => e.Path.EndsWith(".xshmat", StringComparison.OrdinalIgnoreCase) &&
                                              (filter == null || e.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(e => e.Path))
        {
            MaterialResource m;
            try { m = Xshmat.Read(e.Read(), Path.GetFileNameWithoutExtension(e.Path)); } catch { continue; }
            var r = m.Emission;
            if (r == null) continue;
            string recipe = r.Base != null ? $"lerp({r.Glow.Image}, {r.Base.Image}, {r.Weight.Image}.{r.WeightChannel})" : r.Glow.Image;
            if (r.Color.Any(c => c != 1)) recipe += $" * ({string.Join(",", r.Color.Select(c => c.ToString("0.##")))})";
            var glow = Load(r.Glow);
            var baseImg = Load(r.Base);
            var weight = Load(r.Weight);
            if (glow == null || (r.Base != null && (baseImg == null || weight == null)))
            {
                Console.WriteLine($"{m.Name}\t{recipe}\tMISSING IMAGE");
                continue;
            }
            int w = Math.Min(1024, Math.Max(glow.Width, baseImg?.Width ?? 0)), h = Math.Min(1024, Math.Max(glow.Height, baseImg?.Height ?? 0));
            var px = r.Bake(glow, baseImg, weight, w, h);
            double[] mean = new double[3];
            for (int i = 0; i < px.Length; i += 4) for (int k = 0; k < 3; k++) mean[k] += px[i + k];
            int n = px.Length / 4;
            string cov = "";
            if (weight != null)
            {
                int ch = r.WeightChannel switch { ColorComponent.R => 0, ColorComponent.G => 1, ColorComponent.B => 2, _ => 3 };
                int low = 0;
                for (int i = ch; i < weight.Pixels.Length; i += 4) if (weight.Pixels[i] < 128) low++;
                cov = $"\tglow share {100.0 * low / (weight.Width * weight.Height):0.0}%";
            }
            Console.WriteLine($"{m.Name}\t{recipe}\tbaked {w}x{h} mean {string.Join(",", mean.Select(s => (s / n / 255).ToString("0.000")))}{cov}");
            if (pngDir != null)
            {
                Directory.CreateDirectory(pngDir);
                File.WriteAllBytes(Path.Combine(pngDir, r.BakedStem + ".png"), Png.Encode(px, w, h));
            }
        }
        return 0;
    }
}
