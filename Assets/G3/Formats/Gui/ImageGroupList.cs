using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Gui
{
    public sealed class GuiImage
    {
        /// <summary>Original source path, e.g. "#G3:\Data\gui\icons\item\alchemy\Food\IT_Apple.tga".</summary>
        public string SourcePath;
        /// <summary>File name used for lookups ("IT_Apple.tga").</summary>
        public string FileName;
        /// <summary>Atlas path as stored ("Data\gui\icons\compiled\/g3_hud_it_alchemy_00.dds").</summary>
        public string Texture;
        /// <summary>File name of the atlas inside gui.pak ("g3_hud_it_alchemy_00.dds", folder icons/compiled/).</summary>
        public string AtlasFile;
        public GuiRect Rect;
        public ImageGroup Group;
        public override string ToString() => $"{FileName} -> {AtlasFile} {Rect}";
    }

    public sealed class ImageGroup
    {
        public string Name;           // it_alchemy, …, hud_icons, hud_backgrounds, hud_maps, hud_slides
        public string CompiledDir;    // Data\gui\icons\compiled\
        public string SourceRoot;     // Data\gui\icons\item\alchemy
        public string PackBaseName;   // g3_hud_it_alchemy
        public int PackWidth, PackHeight;
        public uint KeyColor;         // 0x00BA9624 in every group (packing background)
        public readonly List<GuiImage> Images = new List<GuiImage>();
    }

    /// <summary>
    /// Library.pak/g3.igl: eCImageGroup list — the index of every packed GUI image (item icons, HUD icons, view
    /// backgrounds, maps, loading slides) into the DXT3 atlases icons/compiled/*.dds of gui.pak. GENOMFLE container.
    /// u32 groupCount, u16[groupCount] names, then per group: u16 version(2), u16 0, u16 name, u16 compiledDir,
    /// u16 sourceRoot, u16 packBaseName, u32 packW, u32 packH, i32 -1, i32 -1, u32 1, u32 1, u32 keyColor, u32 0, u32 0,
    /// u32 1, u32 1, u32 imageCount, imageCount × (u16 sourcePath, u16 texture, rect l,t,r,b as i32).
    /// Images are looked up by file name only (case-insensitive) — that is what item templates and the game code use.
    /// See Docs/formats-gui.md §4.
    /// </summary>
    public sealed class ImageGroupList
    {
        public readonly List<ImageGroup> Groups = new List<ImageGroup>();
        readonly Dictionary<string, GuiImage> _byFile = new Dictionary<string, GuiImage>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<GuiImage> AllImages
        {
            get { foreach (var g in Groups) foreach (var i in g.Images) yield return i; }
        }

        /// <summary>Find an image by file name or any path ending in it; extension is optional (".tga" assumed first).</summary>
        public GuiImage Find(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;
            string f = FileOf(nameOrPath);
            if (_byFile.TryGetValue(f, out var img)) return img;
            if (f.IndexOf('.') < 0)
                foreach (var ext in new[] { ".tga", ".dds", ".bmp" })
                    if (_byFile.TryGetValue(f + ext, out img)) return img;
            // res files reference .tga/.bmp names whose packed source may carry another extension
            string stem = Path.GetFileNameWithoutExtension(f);
            foreach (var ext in new[] { ".tga", ".dds", ".bmp" })
                if (_byFile.TryGetValue(stem + ext, out img)) return img;
            return null;
        }

        public static ImageGroupList Read(byte[] data)
        {
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) throw new InvalidDataException("g3.igl: not a GENOMFLE file");
            var r = g.R;
            var list = new ImageGroupList();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++) g.Str();     // group names (repeated in each group)
            for (int i = 0; i < count; i++)
            {
                if (r.ReadUInt16() != 2) throw new InvalidDataException("g3.igl: unknown group version");
                r.ReadUInt16();
                var grp = new ImageGroup
                {
                    Name = g.Str(), CompiledDir = g.Str(), SourceRoot = g.Str(), PackBaseName = g.Str(),
                    PackWidth = r.ReadInt32(), PackHeight = r.ReadInt32(),
                };
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                grp.KeyColor = r.ReadUInt32();
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                int n = r.ReadInt32();
                for (int k = 0; k < n; k++)
                {
                    var img = new GuiImage { SourcePath = g.Str(), Texture = g.Str(), Group = grp };
                    img.Rect = new GuiRect(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    img.FileName = FileOf(img.SourcePath);
                    img.AtlasFile = FileOf(img.Texture);
                    grp.Images.Add(img);
                    if (!list._byFile.ContainsKey(img.FileName)) list._byFile[img.FileName] = img;
                }
                list.Groups.Add(grp);
            }
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"g3.igl: {g.PayloadEnd - g.Position} unread bytes");
            return list;
        }

        static string FileOf(string path)
        {
            int k = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return k >= 0 ? path.Substring(k + 1) : path;
        }
    }
}
