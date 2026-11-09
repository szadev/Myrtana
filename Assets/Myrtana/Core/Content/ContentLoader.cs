using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Myrtana.Core
{
    /// <summary>
    /// Loads content packs in order into one <see cref="ContentDatabase"/>. Each kind folder holds JSON arrays of
    /// definitions; a later pack replaces a definition with the same key, and <c>{"id": "…", "$remove": true}</c>
    /// deletes one. Problems are collected in <see cref="Problems"/> instead of stopping the load, so one broken file
    /// in a hand-written pack does not hide everything else.
    /// </summary>
    public sealed class ContentLoader
    {
        const string RemoveMarker = "$remove";

        readonly ContentSchema _schema;
        readonly JsonSerializerOptions _json;
        readonly List<string> _problems = new List<string>();

        public ContentLoader(ContentSchema schema)
        {
            _schema = schema;
            _json = ContentJson.CreateOptions(schema);
        }

        public IReadOnlyList<string> Problems => _problems;

        public ContentDatabase Load(IEnumerable<IContentSource> packs)
        {
            var db = new ContentDatabase();
            foreach (var pack in packs) LoadPack(db, pack);
            return db;
        }

        void LoadPack(ContentDatabase db, IContentSource pack)
        {
            if (!pack.Exists(ContentPackManifest.FileName))
            {
                _problems.Add($"{pack.Location}: no {ContentPackManifest.FileName}, pack skipped.");
                return;
            }
            ContentPackManifest manifest;
            using (var s = pack.Open(ContentPackManifest.FileName)) manifest = JsonSerializer.Deserialize<ContentPackManifest>(s, _json);
            if (manifest.Format != ContentPackManifest.CurrentFormat)
                _problems.Add($"{pack.Location}: pack format {manifest.Format}, expected {ContentPackManifest.CurrentFormat}.");
            db.AddPack(manifest);
            foreach (var kind in _schema.Kinds)
            foreach (var file in pack.List(kind.Folder, ".json"))
                LoadFile(db, pack, kind, file);
        }

        void LoadFile(ContentDatabase db, IContentSource pack, ContentKind kind, string file)
        {
            try
            {
                using (var s = pack.Open(file))
                using (var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                {
                    foreach (var element in doc.RootElement.EnumerateArray()) LoadElement(db, kind, element, file);
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                _problems.Add($"{pack.Location}/{file}: {ex.Message}");
            }
        }

        void LoadElement(ContentDatabase db, ContentKind kind, JsonElement element, string file)
        {
            if (element.TryGetProperty(RemoveMarker, out var remove) && remove.ValueKind == JsonValueKind.True)
            {
                string key = element.TryGetProperty("id", out var k) ? k.GetString() : null;
                if (key == null || !db.Remove(kind.Type, key)) _problems.Add($"{file}: nothing to remove for id '{key}'.");
                return;
            }
            var definition = (IDefinition)element.Deserialize(kind.Type, _json);
            if (string.IsNullOrEmpty(definition?.Key)) _problems.Add($"{file}: a {kind.Type.Name} without id was skipped.");
            else db.Put(kind.Type, definition);
        }
    }
}
