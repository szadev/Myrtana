using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>What the host (Unity, G3Cli, a test) plugs into the session.</summary>
    public sealed class SessionPorts
    {
        public SessionPorts(IDialogPresenter dialog, IGameLog log, IReadOnlyList<ISaveParticipant> hostSaveSections = null,
                            IReadOnlyList<ISaveBlocker> hostSaveBlockers = null)
        {
            Dialog = dialog;
            Log = log ?? NullGameLog.Instance;
            HostSaveSections = hostSaveSections ?? Array.Empty<ISaveParticipant>();
            HostSaveBlockers = hostSaveBlockers ?? Array.Empty<ISaveBlocker>();
        }

        public IDialogPresenter Dialog { get; }
        public IGameLog Log { get; }
        /// <summary>State the host keeps itself (player position …), saved and loaded with the game.</summary>
        public IReadOnlyList<ISaveParticipant> HostSaveSections { get; }
        /// <summary>Host conditions that forbid saving (falling, a menu …).</summary>
        public IReadOnlyList<ISaveBlocker> HostSaveBlockers { get; }
    }

    /// <summary>Player settings that live outside save games.</summary>
    public sealed class GameSettings
    {
        /// <param name="randomSeed">
        /// fix-119: null (the game) = every session — a new game and each load — rolls with a fresh seed, like G3's unsaved
        /// <c>rand()</c>; a value pins it (tests, dev tools, scenario runs) so a run is reproducible.
        /// </param>
        public GameSettings(LanguageSettings languages, ulong? randomSeed = null, RuleOptions rules = null)
        {
            Languages = languages;
            RandomSeed = randomSeed;
            Rules = rules ?? RuleOptions.Default;
        }

        public LanguageSettings Languages { get; }
        /// <summary>The pinned seed of the sessions made with these settings; null = a fresh one each (fix-119).</summary>
        public ulong? RandomSeed { get; }
        /// <summary>Rule options (CP 1.75 Alternative Balancing, XP modifier, difficulty …).</summary>
        public RuleOptions Rules { get; }
    }

    /// <summary>
    /// Composition root of the game: creates the state and services from the content, wires the ports and owns the
    /// tick and the save sections. The presentation layer reaches services through the typed properties.
    /// A loaded game is a new session: the new-game state is built first, then the save's sections are read into it.
    /// </summary>
    public sealed class GameSession
    {
        /// <summary>Reason given when a dialog is running.</summary>
        public const string BlockedByDialog = "dialog";

        readonly GameClock _clock;
        readonly PlayTime _playTime = new PlayTime();
        readonly List<ISaveParticipant> _saveSections = new List<ISaveParticipant>();
        readonly List<ISaveBlocker> _saveBlockers = new List<ISaveBlocker>();
        readonly FameSaveSection _fameSection;
        readonly CrimeSaveSection _crimeSection;
        readonly IGameLog _log;
        readonly QuestRewardGiver _questRewards;
        readonly QuestItemCounter _questItems;
        readonly QuestDefeatCounter _questDefeats;
        CraftingServices _crafting;

        GameSession(GameContent content, SessionPorts ports, GameSettings settings)
        {
            _log = new OnceGameLog(ports.Log);
            Content = content;
            Settings = settings;
            _clock = new GameClock(timeScale: 12f); // G3: 1 real second = 12 game seconds (gameplay-logic.md §0.2)
            // fix-119: one generator for all rolls, never restored from a save (TimeSaveSection): G3's rand() is not saved, so a
            // reload rolls anew (picks, pickpocketing, hits, loot, AI); a pinned seed makes tests and scenario runs reproducible.
            RandomSeed = SessionSeed.Resolve(settings.RandomSeed);
            Random = new SeededRandom(RandomSeed);
            Events = new EventBus();
            Flags = new GameEventFlags();
            Actors = new ActorRegistry(content.Database.All<NpcDef>());
            Removal = new ActorRemoval(Events); // stores register below, once they exist
            Quests = new QuestService(content.Database, Events, _log, _clock);
            QuestLog = new QuestLog(Events);
            Fame = new FameLedger();
            var progressionRules = ProgressionRulesOf(content.Database);
            Hero = Hero.CreateNew(content.Database, progressionRules, settings.Rules);
            Progression = new ProgressionService(Hero, progressionRules, settings.Rules, Events);
            var killXp = new KillXpAwarder(Progression, Events);
            _questRewards = new QuestRewardGiver(Quests, Progression, Fame, Events);
            var dialogMemory = new DialogMemory();
            Items = ItemWiring.Create(content.Database, Hero, Progression, Fame, settings.Rules, Random, Events, _log);
            // chests are quest destinations by their entity name (Plunder "Esiel_Chest"); their inventories are container ids
            var chestNames = content.Database.All<InteractiveObjectDef>().Where(o => o.Container != null && !string.IsNullOrEmpty(o.Name))
                                    .ToDictionary(o => InteractiveObjects.ContainerOf(o.Id), o => new ActorId(o.Name));
            _questItems = new QuestItemCounter(Quests, content.Database.All<QuestDef>(), Events,
                                               a => chestNames.TryGetValue(a, out var chest) ? chest : a);
            Events.Subscribe<DialogStarted>(e => Items.Trade.Open(e.Partner, _clock.GameSeconds / 3600.0)); // G3 ZS_Talk: stock at dialog start
            Events.Subscribe<DialogEnded>(e => Items.Trade.NoteContactEnded(e.Partner, _clock.GameSeconds / 3600.0));
            var party = new PlayerParty(Items.Roles, Events);
            Combat = CombatWiring.Create(content.Database, Actors, Hero, settings.Rules, Random, Events, _log, party.IsLedByPlayer);
            Npcs = NpcSimulationWiring.Create(content.Database, _clock, Random, Events);
            // G3 IsFreeDestination: a down or dead user no longer holds its object (Docs/interactive-objects.md §9)
            Npcs.Claims.UserGone = a => Combat.Vitals.IsDown(a) || Combat.Vitals.IsDead(a);
            Npcs.Lifeless = a => Combat.Vitals.IsDead(a); // a corpse keeps its place (fix-80)
            NpcCombat = CombatWiring.CreateNpcCombat(content.Database, Actors, Combat, settings.Rules, Random, () => _playTime.Seconds,
                                                     () => _clock.GameSeconds, Events, _log);
            Combat.Vitals.FinishingHold = NpcCombat.IsBeingFinished; // the knocked-out victim stays down until the finisher lands
            Items.NpcLoot.Connect(NpcCombat.HasLost, NpcCombat.TakeItem); // looted weaponry: the NPC fights without it
            Items.NpcLoot.IsDead = Combat.Vitals.IsDead; // fix-143: late trophies are for corpses
            Magic = MagicWiring.Create(content.Database, Actors, Combat, NpcCombat, Items, settings.Rules, Events, _log);
            Items.Use.DrunkSpells = Magic; // the antidote and the potion against diseases cast their cure (PS_MagicPotion, fix-127)
            HeroSpells = new HeroSpells(Hero, Items.Inventories.Of(ActorId.Player), content.Database);
            // G3 OnPlayerAssignStackKey: only weapons, ammo, shields, spells, potions, food, torches … (QuickSlotRules);
            // spells must be learned
            Items.QuickSlots.CanAssign = item => QuickSlotRules.Allows(content.Database, item) && HeroSpells.CanReady(item);
            Summons = new SummonGroups(Magic.Rules.Summons);
            Chapter = new StoryChapter();
            Crime = CrimeWiring.Create(content.Database, Actors, Hero, Fame, Chapter, Items, Npcs, Combat, NpcCombat, settings.Rules, Random,
                                       Events, () => _clock.GameSeconds / 3600.0, _log, party, Flags);
            Party = PartyWiring.Create(content.Database, party, Items, Quests, Npcs, () => Crime.World, Events);
            // fix-126 [owner]: where the hero talked to quest people (the map pages' quest marks)
            QuestTalks = new QuestTalkMarks(Events, npc => Party.Positions.TryGetPose(npc.Name, out var talkPose)
                                                             ? (talkPose.X, talkPose.Y, talkPose.Z) : ((float, float, float)?)null);
            Enclaves = new EnclaveStates();
            Crime.Attitudes.Enclaves = Enclaves;
            Summoning = new SummonService(Summons, Actors, Npcs, party, Combat.Vitals, NpcCombat, () => content.Database.All<NpcDef>(), Events, _log,
                                          Removal)
            {
                Positions = id => Party.Positions.TryGetPose(id.Name, out var pose) ? pose : (WorldPose?)null,
            };
            if (Crime.Attitudes.Facts is SessionAttitudeFacts summonFacts) summonFacts.SummonerOf = Summons.LeaderOf;
            _questDefeats = new QuestDefeatCounter(Quests, content.Database.All<QuestDef>(), Events, settings.Rules.AlternativeBalancing);
            // fix-161 [owner] "Freeing by force counts" (off until PortOptionsApplier sets it; on load it re-judges failed releases)
            ForcedReleases = new ForcedReleases(Quests, Flags, Combat.Vitals.IsDead, content.Database.All<QuestDef>(),
                                                content.Database.All<DialogEntryDef>(), _log);
            _questDefeats.ReleasedByForce = ForcedReleases.TryRelease;
            WorldState = WorldStateWiring.Create(content.Database, Events, _log, Quests, Actors, Npcs, Combat, NpcCombat, Crime, Enclaves,
                                                 Party, Items, () => Interaction?.Objects);
            // after the sectors are wired: an NPC of a switched-off sector takes no station (fix-67 R33-3)
            Npcs.PlaceAtRoutineStart();
            WorldState.Triggers = new StoryTriggers(content.Database.TryGet(StoryTriggerRules.DefinitionId, out StoryTriggerRules triggers) ? triggers : null,
                                                    Events, Flags, s => WorldState.Sectors.Set(s, true), WorldState.Areas,
                                                    () => Combat.Vitals.StaminaMax(ActorId.Player),
                                                    () => Combat.Vitals.KnockOut(ActorId.Player, byPlayerParty: false));
            var director = new StoryActorDirector(Actors, NpcCombat, Npcs, Combat.Vitals, () => Interaction.Sleep);
            Pickpockets = new PickpocketService(content.Database, Actors, Hero, Progression, Items, Crime.Ledger, settings.Rules, Random, _log, Events)
            {
                AssessTarget = Crime.Crime.AssessTarget,
            };
            var services = new StoryServices(Actors, Combat.Vitals, new NpcVoiceSets(content.Database), Crime.Reputation,
                                             Progression, Fame, Items, Crime.Talk, Chapter, director, Random, party, Party.Positions,
                                             WorldState.Director, new SessionActorTraits(Actors, NpcCombat), Pickpockets);
            var world = new StoryWorld(Flags, Quests, dialogMemory, QuestLog, services, Events, _log);
            content.Database.TryGet(SharedLines.DefinitionId, out SharedLines sharedLines);
            Dialog = new DialogService(world, new DialogCatalog(content.Database.All<DialogEntryDef>()),
                                       CommandRunner.CreateDefault(sharedLines), ports.Dialog);
            // G3 AssessTalk: an NPC with an important info (a gate guard's warning, crime news, a greeting) starts the talk.
            Crime.Crime.HasImportantInfo = Dialog.HasImportantInfo;
            Dialog.TalkBlocked = npc => SleeperTheft.TalkBlocked(npc) // [owner] fix-113: no talk with a spell sleeper
                                        || WorldState.Director.IsDoneDoorTester(npc.Name); // fix-261: an opened temple door
            // fix-247, G3 ZS_Talk: a sleeper talked to first says "YouDisturbedMySlumber", then stands up and talks
            var voiceSets = services.VoiceSets;
            Dialog.Prelude = npc =>
            {
                if (sharedLines == null || !Npcs.TryGet(npc, out var sleeper)) return null;
                bool reacting = Life != null && Life.Tasks.KindOf(npc) != ReactionTaskKind.None;
                if (!SleeperRules.SaysDisturbedOnTalk(Npcs.Sleeps(sleeper), reacting)) return null;
                string voiceSet = voiceSets.VoiceSetOf(npc);
                if (string.IsNullOrEmpty(voiceSet)) return null;
                var text = sharedLines.TextOf(voiceSet, SleeperRules.DisturbedLine);
                return new DialogLine(npc, text, text.Value);
            };
            // fix-158: G3 StartSayRefuseTalk, the answer of an NPC with nothing to talk about
            TalkRefusals = new TalkRefusals(content.Database, Npcs, Combat.Vitals, NpcCombat, Crime, Enclaves, Events);
            // CP AssessTalk: not while the NPC fights or flees (an arena bout is a fight).
            Crime.Crime.InFight = id => NpcCombat.Get(id)?.IsFighting == true;
            Crime.Crime.EndWarningFight = id =>
            {
                var brain = NpcCombat.Get(id);
                if (brain != null && brain.IsFighting && brain.State.Target.IsPlayer &&
                    (brain.State.Reason == AttackReason.ReactToWeapon || brain.State.Reason == AttackReason.Intruder ||
                     brain.State.Reason == AttackReason.ReactToDamage))
                    brain.Disengage();
            };
            // G3 AssessAgressor: a Friendly victim complains (ZS_ReactToDamage) instead of fighting back; the party bond forgives.
            NpcCombat.AttitudeOf = Crime.Attitudes.Of;
            NpcCombat.PartyBond = Crime.Attitudes.PartyBond;
            Magic.Status.HumanoidDiesOfDamageOverTime = DiesOfDamageOverTime; // fix-209: CP OnTick, a hostile orc burns to death
            Events.Subscribe<NpcReacted>(e => { if (e.Reaction.Kind == ReactionKind.Talk) Dialog.TalkFirst(e.Npc); });
            Localizer = new Localizer(settings.Languages, content.Strings);
            // fix-235: a shared line without its voice set's own text shows another set's (or none: no empty subtitle box)
            SharedLineTexts = new SharedLineTexts(sharedLines, Localizer,
                                                  content.Database.All<NpcDef>().Select(n => n.VoiceSet).Append(SharedLineTexts.HeroVoiceSet));
            Interaction = InteractionWiring.Create(content.Database, _clock, Hero, Items, Combat, Random, Events, Flags, Progression);
            Regeneration = new HeroRegeneration(Hero, Combat.Vitals, settings.Rules, () => NpcCombat.IsPlayerInCombat);
            Maps = new WorldMaps(content.Database.All<MapDef>(), () => Items.Inventories.Of(ActorId.Player));
            Teleports = new Teleports(content.Database.All<TeleportDestinationDef>(),
                                      id => Magic.TryGetSpell(id, out var spell) ? spell : null, Events, _log);
            SpellEffects = SpellEffectServices.Create(this, _log); // magic-3: sleep, control, mist, forms … (Docs/combat.md §12.5)
            Life = new NpcLifeServices(Npcs, Actors, Combat.Vitals, NpcCombat, Crime, Items, content.Database, Random, Events, Removal,
                                       () => _clock.GameSeconds / 3600.0)
            {
                BurningOrBarrier = id => Magic.Status.Has(id, ActorStatus.Burning),
                PlayerParty = party,
                Fireclan = NpcLifeServices.EnclavePose(content.Database, MobMeetingPoint.Enclave),
                Enclaves = Enclaves,
            };
            // CP Respawn clears DefeatedByPlayer of a revived creature, so killing it again pays its XP again (fix-81).
            Events.Subscribe<CorpseRevived>(e => killXp.Forget(e.Npc));
            // One removal path for runtime actors (summons, dev despawn): fights and groups end first, the registry goes last.
            Removal.Register(Summoning).Register(NpcCombat).Register(party).Register(Magic.Status).Register(Crime.Crime)
                   .Register(Crime.Ledger).Register(Items.Inventories).Register(Items.NpcLoot).Register(Items.Trade).Register(Items.Roles)
                   .Register(SpellEffects.World).Register(killXp).Register(Life.Tasks).Register(Life.Corpses).Register(Life.Parties).Register(Combat.Vitals)
                   .Register(Npcs).Register(Actors);

            _saveSections.Add(new TimeSaveSection(_clock, _playTime));
            _saveSections.Add(new FlagsSaveSection(Flags));
            _saveSections.Add(new SummonSaveSection(Summoning)); // the creatures exist before the sections below fill them in
            _saveSections.Add(new ActorFactsSaveSection(Actors, content.Database));
            _saveSections.Add(new ActorAlignmentSaveSection(Actors));
            _saveSections.Add(new QuestSaveSection(Quests));
            _saveSections.Add(new QuestLogSaveSection(QuestLog));
            _saveSections.Add(new QuestTalkMarksSaveSection(QuestTalks)); // fix-126: quest talk marks on the map
            _saveSections.Add(new DialogMemorySaveSection(dialogMemory));
            _saveSections.Add(new HeroSaveSection(Hero, killXp));
            _saveSections.Add(_fameSection = new FameSaveSection(Fame));
            _saveSections.Add(new ItemsSaveSection(Items.Inventories, Items.Equipment, Items.World, Items.Roles, Items.Trade, Items.NpcLoot));
            _saveSections.Add(new QuickSlotsSaveSection(Items.QuickSlots));
            _saveSections.Add(new NpcSimulationSave(Npcs));
            _saveSections.Add(new PartySaveSection(party));
            _saveSections.Add(new VitalsSaveSection(Combat.Vitals, Actors.Exists));
            _saveSections.Add(new StatusEffectsSaveSection(Magic.Status, Actors.Exists));
            _saveSections.Add(new NpcCombatSave(NpcCombat));
            _saveSections.Add(Life.Corpses);
            _saveSections.Add(new SessionSection("life.wounded", 1, Life.WriteWoundedInRange, Life.ReadWoundedInRange)); // fix-175
            _saveSections.Add(new NpcPartiesSaveSection(Life.Parties));
            WorldState.Director.JoinNpcHandler = Life.JoinNpc;
            _saveSections.Add(_crimeSection = new CrimeSaveSection(Crime.Ledger));
            _saveSections.Add(new StoryChapterSaveSection(Chapter));
            _saveSections.Add(new InteractiveObjectsSaveSection(Interaction.Objects));
            _saveSections.Add(new LockedChestMarksSaveSection(Interaction.ChestMarks)); // fix-102: map marks of locked chests
            if (Items.NpcLoot != null) _saveSections.Add(new TreasureSaveSection(Items.NpcLoot.Plunder)); // unique chest stacks handed out
            _saveSections.Add(new OrcBouldersSaveSection(Interaction.Boulders));
            _saveSections.Add(new ObjectUsesSaveSection(Interaction.Uses));
            _saveSections.Add(new BookstandSaveSection(() => Bookstands));
            _saveSections.Add(new WorldMapSaveSection(Maps));
            _saveSections.Add(new EnclaveStatesSaveSection(Enclaves));
            _saveSections.Add(new WorldSectorsSaveSection(WorldState.Sectors));
            _saveSections.Add(new StoryWorldDirectorSaveSection(WorldState.Director));
            _saveSections.Add(new GateGuardsSaveSection(WorldState.GateGuards));
            _saveSections.Add(new PickpocketSaveSection(Pickpockets));
            _saveSections.Add(new SessionSection("world.music", 1, w => w.WriteSortedStrings(Music.VictoryPlayed),
                                                 (r, v) => { foreach (var e in r.ReadStrings()) Music.MarkVictoryPlayed(e); }));
            _saveSections.Add(new SessionSection("session.start", 1, WriteStart, ReadStart));
            _saveSections.Add(new SessionSection("session.rules", 1, WriteRules, ReadRules));
            _saveSections.AddRange(ports.HostSaveSections);
            _saveBlockers.Add(SaveBlocker.When(() => Dialog.IsActive, BlockedByDialog));
            _saveBlockers.Add(SaveBlocker.When(() => Combat.Vitals.IsDead(ActorId.Player), CombatWiring.BlockedByDeath));
            _saveBlockers.Add(SaveBlocker.When(() => Combat.Vitals.IsDown(ActorId.Player), CombatWiring.BlockedByKnockOut));
            _saveBlockers.Add(SaveBlocker.When(() => NpcCombat.IsPlayerInCombat, CombatWiring.BlockedByCombat));
            _saveBlockers.AddRange(ports.HostSaveBlockers);
        }

        ProgressionRules ProgressionRulesOf(IContentDatabase content)
        {
            if (content.TryGet(ProgressionRules.DefinitionId, out ProgressionRules rules)) return rules;
            _log.Warning("The content has no progression rules; the hero cannot gain levels.");
            return new ProgressionRules(ProgressionRules.DefinitionId, int.MaxValue / 8, 0, 0, null, 0,
                                        new KillXpRules(0, 0, 0, 0, null, 1), null, null);
        }

        // ------------------------------------------------------------------------------- session-owned save sections

        /// <summary>A section written and read by the session itself.</summary>
        sealed class SessionSection : ISaveParticipant
        {
            readonly Action<ISaveWriter> _write;
            readonly Action<ISaveReader, int> _read;

            public SessionSection(string section, int version, Action<ISaveWriter> write, Action<ISaveReader, int> read)
            {
                Section = section;
                Version = version;
                _write = write;
                _read = read;
            }

            public string Section { get; }
            public int Version { get; }
            public void Write(ISaveWriter w) => _write(w);
            public void Read(ISaveReader r, int version) => _read(r, version);
        }

        /// <summary>"session.start": the new-game script's alarms still pending (a save made in the first moments of a game).</summary>
        void WriteStart(ISaveWriter w)
        {
            var pending = Start?.PendingAlarms ?? (IReadOnlyList<string>)Array.Empty<string>();
            w.WriteStrings(pending.ToList());
            w.WriteDouble(Start?.PlayedSeconds ?? 0);
        }

        void ReadStart(ISaveReader r, int version)
        {
            var pending = r.ReadStrings();
            float played = (float)r.ReadDouble();
            if (pending.Count > 0 && Content.Database.TryGet(GameStartDef.DefinitionId, out GameStartDef def))
                Start = NewGameStart.Resume(def, pending, played, _log);
        }

        /// <summary>
        /// "session.rules": the rule options the game was saved with. CP 1.75 reads Alternative Balancing, extended AI and
        /// the difficulty from the options (ge3.ini [Game]), not from the save, so a loaded game plays with the current
        /// choice; the stored options only let the session warn when they differ (<see cref="SavedRules"/>).
        /// </summary>
        void WriteRules(ISaveWriter w)
        {
            var o = Settings.Rules;
            w.WriteBool(o.AlternativeBalancing);
            w.WriteBool(o.ExtendedAI);
            w.WriteBool(o.FixPassiveExtendedAi);
            w.WriteInt32(o.XpModifierPercent);
            w.WriteInt32((int)o.Difficulty);
        }

        void ReadRules(ISaveReader r, int version)
        {
            bool ab = r.ReadBool(), ai = r.ReadBool(), fix = r.ReadBool();
            int xp = r.ReadInt32();
            var difficulty = (Difficulty)r.ReadInt32();
            SavedRules = new RuleOptions(ab, ai, fix, xp, difficulty);
            var o = Settings.Rules;
            if (o.AlternativeBalancing != ab || o.ExtendedAI != ai || o.Difficulty != difficulty || o.XpModifierPercent != xp)
                _log.Warning($"The save was made with AB {(ab ? "on" : "off")}, extended AI {(ai ? "on" : "off")}, {difficulty}, XP {xp}%; " +
                             "the game continues with the current options.");
        }

        /// <summary>A new game: the hero gets the content's start kit (a loaded game keeps what its save holds).</summary>
        public static GameSession NewGame(GameContent content, SessionPorts ports, GameSettings settings)
        {
            var session = new GameSession(content, ports, settings);
            session.GiveHeroStartKit();
            session.ApplyGameStart();
            session.LayOutCorpses();
            return session;
        }

        /// <summary>
        /// fix-261: the NPCs the world places as corpses (G3 Rtn_Dead, <see cref="NpcDef.DeadAtStart"/>: Ramirez in his pit
        /// near the Mora Sul temple) are dead from the start; a save made before the fix gets them dead on load.
        /// </summary>
        void LayOutCorpses()
        {
            foreach (var npc in Content.Database.All<NpcDef>())
                if (npc.DeadAtStart && Combat.Vitals.LieDead(npc.Id))
                    _log.Info($"{npc.Id.Name} lies dead (G3 Rtn_Dead)");
        }

        /// <summary>The content's new-game script (Docs/game-start.md): clock, chapter, quests, routines, enclaves.</summary>
        void ApplyGameStart()
        {
            if (!Content.Database.TryGet(GameStartDef.DefinitionId, out GameStartDef def)) return;
            Start = NewGameStart.Apply(def, _clock.HourOfDay, _clock.Skip, Chapter, Quests, Npcs, Enclaves, _log);
        }

        /// <summary>G3's raid condition: the alarming member is alive and politically friendly to its enclave.</summary>
        bool MayAlarm(EnclaveStart e)
        {
            var by = new ActorId(e.AlarmBy);
            if (Combat.Vitals.IsDead(by)) return false;
            return !Crime.Attitudes.Facts.TryGetProfile(by, out var p) || p.Alignment == null || p.EnclaveAlignment == null ||
                   Crime.Attitudes.PoliticallyFriendly(p.Alignment, p.EnclaveAlignment);
        }

        /// <summary>CP OnTick: a humanoid NPC burnt or poisoned below 1 HP dies when killable and Hostile/Predator to the player.</summary>
        bool DiesOfDamageOverTime(ActorId id)
        {
            if (id.IsPlayer || !Actors.TryGet(id, out var actor)) return false;
            var victim = Combat.NpcCombatant(id, CombatWiring.CombatDefOf(actor), Hero.Level);
            return StatusEffects.DiesOfDamageOverTime(Combat.Resolver.CanBeKilled(victim, Hero.Level), Crime.Attitudes.Of(id, ActorId.Player));
        }

        void GiveHeroStartKit()
        {
            if (!Content.Database.TryGet(HeroStartKit.DefinitionId, out HeroStartKit kit)) return;
            HeroStart.Apply(kit, Progression, Items.Inventories.Of(ActorId.Player), Items.Equipment, Items.QuickSlots, _log);
        }

        /// <summary>A session in the state stored in <paramref name="save"/>.</summary>
        public static GameSession Load(GameContent content, SessionPorts ports, GameSettings settings, Stream save)
        {
            var session = new GameSession(content, ports, settings);
            var header = SaveFile.Read(save, session._saveSections, session._log);
            session.WarnIfContentChanged(header);
            session.HeroSpells.Sync(); // a save from before spells were carried: learned spells get their stack
            session.Party.Guides.Resume();
            session.Summoning.AfterLoad();
            session.MigrateRoutineFame();
            session.WorldState.Liberation.MigrateLiberatedOutlawEnclaves();
            session.LayOutCorpses();
            session.MigrateTempleLiberation();
            return session;
        }

        /// <summary>
        /// fix-261 save migration: before the fix the Mora Sul temple could be freed without the hero (the zombies the opened
        /// temple lets in killed the orcs of an enclave left in Attack by an earlier brawl), and its political crime against
        /// Ass turned every Hashishin hostile. A save from before (crime section v1) with the temple fled loses that crime.
        /// </summary>
        void MigrateTempleLiberation()
        {
            const string temple = "MoraSulTemple";
            if (_crimeSection.ReadVersion == 0 || _crimeSection.ReadVersion >= 2) return;
            if (Enclaves.Of(temple).Status != EnclaveStatus.Flee) return;
            string alignment = Crime.Attitudes.Facts is SessionAttitudeFacts facts ? facts.EnclaveAlignmentOf(temple) : "Ass";
            if (Crime.Ledger.RemovePoliticalCrime(alignment ?? "Ass"))
                _log.Info($"save migration (fix-261): the political crime of the {temple} liberation against {alignment ?? "Ass"} is taken back");
        }

        /// <summary>
        /// fix-144: a save from before the Routine fame (fame section v1) gets the 100 that G3's ChangeEnclaveStatus(Routine)
        /// gave every enclave the story already brought back to Routine (a liberated Ardea showed 0).
        /// </summary>
        void MigrateRoutineFame()
        {
            if (_fameSection.ReadVersion == 0 || _fameSection.ReadVersion >= 2) return;
            foreach (var enclave in WorldState.Director.RestoreRoutineFame())
                _log.Info($"save migration (fix-144): enclave fame {enclave} = {StoryWorldDirector.RoutineFame} (G3 ChangeEnclaveStatus Routine)");
        }

        public GameContent Content { get; }
        public GameSettings Settings { get; }
        public IGameClock Clock => _clock;
        /// <summary>
        /// Every roll of the game (fix-119 audit: Docs/gameplay-architecture.md §10 "Randomness"). Like G3's C runtime <c>rand()</c> its state
        /// is not restored from a save: a reloaded game does not replay the rolls made after the save (fix-117 lock picks).
        /// </summary>
        public IRandom Random { get; }
        /// <summary>The seed <see cref="Random"/> started with: the pinned one of the settings, else a fresh one (fix-119).</summary>
        public ulong RandomSeed { get; }
        /// <summary>The settings pinned the seed (a test, a dev tool, a scenario run).</summary>
        public bool RandomSeedPinned => Settings.RandomSeed.HasValue;

        /// <summary>
        /// fix-119: restarts <see cref="Random"/> from <paramref name="seed"/> (the scenario runner pins its run after a fresh Play;
        /// what the session rolled before is kept).
        /// </summary>
        public void Reseed(ulong seed) => Random.Load(SeededRandom.StateFor(seed));
        public IEventBus Events { get; }
        public GameEventFlags Flags { get; }
        public ActorRegistry Actors { get; }
        /// <summary>Takes a runtime actor out of every per-actor store (summons vanishing, dev despawn).</summary>
        public ActorRemoval Removal { get; }
        public QuestService Quests { get; }
        /// <summary>fix-161 [owner] port option "Freeing by force counts" (<see cref="Myrtana.Story.ForcedReleases"/>).</summary>
        public ForcedReleases ForcedReleases { get; }
        public QuestLog QuestLog { get; }
        /// <summary>fix-126 [owner]: where the hero talked to the people of each quest (the map pages' quest marks).</summary>
        public QuestTalkMarks QuestTalks { get; }
        public Hero Hero { get; }
        public ProgressionService Progression { get; }
        public FameLedger Fame { get; }
        /// <summary>Inventories, equipment, world items, trade and teaching.</summary>
        public ItemServices Items { get; }
        public DialogService Dialog { get; }
        public ILocalizer Localizer { get; }
        /// <summary>Subtitles of shared (SVM) lines, with another voice set's text where the speaker's has none (fix-235).</summary>
        public SharedLineTexts SharedLineTexts { get; }
        /// <summary>Daily routines of all NPCs (views: Myrtana.Unity.NpcViewManager).</summary>
        public NpcSimulation Npcs { get; }
        /// <summary>Hit resolution, vitals (HP/SP/MP, knock-out, death) and stamina.</summary>
        public CombatService Combat { get; }
        /// <summary>The NPC combat AI (brains, attack token); the presentation ticks the brains of the NPCs it shows.</summary>
        public NpcCombatSystem NpcCombat { get; }
        /// <summary>Spells, casting and mana, burning and freezing (Docs/combat.md §12).</summary>
        public MagicService Magic { get; }
        /// <summary>The hero's spell book: learned spells and their spell-book items (G3 HasLearnedSpell).</summary>
        public HeroSpells HeroSpells { get; }
        /// <summary>The hero's companions and guide (Docs/party.md); <see cref="PartyServices.JoinCompanion"/> for scripted joins.</summary>
        public PartyServices Party { get; }
        /// <summary>Summoned creatures and their summoners.</summary>
        public SummonGroups Summons { get; }
        /// <summary>Summoning: creatures appear, follow or attack, die on the leash or when replaced (Docs/combat.md §12.2).</summary>
        public SummonService Summoning { get; }
        /// <summary>Perception broadcasts, attitudes, crime and reputation (Docs/perception-crime.md).</summary>
        public CrimeServices Crime { get; }
        /// <summary>fix-158: refusal lines of NPCs with nothing to talk about (G3 StartSayRefuseTalk).</summary>
        public TalkRefusals TalkRefusals { get; }
        /// <summary>Creature and sleeper reactions, witnesses' flight, corpses and the processing-range entry (par-npc).</summary>
        public NpcLifeServices Life { get; }
        /// <summary>The story chapter (political attitudes change in chapter 2).</summary>
        public StoryChapter Chapter { get; }
        /// <summary>Chests, doors, locks, switches and sleeping (Docs/interactive-objects.md).</summary>
        public InteractionServices Interaction { get; }
        /// <summary>Robbing sleeping or spell-slept humans with Perk_MasterThief (CP 1.75 PS_Normal_Loop).</summary>
        public SleeperTheft SleeperTheft => _sleeperTheft ??= new SleeperTheft(this);
        SleeperTheft _sleeperTheft;
        /// <summary>The hero's HP/MP regeneration per second: Master Gladiator/Mage perks, sitting, magic barrier (CP 1.75 OnTick).</summary>
        public HeroRegeneration Regeneration { get; }
        /// <summary>The hero's maps and the map page's choice (Docs/map-teleport.md).</summary>
        public WorldMaps Maps { get; }
        /// <summary>Teleport stones and their destinations (Docs/map-teleport.md).</summary>
        public Teleports Teleports { get; }
        public SpellEffectServices SpellEffects { get; }
        /// <summary>Raiding, fighting or fleeing enclaves (the revolution attitudes).</summary>
        public EnclaveStates Enclaves { get; }
        /// <summary>The new-game script's pending part (the first fight's alarm); null without content, once the alarm went off,
        /// or for a loaded game saved after it.</summary>
        public NewGameStart Start { get; private set; }
        /// <summary>Who the hero can pickpocket and who he has robbed (dialog Pickpocket).</summary>
        public PickpocketService Pickpockets { get; }
        /// <summary>World sectors, place/arena/enclave quest counters, enclave liberation, gate guards (Docs/story-coverage.md).</summary>
        public WorldStateServices WorldState { get; }
        /// <summary>What the hero has read at bookstands (section "world.bookstands").</summary>
        public BookstandMemory Bookstands { get; } = new BookstandMemory();
        /// <summary>Crafting stations, recipes and bookstands (Docs/crafting-docs.md); built on first use.</summary>
        public CraftingServices Crafting => _crafting ??= CraftingServices.Create(this, Bookstands);
        /// <summary>The music situation and the enclaves whose liberation music has played (section "world.music").</summary>
        public MusicSituationRule Music { get; } = new MusicSituationRule();
        /// <summary>The rule options stored in the loaded save (null for a new game or an older save); see section "session.rules".</summary>
        public RuleOptions SavedRules { get; private set; }
        /// <summary>Real seconds played in this game (carried over by saves).</summary>
        public double PlaySeconds => _playTime.Seconds;

        System.Func<EnclaveStart, bool> _mayAlarm;
        System.Func<string, System.Collections.Generic.IReadOnlyList<ActorId>> _raiseAlarm;

        /// <summary>One frame. Order (grows with the slices): clock → … → event flush.</summary>
        public void Tick(float realDeltaSeconds)
        {
            _clock.Advance(realDeltaSeconds);
            _playTime.Advance(realDeltaSeconds);
            Npcs.Tick();
            Life.Tick();
            Combat.Vitals.Tick(realDeltaSeconds);
            Regeneration.Tick(realDeltaSeconds);
            Magic.Status.Tick(realDeltaSeconds);
            Summoning.Tick(realDeltaSeconds);
            SpellEffects.Tick(realDeltaSeconds);
            Crime.Tick();
            if (Start != null)
            {
                // Delegates made once: the method group and the lambda (captures this) allocated every frame (perf-7).
                _mayAlarm ??= MayAlarm;
                _raiseAlarm ??= enclave => Crime.Crime.RaiseEnclaveAlarm(enclave, ActorId.Player);
                Start.Tick(realDeltaSeconds, Enclaves, _mayAlarm, _raiseAlarm);
            }
            Dialog.Tick(realDeltaSeconds);
            WorldState.Tick(realDeltaSeconds);
        }

        /// <summary>Another module's or the host's condition that forbids saving (combat …).</summary>
        public void AddSaveBlocker(ISaveBlocker blocker) => _saveBlockers.Add(blocker);

        /// <summary>Why the game cannot be saved right now, or null when it can.</summary>
        public string SaveBlockReason
        {
            get
            {
                for (int i = 0; i < _saveBlockers.Count; i++)   // no LINQ: asked by the save timers (perf-3)
                    if (_saveBlockers[i].SaveBlockReason is string reason) return reason;
                return null;
            }
        }

        /// <summary>
        /// Writes the game to <paramref name="output"/>. Callers check <see cref="SaveBlockReason"/> first; saving while
        /// blocked throws, because a half-finished conversation or fight is not part of the saved state.
        /// </summary>
        public void Save(Stream output, string title, DateTime savedAtUtc) => Save(output, title, savedAtUtc, null);

        /// <summary><see cref="Save(Stream, string, DateTime)"/>; <paramref name="timings"/>: each section's write time, ms (fix-166).</summary>
        public void Save(Stream output, string title, DateTime savedAtUtc, IList<KeyValuePair<string, double>> timings)
        {
            string blocked = SaveBlockReason;
            if (blocked != null) throw new InvalidOperationException($"The game cannot be saved now ({blocked}).");
            var packs = Content.Database.Packs.Select(p => new PackStamp(p.Id, p.ContentHash)).ToList();
            SaveFile.Write(output, new SaveHeader(packs, _playTime.Seconds, title, savedAtUtc), _saveSections, timings);
        }

        /// <summary>
        /// fix-171: the main-thread half of <see cref="Save(Stream, string, DateTime, IList{KeyValuePair{string, double}})"/> for the
        /// crash-recovery snapshot (<see cref="SaveFile.Prepare"/>): heavy sections only copy their state; the returned save is
        /// written by its WriteTo on a worker. <paramref name="bodies"/> is the caller's reusable buffer.
        /// </summary>
        public SaveFile.PendingSave PrepareSave(string title, DateTime savedAtUtc, MemoryStream bodies, IList<KeyValuePair<string, double>> timings = null)
        {
            string blocked = SaveBlockReason;
            if (blocked != null) throw new InvalidOperationException($"The game cannot be saved now ({blocked}).");
            var packs = Content.Database.Packs.Select(p => new PackStamp(p.Id, p.ContentHash)).ToList();
            return SaveFile.Prepare(new SaveHeader(packs, _playTime.Seconds, title, savedAtUtc), _saveSections, bodies, timings);
        }

        void WarnIfContentChanged(SaveHeader header)
        {
            foreach (var stamp in header.Packs)
            {
                var pack = Content.Database.Packs.FirstOrDefault(p => p.Id == stamp.Id);
                if (pack == null) _log.Warning($"The save was made with content pack '{stamp.Id}', which is not loaded.");
                else if (pack.ContentHash != stamp.ContentHash) _log.Warning($"Content pack '{stamp.Id}' changed since the save was made.");
            }
        }
    }
}
