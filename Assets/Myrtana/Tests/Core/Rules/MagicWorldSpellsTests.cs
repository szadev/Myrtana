using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Sleep, control, charm, bloodlust, call animals, mist, time bubble, hero forms and weapon blessing (Docs/combat.md §12.5, CP 1.75).</summary>
    public class MagicWorldSpellsTests
    {
        sealed class World
        {
            public readonly EventBus Events = new EventBus();
            public readonly ActorRegistry Actors;
            public readonly NpcSimulation Npcs;
            public readonly PlayerParty Party;
            public readonly ActorVitals Vitals;
            public readonly NpcCombatSystem Combat;
            public readonly WorldSpells Spells;
            public readonly Hero Hero;
            public readonly ProgressionService Progression;
            public readonly GameClock Clock = new GameClock(10, timeScale: 12);
            public readonly HeroForms Forms;
            public readonly CrimeLedger Ledger = new CrimeLedger();
            public readonly List<CombatShout> Lines = new List<CombatShout>();
            public readonly List<NpcDef> Defs;
            public WorldPose Player = new WorldPose(0, 0, 0, 0);
            public Attitude MistAttitude = Attitude.Friendly;

            public World(RuleOptions options = null)
            {
                Defs = new List<NpcDef>
                {
                    Npc("Farmer#1", new NpcCombatDef(5, 10, "Human", humanoid: true), 3, 0),
                    Npc("Farmer#2", new NpcCombatDef(5, 10, "Human", humanoid: true), 6, 0),
                    Npc("Boss#1", new NpcCombatDef(30, 40, "Human", humanoid: true, boss: true), 4, 4),
                    Npc("Orc#1", new NpcCombatDef(20, 30, "Orc", humanoid: true), 50, 50),
                    Npc("Wolf#1", new NpcCombatDef(5, 10, "Wolf", humanoid: false, animal: true), 30, 0),
                    Npc("Wolf#2", new NpcCombatDef(5, 10, "Wolf", humanoid: false, animal: true), 40, 0),
                    Npc("Shadowbeast#1", new NpcCombatDef(25, 30, "Shadowbeast", humanoid: false, animal: true), 60, 0),
                    Npc("Deer#1", new NpcCombatDef(1, 2, "Deer", humanoid: false, animal: true), 20, 0),
                    Npc("Skeleton#1", new NpcCombatDef(10, 20, "Skeleton", humanoid: true, evil: true), 0, 5),
                };
                var db = new ContentDatabase();
                foreach (var a in TestRules.Attributes()) db.Put(a);
                options ??= RuleOptions.Default;
                Hero = Hero.CreateNew(db, TestRules.Progression(), options);
                Progression = new ProgressionService(Hero, TestRules.Progression(), options, Events);
                Actors = new ActorRegistry(Defs);
                Npcs = new NpcSimulation(Defs, new RoutinePointCatalog(new List<RoutinePointDef>(), new List<AnchorDef>()),
                                         new RoutineSchedule(G3RoutineTables.Schedule()), Clock, new SeededRandom(1));
                Party = new PlayerParty(new ActorRoles(new ContentDatabase()), Events);
                Vitals = new ActorVitals(Hero.Stats, id => Actors.TryGet(id, out var s) && s.Definition.Combat != null
                    ? s.Definition.Combat.Profile(Difficulty.Medium) : null, TestRules.Combat(), Events);
                Combat = new NpcCombatSystem(TestRules.CombatAi(), TestRules.Combat(), options, new SeededRandom(1),
                                             id => Actors.TryGet(id, out var s) ? s.Definition.Combat : null, null, () => 0, () => 0, Events);
                Spells = new WorldSpells(Actors, Npcs, Combat, Vitals, Party, new SeededRandom(1), Events, null)
                {
                    Positions = id => id.IsPlayer ? Player : Npcs.TryGet(id, out var a) ? Npcs.PoseOf(a) : (WorldPose?)null,
                    Attitudes = (self, other) => MistAttitude,
                    Ledger = Ledger,
                };
                Combat.Paralysed = Spells.Paralysed;
                Forms = new HeroForms(Hero, Progression, Vitals, Clock, Combat, options, Events, null);
                Events.Subscribe<CombatShout>(Lines.Add);
            }

            public NpcDef Def(string id) => Defs.First(d => d.Id.Name == id);

            public bool Fights(string npc, out ActorId target)
            {
                var brain = Combat.Get(new ActorId(npc));
                target = brain != null && brain.IsFighting ? brain.State.Target : default;
                return brain != null && brain.IsFighting;
            }

            static NpcDef Npc(string id, NpcCombatDef combat, float x, float z) =>
                new NpcDef(new ActorId(id), new TextId("FO_" + id), null, combat.Species, combat.Species + "_Body_01", new WorldPose(x, 0, z, 0),
                           combat: combat);
        }

        static readonly ActorId Farmer = new ActorId("Farmer#1"), Farmer2 = new ActorId("Farmer#2"), Boss = new ActorId("Boss#1"),
                                Orc = new ActorId("Orc#1"), Wolf = new ActorId("Wolf#1"), Wolf2 = new ActorId("Wolf#2"),
                                Beast = new ActorId("Shadowbeast#1"), Deer = new ActorId("Deer#1"), Skeleton = new ActorId("Skeleton#1");

        // ------------------------------------------------------------------------------------------- sleep

        [Test]
        public void Sleep_HoldsSleepersFor30Seconds_UndeadNeverSleep_HitsDoNotWake()
        {
            var w = new World();
            Assert.That(w.Spells.Sleep(Skeleton), Is.False, "CanSleep: no undead");
            Assert.That(w.Spells.Sleep(Farmer), Is.True);
            Assert.That(w.Spells.Paralysed(Farmer), Is.True);
            Assert.That(w.Combat.Engage(Farmer, ActorId.Player, AttackReason.ReactToDamage), Is.False, "asleep: no fight (AIMode 7)");
            w.Spells.Tick(29f);
            Assert.That(w.Spells.IsAsleep(Farmer), Is.True);
            w.Spells.Tick(1.5f);
            Assert.That(w.Spells.IsAsleep(Farmer), Is.False, "NPCs wake after 30 s");
            Assert.That(w.Lines.Any(l => l.Speaker == Farmer && l.Line == "WhatWasThat"), Is.True);

            Assert.That(w.Spells.Sleep(ActorId.Player), Is.True, "the player can be slept");
            w.Spells.Tick(5.1f);
            Assert.That(w.Spells.IsAsleep(ActorId.Player), Is.False, "the player wakes after 5 s");
        }

        [Test]
        public void Hypnosis_PutsCalmNpcsWithin20mToSleep()
        {
            var w = new World();
            w.Combat.Engage(Farmer2, Wolf, AttackReason.Prey);
            var slept = w.Spells.Hypnosis(ActorId.Player);
            Assert.That(slept, Is.SupersetOf(new[] { Farmer, Boss }), "within 20 m and able to sleep");
            Assert.That(slept, Has.No.Member(Farmer2), "fighting NPCs are skipped");
            Assert.That(slept, Has.No.Member(Skeleton), "undead cannot sleep");
            Assert.That(slept, Has.No.Member(Wolf), "30 m away");
        }

        // ------------------------------------------------------------------------------------------- control

        [Test]
        public void WordOfControl_JoinsAsControlled_OneGroupAtATime_OrcsNeedInt200()
        {
            var w = new World();
            Assert.That(w.Spells.WordOfControl(ActorId.Player, Orc, 150), Is.EqualTo(ControlOutcome.TooHard));
            Assert.That(w.Lines.Last().Line, Is.EqualTo("TooHard"));
            Assert.That(w.Spells.WordOfControl(ActorId.Player, Boss, 300), Is.EqualTo(ControlOutcome.Impossible));
            Assert.That(w.Spells.WordOfControl(ActorId.Player, Farmer, 0), Is.EqualTo(ControlOutcome.Works));
            Assert.That(w.Party.TypeOf(Farmer), Is.EqualTo(PartyMemberType.Controlled));
            var messages = new List<PartyChanged>();
            w.Events.Subscribe<PartyChanged>(messages.Add);
            Assert.That(w.Spells.TameAnimal(ActorId.Player, Wolf, 0), Is.EqualTo(ControlOutcome.Works));
            Assert.That(w.Party.TryGet(Farmer, out _), Is.False, "the previous controlled NPC is released");
            Assert.That(w.Party.TypeOf(Wolf), Is.EqualTo(PartyMemberType.Controlled));
            Assert.That(messages.Any(m => m.Member == Farmer && m.Message.Value == "GO_ControlDismiss"), Is.True);
            Assert.That(messages.Any(m => m.Member == Wolf && m.Message.Value == "GO_ControlJoin"), Is.True);
            Assert.That(w.Spells.TameAnimal(ActorId.Player, Beast, 50), Is.EqualTo(ControlOutcome.TooHard), "strong animals need INT 100");
            Assert.That(w.Spells.TameAnimal(ActorId.Player, Farmer, 300), Is.EqualTo(ControlOutcome.Impossible), "not an animal");
        }

        [Test]
        public void Charm_ForgetsCrimesAndFights_BossesAttack()
        {
            var w = new World();
            w.Ledger.RememberPersonally(Farmer, CrimeType.Theft);
            w.Ledger.RecordFight(Farmer, FightResult.Cancelled, AttackReason.Theft, 1);
            Assert.That(w.Spells.Charm(ActorId.Player, Farmer), Is.EqualTo(ControlOutcome.Works));
            Assert.That(w.Ledger.PersonalOf(Farmer), Is.EqualTo(CrimeType.None));
            Assert.That(w.Ledger.FightOf(Farmer).Result, Is.EqualTo(FightResult.None));
            Assert.That(w.Spells.Charm(ActorId.Player, Boss), Is.EqualTo(ControlOutcome.AttacksCaster));
            Assert.That(w.Fights("Boss#1", out var target) && target.IsPlayer, Is.True);
            Assert.That(w.Spells.Charm(ActorId.Player, Boss), Is.EqualTo(ControlOutcome.DoesntWork), "fighting: doesn't work");
        }

        // ------------------------------------------------------------------------------------------- bloodlust, call animals

        [Test]
        public void Bloodlust_AttacksTheNearestNpc_CallAnimals_SendsTheNearestFighters()
        {
            var w = new World();
            Assert.That(w.Spells.Bloodlust(ActorId.Player, Farmer2), Is.EqualTo(Farmer), "Farmer#1 is 3 m from Farmer#2");
            Assert.That(w.Fights("Farmer#2", out var t) && t == Farmer, Is.True);
            Assert.That(w.Spells.Bloodlust(ActorId.Player, Boss), Is.EqualTo(ActorId.Player), "bosses attack the caster");

            var called = w.Spells.CallAnimals(ActorId.Player, Orc, 250);
            Assert.That(called, Is.EqualTo(new[] { Wolf, Wolf2 }), "INT 250: two; the deer never fights");
            Assert.That(w.Fights("Wolf#1", out var wt) && wt == Orc, Is.True);
            Assert.That(w.Party.TryGet(Wolf, out _), Is.False, "called animals do not join");
            Assert.That(SpellEffectRules.CallAnimalsCount(301), Is.EqualTo(3));
            Assert.That(SpellEffectRules.CallAnimalsCount(200), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------- mist, time bubble

        [Test]
        public void Mist_StopsFriendsAndProvokesOthers_EndsAfter20s_AndOnTeleport()
        {
            var w = new World();
            w.Combat.Engage(Farmer, Wolf, AttackReason.Prey);
            var mist = w.Spells.SummonMist(ActorId.Player, new WorldPose(0, 0, 0, 0), deityPerk: false);
            Assert.That(mist.Remaining, Is.EqualTo(20f));
            Assert.That(w.Spells.InMist(Farmer), Is.True, "friendly to the caster: stands misted");
            Assert.That(w.Fights("Farmer#1", out _), Is.False, "the fight stops");
            Assert.That(w.Spells.InMist(Wolf), Is.False, "30 m: outside");
            w.MistAttitude = Attitude.Neutral;
            w.Spells.EndMist();
            Assert.That(w.Spells.InMist(Farmer), Is.False);
            w.Spells.SummonMist(ActorId.Player, new WorldPose(0, 0, 0, 0), deityPerk: true);
            Assert.That(w.Spells.Mist.Remaining, Is.EqualTo(25f), "+5 s with the deity perk");
            Assert.That(w.Fights("Farmer#1", out var target) && target.IsPlayer, Is.True, "not friendly: attacks the caster");
            w.Spells.Tick(26f);
            Assert.That(w.Spells.Mist, Is.Null);

            w.Spells.StartTimeBubble(ActorId.Player, new WorldPose(0, 0, 0, 0), deityPerk: false);
            Assert.That(w.Spells.TimeBubble.Remaining, Is.InRange(80f, 110f));
            Assert.That(w.Spells.TimeScaleOf(Farmer, new WorldPose(3, 0, 0, 0)), Is.EqualTo(0.5f));
            Assert.That(w.Spells.TimeScaleOf(ActorId.Player, new WorldPose(0, 0, 0, 0)), Is.EqualTo(1f), "not the caster");
            Assert.That(w.Spells.TimeScaleOf(Wolf, new WorldPose(30, 0, 0, 0)), Is.EqualTo(1f), "outside");
            w.Events.Publish(new TeleportRequested(null, "It_TeleportStone"));
            Assert.That(w.Spells.TimeBubble, Is.Null, "CP: a teleport ends the hero's time bubble");
        }

        // ------------------------------------------------------------------------------------------- hero forms

        [Test]
        public void Transform_LastsByInt_LevelUpWaitsForTheReturn_OneLevelOnly()
        {
            var w = new World();
            Assert.That(w.Forms.Transform(w.Def("Deer#1"), 100, artefact: false), Is.True, "deer is an animal (only Animal Form's list excludes it)");
            w.Forms.TransformBack();
            Assert.That(w.Forms.Transform(w.Def("Farmer#1"), 100, artefact: false), Is.False, "not an animal: CAST_FAIL");
            Assert.That(w.Forms.Transform(w.Def("Wolf#1"), 100, artefact: false), Is.True);
            Assert.That(w.Forms.Remaining, Is.EqualTo(100f), "60 + (INT − 60)");
            Assert.That(w.Forms.PerceivedSpecies, Is.EqualTo("Wolf"));
            Assert.That(w.Forms.Restricted && w.Forms.SaveBlockReason != null, Is.True);
            int level = w.Hero.Level;
            w.Progression.AwardXp(1_000_000);
            Assert.That(w.Hero.Level, Is.EqualTo(level), "no level up while transformed");
            w.Forms.Tick(99f);
            Assert.That(w.Forms.Transformed, Is.True);
            w.Forms.Tick(1.5f);
            Assert.That(w.Forms.Transformed, Is.False, "the timer turns him back");
            Assert.That(w.Hero.Level, Is.EqualTo(level + 1), "at most one level on the return");

            Assert.That(w.Forms.Transform(w.Def("Shadowbeast#1"), 100, artefact: false), Is.True);
            Assert.That(w.Forms.Remaining, Is.EqualTo(50f), "large forms last half as long");
            Assert.That(w.Forms.Confirm(), Is.True, "the confirm key turns him back");
            Assert.That(w.Forms.Transform(w.Def("Wolf#1"), 100, artefact: true), Is.True);
            w.Forms.Tick(10_000f);
            Assert.That(w.Forms.Transformed, Is.True, "a druid stone lasts until cancelled");
        }

        [Test]
        public void SoulTravel_EndsFights_DuskToDawn_RacesToEightAndRefills()
        {
            var w = new World();
            w.Combat.Engage(Farmer, ActorId.Player, AttackReason.Theft);
            Assert.That(w.Forms.SoulTravel(), Is.True);
            Assert.That(w.Fights("Farmer#1", out _), Is.False, "nobody fights the ghost");
            Assert.That(w.Forms.Transform(w.Def("Wolf#1"), 100, false), Is.False, "one form at a time");
            Assert.That(w.Forms.Confirm(), Is.True);
            Assert.That(w.Forms.Kind, Is.EqualTo(HeroFormKind.Hero));

            w.Clock.Restore(22 * 3600);
            w.Vitals.AddHitPoints(ActorId.Player, -100);
            Assert.That(w.Forms.StartDuskToDawn(), Is.True);
            Assert.That(w.Clock.TimeScale, Is.EqualTo(3840f * 0.3f).Within(0.01f));
            for (int i = 0; i < 100 && w.Forms.DuskToDawn; i++)
            {
                w.Clock.Advance(1f);
                w.Forms.Tick(1f);
            }
            Assert.That(w.Forms.DuskToDawn, Is.False);
            Assert.That(w.Clock.HourOfDay, Is.InRange(8.0, 8.5));
            Assert.That(w.Clock.TimeScale, Is.EqualTo(12f));
            Assert.That(w.Vitals.HitPoints(ActorId.Player), Is.EqualTo(w.Vitals.HitPointsMax(ActorId.Player)));
            Assert.That(SpellCasting.PreCheck(Spell("Spl_DuskToDawn", "MagicDuskToDawn"), true, 100, false, false, 12.0), Is.EqualTo(CastRefusal.DoesntWork));
        }

        [Test]
        public void AnimalForm_PreCheck_RefusesTargetsOutsideTheList()
        {
            var spell = Spell("Spl_AnimalForm", "MagicAnimalForm", SpellTarget.Focus);
            Assert.That(SpellCasting.PreCheck(spell, true, 100, false, true, 12, "Deer"), Is.EqualTo(CastRefusal.DoesntWork));
            Assert.That(SpellCasting.PreCheck(spell, true, 100, false, true, 12, "Wolf"), Is.EqualTo(CastRefusal.None));
            Assert.That(SpellCasting.PreCheck(spell, true, 100, false, true, 12, "Meatbug"), Is.EqualTo(CastRefusal.None), "CP: passes, then CAST_FAIL");
            Assert.That(TransformRules.CanTransformInto("Meatbug"), Is.False);
        }

        [Test]
        public void DestroyEvil_PreCheck_NeedsAnEvilFocus()
        {
            var spell = Spell(SpellCasting.DestroyEvil, "MagicDestroyEvil", SpellTarget.Focus);
            Assert.That(SpellCasting.PreCheck(spell, true, 100, false, true, 12, "Skeleton", focusEvil: true), Is.EqualTo(CastRefusal.None));
            Assert.That(SpellCasting.PreCheck(spell, true, 100, false, true, 12, "Wolf", focusEvil: false), Is.EqualTo(CastRefusal.DoesntWork));
            Assert.That(SpellCasting.PreCheck(spell, true, 100, false, false, 12), Is.Not.EqualTo(CastRefusal.None), "CP: IsEvil(None) fails");
            Assert.That(SpellCasting.PreCheck(spell, false, 0, false, true, 12, "Wolf"), Is.EqualTo(CastRefusal.None), "NPC casters skip the checks");
        }

        // ------------------------------------------------------------------------------------------- locks, weapons

        [Test]
        public void OpenLocks_OpensEasyLocksOnly()
        {
            Assert.That(SpellEffectRules.OpenLocks(true, true, 4, false), Is.EqualTo(LockSpellOutcome.Opens));
            Assert.That(SpellEffectRules.OpenLocks(true, true, 5, false), Is.EqualTo(LockSpellOutcome.Impossible));
            Assert.That(SpellEffectRules.OpenLocks(true, true, 0, true), Is.EqualTo(LockSpellOutcome.Impossible), "key-only lock");
            Assert.That(SpellEffectRules.OpenLocks(true, false, 1, false), Is.EqualTo(LockSpellOutcome.DoesntWork));
            Assert.That(SpellEffectRules.OpenLocks(false, false, 0, false), Is.EqualTo(LockSpellOutcome.DoesntWork));
        }

        [Test]
        public void BlessAndPoison_OneItem_ScrollOrMana_RefusesMagicAndRepeats_ArrowBatches()
        {
            var db = new ContentDatabase();
            db.Put(Item("Sword", ItemCategory.Weapon, "OneHanded", "Blade"));
            db.Put(Item("Club", ItemCategory.Weapon, "OneHanded", "Impact"));
            db.Put(Item("FireSword", ItemCategory.Weapon, "OneHanded", "Blade", quality: 8));
            db.Put(Item("Arrow", ItemCategory.Weapon, "Arrow", "Missile"));
            db.Put(Item(SpellEffectRules.BlessScroll, ItemCategory.Artefact, "Modify", null));
            db.Put(Item(SpellEffectRules.BlessSpellItem, ItemCategory.Spellbook, "Modify", null));
            db.Put(Item(SpellEffectRules.PoisonPotion, ItemCategory.Alchemy, "Modify", null));
            db.Put(new RecipeDef(SpellEffectRules.PoisonArrowRecipe, CraftKind.Alchemy, null, "PoisonArrow", 10, 4, null));
            var hero = new Inventory();
            hero.Add("Sword", 2);
            hero.Add("Club", 1);
            hero.Add("FireSword", 1);
            hero.Add("Arrow", 25);
            hero.Add(SpellEffectRules.BlessScroll, 1);
            hero.Add(SpellEffectRules.BlessSpellItem, 1);
            hero.Add(SpellEffectRules.PoisonPotion, 2);
            int mana = 150;
            bool perk = false;
            var t = new WeaponTreatments(db, hero, s => perk && s == SpellEffectRules.PoisonPerk, () => mana, m => mana -= m, null);

            Assert.That(t.Candidates(TreatmentKind.Bless).Select(s => s.Item), Is.EquivalentTo(new[] { "Sword", "Club", "FireSword" }));
            Assert.That(t.Candidates(TreatmentKind.Poison).Select(s => s.Item), Is.EquivalentTo(new[] { "Sword", "FireSword", "Arrow" }));
            Assert.That(t.CanOpen(SpellEffectRules.PoisonPotion), Is.EqualTo(TreatmentVerdict.MissingSkill));
            perk = true;
            Assert.That(t.CanOpen(SpellEffectRules.PoisonPotion), Is.EqualTo(TreatmentVerdict.Done));

            var sword = hero.Stacks.First(s => s.Item == "Sword");
            Assert.That(t.Apply(SpellEffectRules.BlessScroll, sword, out int n), Is.EqualTo(TreatmentVerdict.Done));
            Assert.That(n, Is.EqualTo(1));
            Assert.That(hero.Has(SpellEffectRules.BlessScroll), Is.False, "the scroll is used up");
            var blessed = hero.Stacks.First(s => s.Item == "Sword" && (s.Quality & 64) != 0);
            Assert.That(blessed.Amount, Is.EqualTo(1), "one item of the stack");
            Assert.That(t.Apply(SpellEffectRules.BlessSpellItem, blessed, out _), Is.EqualTo(TreatmentVerdict.AlreadyBlessed));
            Assert.That(t.Apply(SpellEffectRules.BlessSpellItem, hero.Stacks.First(s => s.Item == "FireSword"), out _), Is.EqualTo(TreatmentVerdict.AlreadyMagic));
            Assert.That(t.Apply(SpellEffectRules.BlessSpellItem, hero.Stacks.First(s => s.Item == "Club"), out _), Is.EqualTo(TreatmentVerdict.Done));
            Assert.That(mana, Is.EqualTo(50), "the spell costs 100 MP");
            Assert.That(t.Check(SpellEffectRules.BlessSpellItem, hero.Stacks.First(s => s.Item == "Sword" && s.Quality == 0)), Is.EqualTo(TreatmentVerdict.MissingMana));

            Assert.That(t.Apply(SpellEffectRules.PoisonPotion, hero.Stacks.First(s => s.Item == "Arrow"), out int arrows), Is.EqualTo(TreatmentVerdict.Done));
            Assert.That(arrows, Is.EqualTo(10), "CP: a batch of the recipe's size");
            Assert.That(hero.Stacks.First(s => s.Item == "Arrow" && (s.Quality & 4) != 0).Amount, Is.EqualTo(10));
            Assert.That(t.Apply(SpellEffectRules.PoisonPotion, blessed, out _), Is.EqualTo(TreatmentVerdict.Done), "blessed and poisoned can go together");
            Assert.That(hero.Has(SpellEffectRules.PoisonPotion), Is.False);
            Assert.That(WeaponTreatments.KindOf("Sword"), Is.EqualTo(TreatmentKind.None));
        }

        static ItemDef Item(string id, ItemCategory category, string kind, string damage, int quality = 0) =>
            new ItemDef(id, new TextId("FO_" + id), default, category, kind, EquipSlot.None, 10, quality, null, null, null, null, null, null, null,
                        damage != null ? new ItemDamage(damage, 50, default) : null, ItemFlags.None);

        static SpellDef Spell(string id, string effect, SpellTarget target = SpellTarget.Caster) =>
            new SpellDef(id, new[] { "It_" + id }, null, SpellCastType.Regular, target, SpellKind.Target, SpellDeity.Adanos, 40, 40,
                         SpellDelivery.Instant, effect, null, 0, null, null, null);
    }
}
