using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Items;
using G3.Formats.Pak;
using G3.Formats.Templates;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-153 content test over the real Gothic 3 data (skipped without an installation) [owner, Demo_2026-10-19: "I give
    /// an orc schnapps in a talk and he does not drink it"]. CP 1.75 has the drinker drink through the info command
    /// <c>Use</c> (IS_Running_ProcessInfo case 0xF: AssureItems, then the latent <c>_AI_UseInventoryItem</c>: hold the item,
    /// play the Use motion — the info waits —, for an NPC RefreshHitPoints and no ConsumeItems). Every one of the 23 Use
    /// lines must reach the presentation (<see cref="ActorUsedItem"/>) and wait, and every NPC user's rig must have the
    /// standing use motions of the item's use type in the G3 animation pak.
    /// </summary>
    public class DialogItemUseTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly ActorId Mojok = new ActorId("Mojok");
        static readonly ActorId Barkeeper = new ActorId("Montera_BarKeeper_01");

        static string _data, _pack;
        static HashSet<string> _motions;
        static ItemCatalog _templates;
        GameSession _session;
        RecordingPresenter _presenter;
        List<ActorUsedItem> _used;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            _data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(_data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + _data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_item_use_pack");
            G3ContentImporter.Run(_data, _pack);
            using (var fs = new G3FileSystem(_data, "_compiledAnimation"))
                _motions = new HashSet<string>(fs.Files.Select(f => Path.GetFileNameWithoutExtension(f.Path)), StringComparer.OrdinalIgnoreCase);
            using (var fs = new G3FileSystem(_data, "Templates"))
                _templates = ItemCatalog.Build(TemplateArchive.Load(fs));
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            if (_pack != null && Directory.Exists(_pack)) Directory.Delete(_pack, true);
        }

        [SetUp]
        public void NewGame()
        {
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(_pack) });
            _presenter = new RecordingPresenter();
            _session = GameSession.NewGame(content, new SessionPorts(_presenter, null), new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 1));
            _used = new List<ActorUsedItem>();
            _session.Events.Subscribe<ActorUsedItem>(_used.Add);
        }

        IEnumerable<(DialogEntryDef entry, UseItem use)> Uses() =>
            _session.Content.Database.All<DialogEntryDef>().SelectMany(e => e.Commands.OfType<UseItem>().Select(u => (e, u)));

        ActorId User(DialogEntryDef entry, UseItem use) => use.Actor.Resolve(entry.Owner);

        /// <summary>The NPC motion rig of an actor: humans share the hero's skeleton and motions.</summary>
        string RigOf(ActorId actor) =>
            _session.Content.Database.TryGet(actor.Name, out NpcDef npc) && string.Equals(npc.Species, "Orc", StringComparison.OrdinalIgnoreCase) ? "Orc" : "Hero";

        [Test]
        public void TheInfos_Use23Items_18ByNpcs()
        {
            var uses = Uses().ToList();
            Assert.That(uses.Count, Is.EqualTo(23), string.Join(", ", uses.Select(u => u.entry.Id.Name)));
            var npc = uses.Where(u => !User(u.entry, u.use).IsPlayer).ToList();
            Assert.That(npc.Count, Is.EqualTo(18));
            Assert.That(npc.Count(u => u.use.Item == "It_Letter"), Is.EqualTo(10), "read letters");
            Assert.That(npc.Where(u => RigOf(User(u.entry, u.use)) == "Orc").Select(u => u.entry.Owner.Name), Is.EqualTo(new[] { "Mojok" }),
                        "the only orc who drinks in an info: Mojok (SKALVERAM3482); Gnar and Gorok only take the schnapps (G3 data)");
        }

        [Test]
        public void GnarAndGorok_OnlyTakeTheSchnapps_AsInG3()
        {
            foreach (var id in new[] { "SKALVERAM3483", "SKALVERAM3481" })
            {
                Assert.That(_session.Content.Database.TryGet(id, out DialogEntryDef e), Is.True, id);
                Assert.That(e.Commands.OfType<GiveItems>().Single().Item, Is.EqualTo("It_Booze"), id);
                Assert.That(e.Commands.OfType<UseItem>(), Is.Empty, id + ": the CP 1.75 info has no Use line");
            }
        }

        [Test]
        public void EveryNpcUser_HasTheStandingUseMotions_OfTheItemsUseType()
        {
            foreach (var (entry, use) in Uses())
            {
                var user = User(entry, use);
                if (user.IsPlayer) continue;
                var template = _templates.Get(use.Item);
                Assert.That(template, Is.Not.Null, $"{entry.Id.Name}: template {use.Item}");
                string field = template.UseType.ToString(); // the hand field of the motion names (gCInteraction_PS.UseType)
                if (_session.Content.Database.TryGet(use.Item, out ItemDef def))
                    Assert.That(def.Kind, Is.EqualTo(field), $"{entry.Id.Name}: the presentation's hand field (ItemDef.Kind) of {use.Item}");
                else
                    Assert.That(use.Item, Is.EqualTo("It_Letter"), $"{entry.Id.Name}: only the letter is a prop (G3HandItems: It_Letter = Letter)");
                string rig = RigOf(user);
                foreach (var phase in new[] { "HoldRight_Begin", "Use_Begin", "Use_End", "HoldRight_End" })
                {
                    string motion = $"{rig}_Stand_None_{field}_P0_{phase}_N_Fwd_00_%_00_P0_0";
                    Assert.That(_motions.Contains(motion), Is.True, $"{entry.Id.Name} {user} {use.Item}: {motion}");
                }
            }
        }

        List<string> Lines()
        {
            var shown = new List<string>();
            while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine)
            {
                shown.Add(_presenter.Lines.Last().Voice ?? "");
                _session.Dialog.CompleteLine();
            }
            return shown;
        }

        void Choose(string id)
        {
            Assert.That(_session.Dialog.IsActive && _session.Dialog.Current.Options.Any(o => o.Entry.Name == id), Is.True, id + " offered");
            _session.Dialog.Choose(new Id<DialogEntryDef>(id));
        }

        [Test]
        public void Mojok_DrinksTheSchnapps_TheTalkWaitsForIt_ThenGoesOn()
        {
            _session.Quests.TrySetStatus(new Id<QuestDef>("Flint_BoozeDelivery"), QuestStatus.Running);
            _session.Items.Inventories.Give(ActorId.Player, "It_Booze", 1);
            _session.Dialog.Talk(Mojok);
            Lines();
            Choose("SKALVERAM3482");
            Lines();

            Assert.That(_used.Select(u => (u.Actor, u.Item)), Is.EqualTo(new[] { (Mojok, "It_Booze") }));
            Assert.That(_session.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "G3 _AI_UseInventoryItem is latent");
            Assert.That(_session.Items.Inventories.Of(Mojok).Count("It_Booze"), Is.EqualTo(1), "G3 consumes only the player's item");
            Assert.That(_session.Items.Inventories.Of(ActorId.Player).Count("It_Booze"), Is.Zero);

            _session.Dialog.Tick(UseItemHandler.NpcUseSeconds - 0.5f);
            Assert.That(_session.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "still drinking");
            _session.Dialog.Tick(0.6f);
            var after = Lines();
            Assert.That(after.Count, Is.EqualTo(2), "Mojok's two lines come after the drink");
            Assert.That(after.All(v => v.Contains("Mojok")), Is.True, string.Join(", ", after));
            Assert.That(_session.Flags.IsSet("Mojok_Booze"), Is.True);
        }

        [Test]
        public void MonteraBarkeeper_TheHeroDrinks_WithTheMotion()
        {
            _session.Flags.Set("MonteraBarKeeper01_Drink");
            _session.Items.Inventories.Give(ActorId.Player, "It_Gold", 10);
            _session.Dialog.Talk(Barkeeper);
            Lines();
            Choose("BPANKRATZ31027");
            Lines();

            Assert.That(_used.Select(u => (u.Actor, u.Item)), Is.EqualTo(new[] { (ActorId.Player, "It_Booze") }));
            Assert.That(_session.Items.Inventories.Of(ActorId.Player).Count("It_Booze"), Is.Zero, "the hero's drink is used up");
            if (_session.Dialog.IsActive)
            {
                Assert.That(_session.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "the talk waits for the hero's drink");
                _session.Dialog.Tick(UseItemHandler.HeroUseSeconds + 0.1f);
                Assert.That(_session.Dialog.Current == null || _session.Dialog.Current.Phase != ConversationPhase.AwaitingTime, Is.True);
            }
            Assert.That(_session.Flags.IsSet("MonteraBarKeeper01_Drink"), Is.False);
        }
    }
}
