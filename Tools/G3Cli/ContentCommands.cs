using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;

static partial class Program
{
    const string DefaultProject = @".";
    static string Project => Environment.GetEnvironmentVariable("G3WORLD") ?? DefaultProject;
    static string DefaultPackDir => Path.Combine(Project, "Assets", "StreamingAssets", "Content", G3ContentImporter.PackId);
    static string DefaultVoiceDir => GameDataLayout.VoiceRoot(Project);

    /// <summary>
    /// G3Cli content import [--out dir] | validate [--pack dir] | voice [--region x z half] [--out dir] [--langs a,b]
    /// | voice --migrate (moves an old Assets/StreamingAssets/Voice to GameData/Voice, deleting its .meta files)
    /// — the "Gothic3" content pack (Docs/gameplay-architecture.md §5).
    /// </summary>
    static int ContentCommand(string data, string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "";
        string pack = Option(args, "--pack") ?? Option(args, "--out") ?? DefaultPackDir;
        switch (sub)
        {
            case "import":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var report = G3ContentImporter.Run(data, pack);
                Console.Write(report);
                Console.WriteLine($"pack written to {pack} in {sw.Elapsed.TotalSeconds:F1} s");
                return 0;
            }
            case "validate":
                return ContentValidate(pack);
            case "combat":
                return ContentCombat(pack, args.Skip(2).Where(a => !a.StartsWith("--") && a != pack).ToArray());
            case "voice" when args.Contains("--migrate"):
                // One-shot: run with the editor closed (or Auto Refresh off) so Unity does not see the files vanish mid-way.
                Console.WriteLine(GameDataFiles.Move(Option(args, "--from") ?? GameDataLayout.LegacyVoiceRoot(Project),
                                                     Option(args, "--out") ?? DefaultVoiceDir));
                return 0;
            case "voice":
            {
                // --all: every speaker of the pack (all dialog owners, every NPC voice set, the hero), no region.
                var langs = (Option(args, "--langs") ?? string.Join(",", G3ContentImporter.VoiceLanguages)).Split(',');
                string voicePack = Option(args, "--pack") ?? DefaultPackDir;
                IReadOnlyCollection<string> owners;
                if (args.Contains("--all")) owners = VoiceExtractor.AllOwners(voicePack);
                else
                {
                    var region = (Option(args, "--region") ?? "655,586,150").Split(',').Select(float.Parse).ToArray();
                    owners = VoiceExtractor.OwnersInRegion(data, region[0], region[1], region[2]);
                }
                Console.WriteLine(VoiceExtractor.Extract(data, voicePack, Option(args, "--out") ?? DefaultVoiceDir, owners, langs,
                                                         dryRun: args.Contains("--dry")));
                return 0;
            }
        }
        Console.WriteLine("usage: content import [--out dir] | validate [--pack dir] | combat <npc filter>… [--pack dir] | voice [--all | --region x,z,half] [--langs Russian,English] [--out dir] [--dry] | voice --migrate [--from dir] [--out dir]");
        return 1;
    }

    /// <summary>The imported combat facts of NPCs whose id contains one of the filters (at most 3 per filter).</summary>
    static int ContentCombat(string pack, string[] filters)
    {
        var db = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) }).Database;
        var npcs = db.All<NpcDef>();
        Console.WriteLine($"{npcs.Count} NPCs, {npcs.Count(n => n.Combat != null)} with combat facts, " +
                          $"{npcs.Count(n => n.Combat?.Equipment.Count > 0)} with weaponry; combat rules: {db.TryGet(Myrtana.Rules.CombatRules.DefinitionId, out Myrtana.Rules.CombatRules _)}, combat AI: {db.TryGet(CombatAiRules.DefinitionId, out CombatAiRules _)}");
        foreach (var filter in filters)
        foreach (var npc in npcs.Where(n => n.Id.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).Take(3))
        {
            var c = CombatWiring.CombatDefOf(npc);
            var profile = c.Profile(Myrtana.Rules.Difficulty.Medium);
            var loadout = CombatWiring.LoadoutOf(npc, db);
            var flags = new[] { ("humanoid", c.Humanoid), ("boss", c.Boss), ("guard", c.Guard), ("immortal", c.Immortal), ("friend", c.FriendType),
                                ("mage", c.Mage), ("ambient", c.AmbientCreature), ("evil", c.Evil), ("animal", c.Animal), ("game", c.GameAnimal) }
                .Where(f => f.Item2).Select(f => f.Item1);
            Console.WriteLine($"{npc.Id}: {c.Species} level {c.Level} max {c.LevelMax} (Medium: HP {profile.HitPointsMax}, SP {profile.StaminaMax}), " +
                              $"{c.Role}/{c.Class}, faction {c.Faction ?? "-"}, attitude to player {c.AttitudeToPlayer?.ToString() ?? "-"}, " +
                              $"flags [{string.Join(" ", flags)}]");
            Console.WriteLine($"    equipment [{string.Join(", ", c.Equipment)}] -> melee {loadout.Melee}{(loadout.Shield ? " + shield" : "")}" +
                              $"{(loadout.HasRanged ? $", ranged {loadout.Ranged} ammo {loadout.Ammo?.Id ?? "-"}" : "")}");
        }
        return 0;
    }

    static string Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Loads the pack like the game does and checks every reference the runtime will follow.</summary>
    static int ContentValidate(string pack)
    {
        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(pack) });
        var db = content.Database;
        var problems = new List<string>(content.Problems);
        var entries = db.All<DialogEntryDef>();
        foreach (var e in entries)
        {
            if (!e.Parent.IsEmpty && !db.TryGet(e.Parent, out _)) problems.Add($"{e.Id}: parent {e.Parent} missing");
            if (!e.Owner.IsPlayer && !db.TryGet(e.Owner.Name, out NpcDef _)) problems.Add($"{e.Id}: owner {e.Owner} is not an NPC");
            foreach (var q in e.Commands.OfType<StartQuest>())
                if (!db.TryGet(q.Quest, out _)) problems.Add($"{e.Id}: quest {q.Quest} missing");
        }
        var strings = new Dictionary<string, StringTable>();
        foreach (var lang in new[] { "Russian", "English" }) strings[lang] = content.Strings.Load(lang);
        var says = entries.SelectMany(e => e.Commands.OfType<Say>()).ToList();
        foreach (var lang in strings.Keys)
            Console.WriteLine($"{lang}: {strings[lang]?.Count ?? 0} strings; Say lines without {lang} text: " +
                              $"{says.Count(s => strings[lang] == null || !strings[lang].TryGetText(s.Text.Value, out _))}");
        Console.WriteLine($"{db.Packs.Count} pack(s) [{string.Join(", ", db.Packs.Select(p => p.Id + " " + p.ContentHash?.Substring(0, 12)))}], " +
                          $"{entries.Count} dialog entries, {db.All<QuestDef>().Count} quests, {db.All<NpcDef>().Count} NPCs, " +
                          $"{says.Count} Say lines ({says.Count(s => s.Voice != null)} with a voice clip)");
        Console.WriteLine($"not supported yet: {entries.Count(e => ContainsUnsupported(e.Condition))} entries hidden by an unsupported condition, " +
                          $"{entries.SelectMany(e => e.Commands.OfType<NotSupported>()).Count()} commands skipped at runtime");
        foreach (var p in problems.Take(30)) Console.WriteLine("  " + p);
        Console.WriteLine($"problems: {problems.Count}");
        return problems.Count == 0 ? 0 : 2;
    }

    static bool ContainsUnsupported(Condition c) => c switch
    {
        Unsupported _ => true,
        AllOf all => all.Conditions.Any(ContainsUnsupported),
        AnyOf any => any.Conditions.Any(ContainsUnsupported),
        Not not => ContainsUnsupported(not.Condition),
        _ => false,
    };

    /// <summary>
    /// G3Cli dialog &lt;NPC&gt; [--lang Russian] [--choose 1,2,…] [--give It_Gold:500,It_Wolf_Fur:20] [--xp 3000] [--pack dir]
    /// — plays a conversation in the console through the real DialogService, with the game's notices (XP, quests,
    /// items). Choices come from --choose (1-based, then the last option = usually "End") or stdin; --give and --xp
    /// prepare the hero first.
    /// </summary>
    static int DialogCommand(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: dialog <NPC> [--lang Russian] [--choose 1,2] [--give item:n,…] [--xp n] [--pack dir]"); return 1; }
        var npc = new ActorId(args[1]);
        string lang = Option(args, "--lang") ?? "Russian";
        var script = new Queue<int>((Option(args, "--choose") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse));
        bool interactive = Option(args, "--choose") == null;
        var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(Option(args, "--pack") ?? DefaultPackDir) });
        foreach (var p in content.Problems) Console.WriteLine("content: " + p);
        var presenter = new ConsoleDialogPresenter();
        var session = GameSession.NewGame(content, new SessionPorts(presenter, new ConsoleLog()),
                                          new GameSettings(new LanguageSettings(lang, lang), randomSeed: 1));
        presenter.Bind(session);
        using var notices = new GameNotices(session);
        notices.Posted += (text, _) => Console.WriteLine("  >> " + text);
        foreach (var gift in (Option(args, "--give") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = gift.Split(':');
            session.Items.Inventories.Give(ActorId.Player, parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 1);
        }
        if (int.TryParse(Option(args, "--xp"), out int xp)) session.Progression.AwardXp(xp);
        if (!session.Dialog.CanTalkTo(npc)) { Console.WriteLine($"{npc} has no dialog topics"); return 1; }
        session.Dialog.Talk(npc);
        while (session.Dialog.IsActive)
        {
            var c = session.Dialog.Current;
            if (c.Phase == ConversationPhase.AwaitingLine) { session.Dialog.CompleteLine(); continue; }
            if (c.Phase == ConversationPhase.AwaitingTime) { Console.WriteLine($"  (wait {c.WaitSeconds:0.##} s)"); session.Dialog.Tick(c.WaitSeconds); continue; }
            if (c.Phase != ConversationPhase.AwaitingChoice) break;
            int pick = script.Count > 0 ? script.Dequeue() : interactive ? ReadChoice(c.Options.Count) : c.Options.Count;
            session.Dialog.Choose(c.Options[Math.Clamp(pick, 1, c.Options.Count) - 1].Entry);
        }
        Console.WriteLine("-- quest status: " + string.Join(", ", content.Database.All<DialogEntryDef>()
            .Where(e => e.Owner == npc).SelectMany(e => e.Commands.OfType<StartQuest>()).Select(s => s.Quest).Distinct()
            .Select(q => $"{q} {session.Quests.StatusOf(q)}")));
        Console.WriteLine("-- flags: " + string.Join(", ", session.Flags.All));
        Console.WriteLine($"-- hero: level {session.Hero.Level}, XP {session.Hero.Xp}, LP {session.Hero.LearningPoints}, " +
                          "items " + string.Join(", ", session.Items.Inventories.Of(ActorId.Player).Stacks.Select(st => $"{st.Item} x{st.Amount}")));
        return 0;
    }

    static int ReadChoice(int count)
    {
        Console.Write($"choice 1-{count}> ");
        var line = Console.ReadLine();
        return line == null ? count : int.TryParse(line, out var n) ? n : count;
    }

    sealed class ConsoleLog : IGameLog
    {
        public void Info(string message) => Console.WriteLine("  (info) " + message);
        public void Warning(string message) => Console.WriteLine("  (warning) " + message);
    }

    /// <summary>Text presenter: prints lines and numbered options; the loop in <see cref="DialogCommand"/> answers.</summary>
    sealed class ConsoleDialogPresenter : IDialogPresenter
    {
        GameSession _session;

        public void Bind(GameSession session) => _session = session;

        string Name(ActorId actor) =>
            _session.Content.Database.TryGet(actor.Name, out NpcDef def) && !def.DisplayName.IsEmpty
                ? _session.Localizer.Text(def.DisplayName) : actor.Name;

        public void Open(ActorId partner) => Console.WriteLine($"== conversation with {Name(partner)}");

        public void ShowLine(DialogLine line) =>
            Console.WriteLine($"{Name(line.Speaker)}: {_session.Localizer.Text(line.Text)}" + (line.Voice == null ? "  [no voice]" : $"  [{line.Voice}]"));

        public void ShowOptions(IReadOnlyList<DialogOption> options)
        {
            for (int i = 0; i < options.Count; i++)
                Console.WriteLine($"   {i + 1}. {(options[i].Title.IsEmpty ? "…" : DialogOptionText.Format(_session.Localizer.Text(options[i].Title), options[i].Cost, _session.Localizer.Text))}");
        }

        public void Close() => Console.WriteLine("== end");
    }
}
