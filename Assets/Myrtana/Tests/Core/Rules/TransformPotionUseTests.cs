using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-193: a transformation potion is drunk (the potion use motion, then the transformation), not cast: its quick-slot
    /// key does not ready the spell hand. Druid stones (use type Cast) and runes are readied and cast.
    /// </summary>
    public class TransformPotionUseTests
    {
        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder,
                 ItemTests.Item("It_Potion_Wolf", 200, kind: "Potion"),
                 ItemTests.Item("It_DruidStone_Wolf", 2000, kind: "Cast"),
                 ItemTests.Item("It_Potion_CurePoison", 200, kind: "Potion"),
                 ItemTests.Item("It_Spell_Fireball", 200, kind: "Cast"),
                 ItemTests.Item("It_Scroll_Fireball", 50, kind: "Scroll"))
            .Add(RulesSchema.SpellFolder,
                 Spell("Spl_Transform_Wolf", "MagicTransform", new[] { "It_DruidStone_Wolf" }, new[] { "It_Potion_Wolf" }, "Wolf"),
                 Spell("Spl_CurePoison", "MagicCurePoison", new[] { "It_Potion_CurePoison" }, null, null),
                 Spell("Spl_Fireball", "MagicFireball", new[] { "It_Spell_Fireball" }, new[] { "It_Scroll_Fireball" }, null))
            .Add(SimulationSchema.NpcFolder,
                 new NpcDef(new ActorId("Wolf"), new TextId("FO_Wolf"), null, "Wolf", "Wolf_Body_01", new WorldPose(30, 0, 0, 0),
                            combat: new NpcCombatDef(5, 10, "Wolf", humanoid: false, animal: true)));

        static SpellDef Spell(string id, string effect, string[] items, string[] scrolls, string spawn) =>
            new SpellDef(id, items, scrolls, SpellCastType.Regular, SpellTarget.Caster, SpellKind.Target, SpellDeity.Adanos, 20, 20,
                         SpellDelivery.Instant, effect, null, 0, spawn, null, null);

        [Test]
        public void Potions_AreDrunk_StonesRunesAndScrolls_AreCast()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Magic.IsSpellItem("It_Potion_Wolf"), Is.True, "the potion is listed as the spell's scroll");
            Assert.That(s.Magic.IsCastItem("It_Potion_Wolf"), Is.False, "a transformation potion is drunk, not cast");
            Assert.That(s.Magic.IsCastItem("It_Potion_CurePoison"), Is.False, "the antidote too");
            Assert.That(s.Magic.IsCastItem("It_DruidStone_Wolf"), Is.True, "druid stone: the cast motion");
            Assert.That(s.Magic.IsCastItem("It_Spell_Fireball"), Is.True);
            Assert.That(s.Magic.IsCastItem("It_Scroll_Fireball"), Is.True, "a scroll is cast");
            Assert.That(s.Magic.IsCastItem("It_Gold_None"), Is.False);

            Assert.That(s.SpellEffects.IsTransformItem("It_Potion_Wolf"), Is.True);
            Assert.That(s.SpellEffects.DrinksToTransform("It_Potion_Wolf"), Is.True, "drink motion first, the transformation after");
            Assert.That(s.SpellEffects.DrinksToTransform("It_DruidStone_Wolf"), Is.False);
            Assert.That(s.SpellEffects.DrinksToTransform("It_Potion_CurePoison"), Is.False);
        }

        [Test]
        public void TheDrinkIsCheckedFirst_TheTransformationFollowsIt()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Of(ActorId.Player).Add("It_Potion_Wolf", 2);
            Assert.That(s.SpellEffects.CanUseTransformItem("It_Potion_Wolf"), Is.True);
            Assert.That(s.SpellEffects.Hero.Kind, Is.EqualTo(HeroFormKind.Hero), "checking does not transform");
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Potion_Wolf"), Is.EqualTo(2), "nor use up the potion");
            Assert.That(s.SpellEffects.UseTransformItem("It_Potion_Wolf"), Is.True);
            Assert.That(s.SpellEffects.Hero.Kind, Is.EqualTo(HeroFormKind.Animal));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Potion_Wolf"), Is.EqualTo(1));
            Assert.That(s.SpellEffects.CanUseTransformItem("It_Potion_Wolf"), Is.False, "one form at a time: refused before the drink");
            s.SpellEffects.Hero.TransformBack();
            Assert.That(s.SpellEffects.CanUseTransformItem("It_Potion_Wolf"), Is.True);
        }
    }
}
