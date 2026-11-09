using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Animation;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Mesh;
using G3.Formats.Pak;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// The original game files the offline Source exporter reads (Docs/remaster-pipeline.md §12): actors, static meshes,
    /// shader materials and images by file stem. Implemented over the paks (<see cref="PakSourceData"/>) and in memory by
    /// the tests.
    /// </summary>
    public interface ISourceData
    {
        /// <summary>The actor (LoD 0) of <c>&lt;stem&gt;.xact</c>; throws <see cref="FileNotFoundException"/> when missing.</summary>
        EmfxActor Actor(string name);
        /// <summary>Static mesh (.xcmsh; .xlmsh resolved to its LoD 0); null when missing or unreadable.</summary>
        MeshResource Mesh(string file);
        /// <summary>Shader material <c>&lt;stem&gt;.xshmat</c>; null when missing or unreadable.</summary>
        MaterialResource Shader(string stem);
        /// <summary>The image's stem as stored (case of the pak) when <c>&lt;stem&gt;.ximg</c> exists, else null.</summary>
        string ImageName(string stem);
        ImageResource Image(string stem);
        bool HasMotion(string stem);
    }

    /// <summary><see cref="ISourceData"/> over the game's paks (the same volumes the Unity character importer reads).</summary>
    public sealed class PakSourceData : ISourceData, IDisposable
    {
        readonly G3FileSystem _fs;
        readonly Dictionary<string, PakEntry> _byName = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, EmfxActor> _actors = new Dictionary<string, EmfxActor>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, MeshResource> _meshes = new Dictionary<string, MeshResource>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, MaterialResource> _shaders = new Dictionary<string, MaterialResource>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Warnings = new List<string>();

        public PakSourceData(string dataDir)
        {
            _fs = new G3FileSystem(dataDir, "_compiledAnimation", "_compiledMaterial", "_compiledImage", "_compiledMesh");
            foreach (var e in _fs.Files) _byName[Path.GetFileName(e.Path)] = e;
        }

        public void Dispose() => _fs.Dispose();

        public IEnumerable<string> FileNames => _byName.Keys;

        public EmfxActor Actor(string name)
        {
            string file = Path.GetFileNameWithoutExtension(name) + ".xact";
            if (_actors.TryGetValue(file, out var a)) return a ?? throw new FileNotFoundException(file);
            if (!_byName.TryGetValue(file, out var e)) { _actors[file] = null; throw new FileNotFoundException(file); }
            return _actors[file] = Xact.Read(e.Read(), file).Actor;
        }

        public MeshResource Mesh(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (_meshes.TryGetValue(file, out var m)) return m;
            string resolved = file;
            if (file.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var list = _byName.TryGetValue(file, out var lod) ? Xlmsh.Read(lod.Read()) : null;
                resolved = list != null && list.Count > 0 ? list[0] : null;
            }
            if (resolved != null && _byName.TryGetValue(resolved, out var e))
            {
                try { m = Xcmsh.Read(e.Read(), resolved); }
                catch (Exception ex) { Warnings.Add($"mesh {resolved}: {ex.Message}"); }
            }
            else Warnings.Add($"mesh not found: {file}");
            return _meshes[file] = m;
        }

        public MaterialResource Shader(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            if (_shaders.TryGetValue(stem, out var s)) return s;
            if (_byName.TryGetValue(stem + ".xshmat", out var e))
            {
                try { s = Xshmat.Read(e.Read(), stem); }
                catch (Exception ex) { Warnings.Add($"material {stem}: {ex.Message}"); }
            }
            return _shaders[stem] = s;
        }

        public string ImageName(string stem) =>
            stem != null && _byName.TryGetValue(stem + ".ximg", out var e) ? Path.GetFileNameWithoutExtension(e.Path) : null;

        public ImageResource Image(string stem) =>
            stem != null && _byName.TryGetValue(stem + ".ximg", out var e) ? Ximg.Read(e.Read()) : null;

        public bool HasMotion(string stem) => stem != null && _byName.ContainsKey(stem + ".xmot");
    }
}
