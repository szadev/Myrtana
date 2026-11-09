using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>The schema of everything the game modules can read from a content pack.</summary>
    public static class GameSchema
    {
        public static ContentSchema Create()
        {
            var schema = new ContentSchema();
            RulesSchema.Register(schema);
            SimulationSchema.Register(schema);
            StorySchema.Register(schema);
            return schema;
        }
    }

    /// <summary>Loaded content packs: definitions (layered by id) and the per-language string tables.</summary>
    public sealed class GameContent
    {
        GameContent(IContentDatabase database, IStringTableSource strings, IReadOnlyList<string> problems)
        {
            Database = database;
            Strings = strings;
            Problems = problems;
        }

        public IContentDatabase Database { get; }
        public IStringTableSource Strings { get; }
        /// <summary>Files or entries that could not be loaded (the rest of the content is still usable).</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Loads <paramref name="packs"/> in order; later packs override earlier ones.</summary>
        public static GameContent Load(IReadOnlyList<IContentSource> packs)
        {
            var schema = GameSchema.Create();
            var loader = new ContentLoader(schema);
            var database = loader.Load(packs);
            return new GameContent(database, new PackStringTables(packs, schema), loader.Problems);
        }
    }
}
