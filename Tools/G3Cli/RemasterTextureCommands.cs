using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Formats.Remaster;

static partial class Program
{
    /// <summary>
    /// R1 texture sources (Docs/remaster-pipeline.md §9):
    ///   remaster mats &lt;actor|mesh stem&gt;... [--switch n]       materials and the images they use (diffuse/normal/specular)
    ///   remaster textures &lt;outDir&gt; &lt;actor|mesh stem&gt;... [--switch n]   those images as PNG + sources.json (pipeline input)
    /// Image choice follows G3CharacterImporter.GetMaterial: the .xshmat's images when its diffuse equals the FXA layer
    /// (bodies), else the FXA material layers (heads: per-head textures). Normal maps are written as plain tangent-space
    /// RGB in Unity's convention (X from DXT5nm alpha, Y flipped to "up", Z rebuilt).
    /// </summary>
    static int RemasterTextureCommand(string data, string[] args)
    {
        var rest = args.Skip(2).ToList();
        int sw = 0;
        int si = rest.IndexOf("--switch");
        if (si >= 0) { sw = int.Parse(rest[si + 1]); rest.RemoveRange(si, 2); }
        string outDir = null;
        if (args[1] == "textures") { outDir = rest[0]; rest.RemoveAt(0); }

        using var fs = new G3FileSystem(data, "_compiledAnimation", "_compiledMaterial", "_compiledImage", "_compiledMesh");
        var byStem = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in fs.Files) byStem[Path.GetFileName(e.Path)] = e;

        var sources = new List<RemasterTextureSet>();
        foreach (var part in rest)
            foreach (var set in PartTextures(byStem, part, sw))
            {
                Console.WriteLine($"{set.Part,-40} {set.Material,-44} D={set.Diffuse ?? "-"} N={set.Normal ?? "-"} S={set.Specular ?? "-"} kind={set.Kind}");
                if (!sources.Any(s => s.Diffuse == set.Diffuse && s.Material == set.Material)) sources.Add(set);
            }
        if (outDir == null) return 0;

        Directory.CreateDirectory(outDir);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            Export(s.Diffuse, false); Export(s.Normal, true); Export(s.Specular, false);
        }
        File.WriteAllText(Path.Combine(outDir, "sources.json"), RemasterTextureSet.ToJson(sources));
        Console.WriteLine($"{done.Count} images, {sources.Count} materials -> {outDir}");
        return 0;

        void Export(string image, bool normal)
        {
            if (image == null || !done.Add(image)) return;
            if (!byStem.TryGetValue(image + ".ximg", out var e)) { Console.WriteLine($"missing image {image}"); return; }
            var img = Ximg.Read(e.Read());
            var rgba = DecodeDxtAny(img.Mips[0], img.Width, img.Height, img.Format);
            if (normal) DxtNormalToRgb(rgba, img.Format == PixelFormat.DXT5);
            string path = Path.Combine(outDir, image + ".png");
            WritePng(path, rgba, img.Width, img.Height);
            Console.WriteLine($"  {image}.png {img.Width}x{img.Height} {img.Format}");
        }
    }

    /// <summary>Genome normal map texels → plain RGB normal, Unity (Y up) convention.</summary>
    static void DxtNormalToRgb(byte[] rgba, bool dxt5nm)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            double x = (dxt5nm ? rgba[i + 3] : rgba[i]) / 255.0 * 2 - 1;
            double y = -(rgba[i + 1] / 255.0 * 2 - 1);
            double z = Math.Sqrt(Math.Max(0, 1 - x * x - y * y));
            rgba[i] = (byte)Math.Round((x + 1) * 127.5);
            rgba[i + 1] = (byte)Math.Round((y + 1) * 127.5);
            rgba[i + 2] = (byte)Math.Round((z + 1) * 127.5);
            rgba[i + 3] = 255;
        }
    }

    static IEnumerable<RemasterTextureSet> PartTextures(Dictionary<string, PakEntry> byStem, string part, int sw)
    {
        string Resolve(string img)
        {
            if (img == null) return null;
            string b = System.Text.RegularExpressions.Regex.Replace(img, @"_(S\d+|\d\d)$", "");
            var cands = sw > 0 ? new[] { $"{b}_S{sw}", img, $"{b}_S1", $"{b}_01" } : new[] { img, $"{b}_S1", $"{b}_01" };
            return cands.FirstOrDefault(c => byStem.ContainsKey(c + ".ximg"));
        }
        MaterialResource Shader(string xsh)
        {
            if (xsh == null || !byStem.TryGetValue(xsh + ".xshmat", out var xe)) return null;
            try { return Xshmat.Read(xe.Read(), xsh); } catch { return null; }
        }
        RemasterTextureSet FromShader(string p, string name, MaterialResource m) => new RemasterTextureSet
        {
            Part = p, Material = name, Diffuse = Resolve(m?.Diffuse?.Image), Normal = Resolve(m?.Normal?.Image),
            Specular = Resolve(m?.Specular?.Image ?? m?.SpecularMask?.Map?.Image),
            SpecularChannel = m?.SpecularMask?.Map != null ? m.SpecularMask.MapComponent : ColorComponent.Default,
            Masked = m != null && m.Blend != BlendMode.Normal, Kind = RemasterMaterialKinds.Classify(name, m?.Diffuse?.Image, m?.Specular?.Image ?? m?.SpecularMask?.Map?.Image),
        };

        if (byStem.TryGetValue(part + ".xact", out var ae))
        {
            var act = Xact.Read(ae.Read(), part).Actor;
            var ids = act.Meshes.Where(m => !m.IsCollisionMesh).SelectMany(m => m.SubMeshes).Select(s => s.MaterialId).Distinct();
            foreach (int id in ids)
            {
                string xsh = act.MaterialRefs.Where(r => r.MaterialIndex == id && !string.IsNullOrEmpty(r.Name))
                                .Select(r => Path.GetFileNameWithoutExtension(r.Name)).FirstOrDefault();
                var mc = id >= 0 && id < act.Materials.Count ? act.Materials[id] : null;
                string layerD = mc?.Layers.FirstOrDefault(l => l.MapType == 1)?.Texture;
                var shared = Shader(xsh);
                if (shared != null && (layerD == null || string.Equals(shared.Diffuse?.Image, layerD, StringComparison.OrdinalIgnoreCase) || Resolve(layerD) == null))
                {
                    yield return FromShader(part, xsh, shared);
                    continue;
                }
                string name = (mc?.Name ?? $"Mat{id}").Replace("|", "");
                yield return new RemasterTextureSet
                {
                    Part = part, Material = name,
                    Diffuse = Resolve(layerD),
                    Normal = Resolve(mc?.Layers.FirstOrDefault(l => l.MapType == 3)?.Texture),
                    Specular = Resolve(mc?.Layers.FirstOrDefault(l => l.MapType == 9)?.Texture),
                    Masked = (shared != null && shared.Blend != BlendMode.Normal) || mc?.Layers.Any(l => l.MapType == 6) == true,
                    Kind = RemasterMaterialKinds.Classify(name, layerD, mc?.Layers.FirstOrDefault(l => l.MapType == 9)?.Texture),
                };
            }
            yield break;
        }
        if (byStem.TryGetValue(part + ".xcmsh", out var me))
        {
            var mesh = Xcmsh.Read(me.Read(), me.Path);
            foreach (var xsh in mesh.Elements.Select(e => Path.GetFileNameWithoutExtension(e.Material)).Distinct())
                yield return FromShader(part, xsh, Shader(xsh));
            yield break;
        }
        Console.WriteLine($"not found: {part} (.xact/.xcmsh)");
    }

    sealed class RemasterTextureSet
    {
        public string Part, Material, Diffuse, Normal, Specular;
        public bool Masked;
        public ColorComponent SpecularChannel;   // which channel of the specular image Genome reads (Default = rgb)
        public RemasterMaterialKind Kind;

        static string Q(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        public static string ToJson(List<RemasterTextureSet> sets) =>
            "[\n" + string.Join(",\n", sets.Select(s =>
                $"  {{\"part\": {Q(s.Part)}, \"material\": {Q(s.Material)}, \"diffuse\": {Q(s.Diffuse)}, \"normal\": {Q(s.Normal)}, " +
                $"\"specular\": {Q(s.Specular)}, \"specularChannel\": {Q(s.SpecularChannel.ToString().ToLowerInvariant())}, \"masked\": {(s.Masked ? "true" : "false")}, \"kind\": {Q(RemasterMaterialKinds.Name(s.Kind))}}}")) + "\n]\n";
    }
}
