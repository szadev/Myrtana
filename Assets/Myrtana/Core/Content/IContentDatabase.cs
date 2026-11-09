using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>An immutable content definition (dialog entry, quest, NPC …) identified by a unique key within its kind.</summary>
    public interface IDefinition
    {
        string Key { get; }
    }

    /// <summary>All definitions of the loaded content packs, after layering (later packs replace earlier ones by key).</summary>
    public interface IContentDatabase
    {
        IReadOnlyList<ContentPackManifest> Packs { get; }
        bool TryGet<T>(string key, out T definition) where T : class, IDefinition;
        IReadOnlyCollection<T> All<T>() where T : class, IDefinition;
    }

    public static class ContentDatabaseExtensions
    {
        public static bool TryGet<T>(this IContentDatabase db, Id<T> id, out T definition) where T : class, IDefinition =>
            db.TryGet(id.Name ?? string.Empty, out definition);

        public static T Get<T>(this IContentDatabase db, Id<T> id) where T : class, IDefinition =>
            db.TryGet(id, out var definition)
                ? definition
                : throw new KeyNotFoundException($"No {typeof(T).Name} with id '{id}' in the loaded content.");
    }
}
