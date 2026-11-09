using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-110 audit: every spell an item or scroll casts has a route that carries it out and a G3 effect that shows it
    /// (owner: the Mist and Sleep scrolls spent mana and did nothing).
    /// </summary>
    public class SpellRoutesTests
    {
        static readonly MagicRules Rules = G3MagicImporter.Rules();

        static SpellDef Spell(string id, string effect, SpellDelivery delivery = SpellDelivery.Instant, string spawn = null,
                              SpellVisuals visuals = null, SpellTarget target = SpellTarget.Focus) =>
            new SpellDef(id, new[] { "It_" + id }, new[] { "It_Scroll_" + id }, SpellCastType.Regular, target, SpellKind.Target,
                         SpellDeity.Adanos, 10, 10, delivery, effect, null, 0, spawn, null, visuals);

        static SpellVisuals Cast(string caster, string target = null) => new SpellVisuals(null, null, null, null, null, caster, target);

        [Test]
        public void Of_RoutesEveryKindOfSpell()
        {
            Assert.That(SpellRoutes.Of(Spell("Spl_Sleep", "MagicSleep"), Rules), Is.EqualTo(SpellRoute.World));
            Assert.That(SpellRoutes.Of(Spell("Spl_SummonMist", "MagicSummonMist", SpellDelivery.Summon, "Smn_Mist"), Rules),
                        Is.EqualTo(SpellRoute.World), "the mist's Smn_ object is no area spell");
            Assert.That(SpellRoutes.Of(Spell("Spl_HeatWave", "MagicHeatWave", SpellDelivery.Summon, "Smn_HeatWave"), Rules), Is.EqualTo(SpellRoute.Area));
            Assert.That(SpellRoutes.Of(Spell("Spl_SummonSkeleton", "MagicSummonSkeleton"), Rules), Is.EqualTo(SpellRoute.Summon));
            Assert.That(SpellRoutes.Of(Spell("Spl_Fireball", null, SpellDelivery.Projectile, "Mis_Fireball"), Rules), Is.EqualTo(SpellRoute.Missile));
            Assert.That(SpellRoutes.Of(Spell("Spl_Fear", "MagicFear"), Rules), Is.EqualTo(SpellRoute.Fear));
            Assert.That(SpellRoutes.Of(Spell("Spl_Teleport_Ardea", Teleports.Effect), Rules), Is.EqualTo(SpellRoute.Teleport));
            Assert.That(SpellRoutes.Of(Spell("Spl_Cure", "MagicHeal"), Rules), Is.EqualTo(SpellRoute.Instant));
            Assert.That(SpellRoutes.Of(Spell("Spl_Light", null, SpellDelivery.Summon, "Smn_Light"), Rules), Is.EqualTo(SpellRoute.Light));
            Assert.That(SpellRoutes.Of(Spell("Spl_BlessWeapon", null), Rules), Is.EqualTo(SpellRoute.Inventory));
            Assert.That(SpellRoutes.Of(Spell("Spl_Unknown", "MagicNothing"), Rules), Is.EqualTo(SpellRoute.None));
        }

        [Test]
        public void Visuals_IncludeWhatTheSpellLeavesInTheWorld()
        {
            Assert.That(SpellRoutes.Visuals(Spell("Spl_SummonMist", "MagicSummonMist", SpellDelivery.Summon, "Smn_Mist")),
                        Does.Contain(SpellEffectRules.MistEffect));
            Assert.That(SpellRoutes.Visuals(Spell("Spl_TimeBubble", "MagicTimeBubble")), Does.Contain(SpellEffectRules.TimeBubbleEffect));
            Assert.That(SpellRoutes.Visuals(Spell("Spl_Light", null)), Does.Contain(SpellEffectRules.LightSourceEffect));
            Assert.That(SpellRoutes.Visuals(Spell("Spl_Sleep", "MagicSleep", visuals: Cast(null, "eff_magic_sleep_target_01"))),
                        Does.Contain("eff_magic_sleep_target_01"));
        }

        [Test]
        public void ShowsCasterCast_UnlessSomethingElseShowsIt()
        {
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_Fear", "MagicFear", visuals: Cast("eff_magic_fear_cast_01")), SpellRoute.Fear), Is.True);
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_Cure", "MagicHeal", visuals: Cast("eff_magic_fullheal_cast_01")), SpellRoute.Instant), Is.True);
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_HeatWave", "MagicHeatWave", visuals: Cast("eff_magic_heatwave_cast_01")), SpellRoute.Area),
                        Is.False, "the area object shows the wave");
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_Teleport_X", Teleports.Effect, visuals: Cast("eff_magic_teleport_cast_01")), SpellRoute.Teleport), Is.False);
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_Transform_Wolf", "MagicTransform",
                        visuals: Cast("eff_magic_animalform_cast_01", "eff_magic_animalform_cast_01")), SpellRoute.World), Is.False, "same as the target effect");
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_SoulTravel", "MagicSoulTravel", visuals: Cast("eff_magic_soultravel_cast_01")), SpellRoute.World), Is.False);
            Assert.That(SpellRoutes.ShowsCasterCast(Spell("Spl_Sleep", "MagicSleep"), SpellRoute.World), Is.False, "no caster effect");
        }

        [Test]
        public void HasInstantEffect_MatchesApplyInstant()
        {
            foreach (var e in new[] { "MagicHeal", "MagicHealOther", "MagicRefresh", "MagicPoison", "MagicDisease", "MagicCurePoison", "MagicCureDisease", Teleports.Effect })
                Assert.That(MagicService.HasInstantEffect(e), Is.True, e);
            foreach (var e in new[] { null, "MagicSleep", "MagicSummonMist", "MagicFear" })
                Assert.That(MagicService.HasInstantEffect(e), Is.False, e ?? "null");
        }

        /// <summary>The content pack's spells: each one an item or scroll casts is carried out and has a G3 effect to show.</summary>
        [Test]
        public void EveryCarriedSpellOfThePack_HasARouteAndAVisual()
        {
            string file = FindFile("Assets/StreamingAssets/Content/Gothic3/spells/spells.json");
            if (file == null) Assert.Ignore("content pack not built (G3Cli content import)");
            var options = ContentJson.CreateOptions(GameSchema.Create());
            var spells = JsonSerializer.Deserialize<List<SpellDef>>(File.ReadAllBytes(file), options);
            var carried = spells.Where(s => s.Items.Count + s.Scrolls.Count > 0).ToList();
            Assert.That(carried.Count, Is.GreaterThan(40));
            var noRoute = carried.Where(s => SpellRoutes.Of(s, Rules) == SpellRoute.None).Select(s => s.Id).ToList();
            Assert.That(noRoute, Is.Empty, "spells an item casts that do nothing");
            var noVisual = carried.Where(s => !SpellRoutes.Visuals(s).Any()).Select(s => s.Id).ToList();
            Assert.That(noVisual, Is.Empty, "spells without any G3 effect name");
            Assert.That(SpellRoutes.Of(carried.First(s => s.Id == "Spl_SummonMist"), Rules), Is.EqualTo(SpellRoute.World));
            Assert.That(SpellRoutes.Of(carried.First(s => s.Id == "Spl_Sleep"), Rules), Is.EqualTo(SpellRoute.World));

            string library = FindFile("Assets/G3Imported/Effects/Resources/G3EffectLibrary.asset");
            if (library == null) return;
            var imported = new HashSet<string>(Regex.Matches(File.ReadAllText(library), @"- name: (\S+)\r?\n\s+spawns:\r?\n\s+- prefab")
                                                    .Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);
            // Bless Weapon shows eff_inv_bless_01 on the inventory page (inventory effects are not imported yet).
            var unseen = carried.Where(s => SpellRoutes.Of(s, Rules) != SpellRoute.Inventory && !SpellRoutes.Visuals(s).Any(imported.Contains)).Select(s => s.Id).ToList();
            Assert.That(unseen, Is.Empty, "spells none of whose effects is imported (placeholder glow only)");
            Assert.That(imported, Does.Contain(SpellEffectRules.MistEffect), "the mist cloud");
            Assert.That(imported, Does.Contain(carried.First(s => s.Id == "Spl_Sleep").Visuals.TargetCast), "the sleep effect");
        }

        /// <summary>
        /// fix-159 real data: the carried missile spells, and which of them can ever home. G3 MagicProjectile flies Missile
        /// only for action PowerCast with a focus [0x10052db0]; only Power-type spells reach PowerCast. All four G3
        /// missile spells are Power type, so each homes when power-cast at a target and flies straight otherwise (their
        /// Mis_ templates say PathStyle 0 Line, TargetUpdateMSec 0; MagicProjectile overwrites the PathStyle anyway).
        /// </summary>
        [Test]
        public void MissileSpellsOfThePack_HomeOnlyAsPowerCasts()
        {
            string file = FindFile("Assets/StreamingAssets/Content/Gothic3/spells/spells.json");
            if (file == null) Assert.Ignore("content pack not built (G3Cli content import)");
            var options = ContentJson.CreateOptions(GameSchema.Create());
            var spells = JsonSerializer.Deserialize<List<SpellDef>>(File.ReadAllBytes(file), options);
            var missiles = spells.Where(s => s.Items.Count + s.Scrolls.Count > 0 && SpellRoutes.Of(s, Rules) == SpellRoute.Missile).ToList();
            var canHome = missiles.Where(ProjectileFlight.CanHome).Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            TestContext.Out.WriteLine("missile spells: " + string.Join(", ", missiles.Select(s => s.Id + " (" + s.CastType + ")")));
            TestContext.Out.WriteLine("home when power-cast at a target: " + string.Join(", ", canHome));
            Assert.That(missiles.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal),
                        Is.EqualTo(new[] { "Spl_DestroyEvil", "Spl_Fireball", "Spl_Icelance", "Spl_Lightningbolt" }));
            Assert.That(canHome, Is.EqualTo(new[] { "Spl_DestroyEvil", "Spl_Fireball", "Spl_Icelance", "Spl_Lightningbolt" }));
            foreach (var s in missiles)
            {
                Assert.That(ProjectileFlight.StyleOf(player: false, spell: true, hasTarget: true, powerCast: false),
                            Is.EqualTo(ProjectilePathStyle.Line), s.Id + " plain cast flies straight");
                Assert.That(ProjectileFlight.StyleOf(player: true, spell: true, hasTarget: true, powerCast: false),
                            Is.EqualTo(ProjectilePathStyle.Line), s.Id + " plain cast flies straight");
            }
        }

        static string FindFile(string relative)
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    string p = Path.Combine(d.FullName, relative);
                    if (File.Exists(p)) return p;
                    string git = Path.Combine(d.FullName, ".git");
                    if (!File.Exists(git)) continue;
                    string line = File.ReadAllText(git).Trim();
                    int at = line.IndexOf("/.git/worktrees/", StringComparison.Ordinal);
                    if (!line.StartsWith("gitdir:", StringComparison.Ordinal) || at < 0) continue;
                    string main = Path.Combine(line.Substring(7, at - 7).Trim(), relative);
                    if (File.Exists(main)) return main;
                }
            }
            return null;
        }
    }
}
