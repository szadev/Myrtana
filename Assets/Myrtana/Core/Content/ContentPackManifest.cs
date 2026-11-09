using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Myrtana.Core
{
    /// <summary>
    /// <c>pack.json</c> at the root of a content pack: identity, version and the hash of all its files (saves record it to
    /// detect that the content changed).
    /// </summary>
    public sealed class ContentPackManifest
    {
        public const string FileName = "pack.json";

        /// <summary>Version of the pack layout and JSON schema understood by <see cref="ContentLoader"/>.</summary>
        public const int CurrentFormat = 1;

        [JsonConstructor]
        public ContentPackManifest(string id, string title, int version, int format, string contentHash,
                                   IReadOnlyList<string> languages)
        {
            Id = id;
            Title = title;
            Version = version;
            Format = format;
            ContentHash = contentHash;
            Languages = languages ?? Array.Empty<string>();
        }

        public string Id { get; }
        public string Title { get; }
        public int Version { get; }
        public int Format { get; }
        public string ContentHash { get; }
        /// <summary>Languages with a string table in this pack (G3 names: "Russian", "English" …).</summary>
        public IReadOnlyList<string> Languages { get; }

        public ContentPackManifest WithHash(string contentHash) =>
            new ContentPackManifest(Id, Title, Version, Format, contentHash, Languages);
    }
}
