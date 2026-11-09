using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Myrtana.Core
{
    /// <summary>
    /// Writes a content pack directory: definition files, string tables and <c>pack.json</c> with a SHA-256 over all
    /// files. Used by importers; hand-written packs need no writer. The target directory is replaced as a whole.
    /// </summary>
    public sealed class ContentPackWriter
    {
        readonly string _root;
        readonly JsonSerializerOptions _json;

        public ContentPackWriter(string root, ContentSchema schema)
        {
            _root = Path.GetFullPath(root);
            _json = ContentJson.CreateOptions(schema, indented: true);
            ClearPreviousPack(_root);
            Directory.CreateDirectory(_root);
        }

        public string Root => _root;

        /// <summary>Writes <paramref name="definitions"/> as one JSON array to <c>&lt;folder&gt;/&lt;fileStem&gt;.json</c>.</summary>
        public void WriteDefinitions<T>(string folder, string fileStem, IEnumerable<T> definitions) where T : class, IDefinition =>
            WriteJson(folder + "/" + fileStem + ".json", definitions.ToList());

        public void WriteStrings(string language, StringTableFile table) =>
            WriteJson(StringTableFile.PathFor(language), table);

        /// <summary>Writes <c>pack.json</c> last, with the hash of everything written before. Returns the final manifest.</summary>
        public ContentPackManifest Finish(ContentPackManifest manifest)
        {
            var final = manifest.WithHash(HashFiles());
            WriteJson(ContentPackManifest.FileName, final);
            return final;
        }

        /// <summary>Deletes an earlier generated pack; refuses to touch a non-empty directory that is not a pack.</summary>
        static void ClearPreviousPack(string root)
        {
            if (!Directory.Exists(root)) return;
            bool isPack = File.Exists(Path.Combine(root, ContentPackManifest.FileName));
            if (!isPack && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException($"{root} is not empty and is not a content pack; refusing to overwrite it.");
            Directory.Delete(root, recursive: true);
        }

        void WriteJson<T>(string relativePath, T value)
        {
            string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(value, _json), new UTF8Encoding(false));
        }

        string HashFiles()
        {
            using (var sha = SHA256.Create())
            {
                var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(_root.Length + 1).Replace(Path.DirectorySeparatorChar, '/'))
                    .Where(f => f != ContentPackManifest.FileName && !f.EndsWith(".meta", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    var name = Encoding.UTF8.GetBytes(f);
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    var bytes = File.ReadAllBytes(Path.Combine(_root, f));
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
