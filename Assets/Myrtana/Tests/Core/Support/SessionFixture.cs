using System;
using System.Collections.Generic;
using System.IO;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Tests
{
    /// <summary>
    /// A whole <see cref="GameSession"/> over a small content pack written to a temporary directory (the same loading
    /// path as the game). Add definitions with <see cref="Add{T}"/>, then use <see cref="NewGame"/> / <see cref="Reload"/>.
    /// </summary>
    public sealed class SessionFixture : IDisposable
    {
        public static readonly ActorId Npc = new ActorId("Gnar");

        readonly string _root = Path.Combine(Path.GetTempPath(), "myrtana_session_" + Guid.NewGuid().ToString("N"));
        readonly List<Action<ContentPackWriter>> _writes = new List<Action<ContentPackWriter>>();
        GameContent _content;
        ulong? _seed = 7;

        public SessionFixture()
        {
            Add(SimulationSchema.NpcFolder, new NpcDef(Npc, new TextId("FO_Gnar"), "Orc_Warrior_Hard"),
                                            new NpcDef(ActorId.Player, new TextId("FO_PC_Hero"), "Player"));
            Add(RulesSchema.AttributeFolder, TestRules.Attributes());
            Add(RulesSchema.ProgressionFolder, TestRules.Progression());
            Add(RulesSchema.ItemRulesFolder, TestRules.Items());
            Add(RulesSchema.TradeRulesFolder, TestRules.Trade());
            Add(RulesSchema.TeachingRulesFolder, TestRules.Teaching());
            Add(RulesSchema.CombatRulesFolder, TestRules.Combat());
            Add(SimulationSchema.CombatAiFolder, TestRules.CombatAi());
        }

        public RecordingPresenter Presenter { get; } = new RecordingPresenter();
        public ListLog Log { get; } = new ListLog();
        public List<ISaveParticipant> HostSections { get; } = new List<ISaveParticipant>();
        public List<ISaveBlocker> HostBlockers { get; } = new List<ISaveBlocker>();

        public SessionFixture Add<T>(string folder, params T[] definitions) where T : class, IDefinition
        {
            int index = _writes.Count;
            _writes.Add(w => w.WriteDefinitions(folder, typeof(T).Name + index, definitions));
            _content = null;
            return this;
        }

        public SessionFixture Quest(string id) =>
            Add(StorySchema.QuestFolder, new QuestDef(new Id<QuestDef>(id), new TextId("QUEST_" + id), null));

        /// <summary>fix-119: the pinned seed of the sessions (default 7); null = a fresh seed per session, as in the game.</summary>
        public SessionFixture Seed(ulong? seed)
        {
            _seed = seed;
            return this;
        }

        public GameContent Content => _content ?? (_content = Build());

        public GameSession NewGame() => GameSession.NewGame(Content, Ports(), Settings());

        public GameSession Load(byte[] save) => GameSession.Load(Content, Ports(), Settings(), new MemoryStream(save));

        public static byte[] Save(GameSession session)
        {
            var stream = new MemoryStream();
            session.Save(stream, "test", new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc));
            return stream.ToArray();
        }

        /// <summary>Save, then load into a new session.</summary>
        public GameSession Reload(GameSession session) => Load(Save(session));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        SessionPorts Ports() => new SessionPorts(Presenter, Log, HostSections, HostBlockers);

        GameSettings Settings() => new GameSettings(new LanguageSettings("English", "English"), randomSeed: _seed);

        GameContent Build()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            var writer = new ContentPackWriter(_root, GameSchema.Create());
            foreach (var write in _writes) write(writer);
            writer.Finish(new ContentPackManifest("Test", "Test", 1, ContentPackManifest.CurrentFormat, null, Array.Empty<string>()));
            return GameContent.Load(new IContentSource[] { new DirectoryContentSource(_root) });
        }
    }
}
