using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Myrtana.Core
{
    /// <summary>Read access to the files of one content pack. Paths are relative and use '/'.</summary>
    public interface IContentSource
    {
        /// <summary>For messages: a directory path, an archive name …</summary>
        string Location { get; }
        bool Exists(string path);
        Stream Open(string path);
        /// <summary>Relative paths of the files directly in <paramref name="folder"/> with the given extension, sorted ordinally.</summary>
        IReadOnlyList<string> List(string folder, string extension);
    }

    /// <summary>A pack stored as a plain directory (StreamingAssets in Unity, any folder in G3Cli and tests).</summary>
    public sealed class DirectoryContentSource : IContentSource
    {
        readonly string _root;

        public DirectoryContentSource(string root) => _root = Path.GetFullPath(root);

        public string Location => _root;

        public bool Exists(string path) => File.Exists(Full(path));

        public Stream Open(string path) => File.OpenRead(Full(path));

        public IReadOnlyList<string> List(string folder, string extension)
        {
            string dir = Full(folder);
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*" + extension)
                .Select(f => folder + "/" + Path.GetFileName(f))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        string Full(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
    }
}
