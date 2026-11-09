using System;
using System.Collections.Generic;
using G3.Effects;
using G3.Weather;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>effects-2: the hero's torch (G3 rules), status aura choice, rain columns under roofs (fix-130: stale columns, streak ends, rain shadow), weather snapshots.</summary>
    public class Effects2Tests
    {
        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Misc, kind, EquipSlot.None, 0, 0, null, null,
                        null, null, null, null, null, null, default);

        static readonly Dictionary<string, ItemDef> Items = new Dictionary<string, ItemDef>
        {
            ["It_Torch_Burning"] = Item("It_Torch_Burning", "Torch"),
            ["It_Apple"] = Item("It_Apple", "Fruit"),
        };

        static (HeroTorch torch, Inventory inv, List<TorchChanged> events) Torch(int torches = 2, string restored = null)
        {
            var inv = new Inventory();
            if (torches > 0) inv.Add("It_Torch_Burning", torches);
            inv.Add("It_Apple", 1);
            var bus = new EventBus();
            var events = new List<TorchChanged>();
            bus.Subscribe<TorchChanged>(events.Add);
            var t = new HeroTorch();
            t.Bind(inv, id => Items.TryGetValue(id, out var d) ? d : null, bus, restored);
            return (t, inv, events);
        }

        [Test]
        public void UsingATorchTakesItIntoTheLeftHandAndPuttingItAwayReturnsIt()
        {
            var (t, inv, events) = Torch();
            Assert.That(t.Take("It_Torch_Burning"), Is.EqualTo(TorchTakeResult.Taken));
            Assert.That(t.InHand, Is.True);
            Assert.That(t.Take("It_Torch_Burning"), Is.EqualTo(TorchTakeResult.AlreadyHeld));
            Assert.That(t.Release(), Is.True);
            Assert.That(t.InHand, Is.False);
            Assert.That(inv.Count("It_Torch_Burning"), Is.EqualTo(2)); // never used up
            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That(events[1].Held, Is.False);
        }

        [Test]
        public void OnlyTorchesThatAreCarried()
        {
            var (t, _, _) = Torch(torches: 0);
            Assert.That(t.Take("It_Apple"), Is.EqualTo(TorchTakeResult.NotATorch));
            Assert.That(t.Take("It_Torch_Burning"), Is.EqualTo(TorchTakeResult.NotCarried));
        }

        [Test]
        public void TwoHandedWeaponsShieldsAndWaterRefuseTheTorch()
        {
            var (t, _, _) = Torch();
            Assert.That(t.Take("It_Torch_Burning", "2H"), Is.EqualTo(TorchTakeResult.HandsBusy));
            Assert.That(t.Take("It_Torch_Burning", "Bow"), Is.EqualTo(TorchTakeResult.HandsBusy));
            Assert.That(t.Take("It_Torch_Burning", "1H", leftHandBusy: true), Is.EqualTo(TorchTakeResult.HandsBusy));
            Assert.That(t.Take("It_Torch_Burning", swimming: true), Is.EqualTo(TorchTakeResult.Swimming));
            Assert.That(t.Take("It_Torch_Burning", "1H"), Is.EqualTo(TorchTakeResult.Taken));
        }

        [Test]
        public void DrawingAOneHandedWeaponKeepsTheTorchOthersReturnIt()
        {
            var (t, inv, _) = Torch();
            t.Take("It_Torch_Burning");
            Assert.That(t.OnWeaponDrawn("1H"), Is.False);
            Assert.That(t.OnWeaponDrawn("Fist"), Is.False);
            Assert.That(t.InHand, Is.True);
            Assert.That(t.OnWeaponDrawn("Bow"), Is.True);
            Assert.That(t.InHand, Is.False);
            Assert.That(inv.Count("It_Torch_Burning"), Is.EqualTo(2));
            Assert.That(t.OnWeaponHolstered("Bow"), Is.False); // ranged: not taken back
            Assert.That(t.InHand, Is.False);
        }

        [Test]
        public void HolsteringAMeleeWeaponTakesTheTorchBack()
        {
            var (t, inv, _) = Torch();
            t.Take("It_Torch_Burning");
            Assert.That(t.OnWeaponDrawn("2H"), Is.True);
            Assert.That(t.HeldBeforeDraw, Is.EqualTo("It_Torch_Burning"));
            Assert.That(t.OnWeaponHolstered("2H"), Is.True);
            Assert.That(t.InHand, Is.True);

            Assert.That(t.OnWeaponDrawn("1H", leftHandBusy: true), Is.True); // the shield takes the left hand
            Assert.That(t.OnWeaponHolstered("1H", swimming: true), Is.False); // not in deep water
            Assert.That(t.InHand, Is.False);

            t.Take("It_Torch_Burning");
            t.OnWeaponDrawn("Staff");
            Assert.That(t.OnWeaponDrawn("Cast"), Is.False); // a spell after the staff: no re-hold
            Assert.That(t.OnWeaponHolstered("Cast"), Is.False);
            Assert.That(inv.Count("It_Torch_Burning"), Is.EqualTo(2));
        }

        [Test]
        public void InteractionsStowKeepOrReleaseTheTorch()
        {
            Assert.That(HeroTorch.ForInteraction("Chest", "Chest_Big"), Is.EqualTo(TorchInteraction.KeepInHand));
            Assert.That(HeroTorch.ForInteraction("Door", "Door_01"), Is.EqualTo(TorchInteraction.KeepInHand));
            Assert.That(HeroTorch.ForInteraction("Bed", "G3_Bed_01"), Is.EqualTo(TorchInteraction.Stow));
            Assert.That(HeroTorch.ForInteraction("Anvil", "Anvil_02"), Is.EqualTo(TorchInteraction.Stow));
            Assert.That(HeroTorch.ForInteraction("Bench", "Bench_Wood"), Is.EqualTo(TorchInteraction.Release));

            var (t, inv, _) = Torch();
            t.Take("It_Torch_Burning");
            Assert.That(t.BeginInteraction("Bed", "G3_Bed_01"), Is.EqualTo(TorchInteraction.Stow));
            Assert.That(t.InHand, Is.False);
            t.EndInteraction();
            Assert.That(t.InHand, Is.True);
            Assert.That(inv.Count("It_Torch_Burning"), Is.EqualTo(2));
            t.BeginInteraction("Stool", "Stool_01");
            Assert.That(t.Item, Is.Null);
            t.EndInteraction();
            Assert.That(t.Item, Is.Null);
            Assert.That(inv.Count("It_Torch_Burning"), Is.EqualTo(2)); // back in the inventory
        }

        [Test]
        public void SellingTheHeldTorchEmptiesTheHand()
        {
            var (t, inv, _) = Torch(torches: 1);
            t.Take("It_Torch_Burning");
            inv.Remove("It_Torch_Burning", 1);
            Assert.That(t.Item, Is.Null);
        }

        [Test]
        public void TheTorchSurvivesASaveOnlyWhenStillCarried()
        {
            var (t, _, _) = Torch(restored: "It_Torch_Burning");
            Assert.That(t.InHand, Is.True);
            var (u, _, _) = Torch(torches: 0, restored: "It_Torch_Burning");
            Assert.That(u.InHand, Is.False);
            var section = new TorchSaveSection(() => t);
            Assert.That(section.TakeRestored(), Is.Null);
        }

        [Test]
        public void StatusEffectsFollowG3sBodyClasses()
        {
            Assert.That(StatusAuraRules.FrozenEffect(StatusAuraRules.BodyOf(true, false, false)), Is.EqualTo("eff_magic_icespell_target_01"));
            Assert.That(StatusAuraRules.FrozenEffect(StatusAuraRules.BodyOf(false, true, true)), Is.EqualTo("eff_magic_icespell_target_01"));
            Assert.That(StatusAuraRules.FrozenEffect(StatusAuraRules.BodyOf(false, false, true)), Is.EqualTo("eff_magic_icespell_target_03"));
            Assert.That(StatusAuraRules.ThawBurst(StatusAuraRules.BodyOf(false, false, false)), Is.EqualTo("eff_magic_icespell_burst_02"));
            Assert.That(StatusAuraRules.BurningEffect, Is.EqualTo("eff_magic_firespell_target_01"));
        }

        [Test]
        public void ItemLightFactorStaysInRange()
        {
            for (float t = 0; t < 5; t += 0.07f)
            {
                float f = ItemEffectRules.Factor(ItemEffectRules.LightEffect.Blink, t, 1, 2);
                Assert.That(f, Is.InRange(0.74f, 1.0001f));
            }
            Assert.That(ItemEffectRules.Factor(ItemEffectRules.LightEffect.Steady, 3, 0, 1), Is.EqualTo(1f));
        }

        [Test]
        public void RainColumnsAreTracedOnceWithinTheFrameBudget()
        {
            int traces = 0;
            var cols = new PrecipitationColumns(0.5f, (x, z) => { traces++; return x < 0 ? 3f : 0f; }, tracesPerFrame: 2);
            Assert.That(cols.TryGround(0.1f, 0.1f, out float y), Is.True);
            Assert.That(y, Is.EqualTo(0f));
            Assert.That(cols.TryGround(0.2f, 0.4f, out _), Is.True); // same column: cached
            Assert.That(cols.TryGround(-1f, 0, out y), Is.True);
            Assert.That(y, Is.EqualTo(3f));
            Assert.That(cols.TryGround(5f, 5f, out _), Is.False); // budget spent
            Assert.That(traces, Is.EqualTo(2));
            cols.BeginFrame();
            Assert.That(cols.TryGround(5f, 5f, out _), Is.True);
            cols.Prune(0, 0, 1f);
            Assert.That(cols.Cached, Is.EqualTo(2));
        }

        [Test]
        public void DropsEndOnTheRoofOrTheCubeFloor()
        {
            // under a roof at 3 m with drops spawned at 5 m: the drop dies on the roof and splashes there
            Assert.That(PrecipitationColumns.FallSeconds(5f, 3f, -5f, 10f, out bool splash), Is.EqualTo(0.2f).Within(1e-5));
            Assert.That(splash, Is.True);
            // a ceiling above the spawn point: indoors, no drop at all
            Assert.That(PrecipitationColumns.FallSeconds(5f, 6f, -5f, 10f, out _), Is.EqualTo(0f));
            // ground below the cube: the drop leaves the cube without a splash
            Assert.That(PrecipitationColumns.FallSeconds(5f, -20f, -5f, 10f, out splash), Is.EqualTo(1f).Within(1e-5));
            Assert.That(splash, Is.False);
        }

        [Test]
        public void StaleRainColumnsAreTracedAgain()
        {
            // fix-130: a column traced before the mill streamed in kept raining through its roof
            float roof = float.NaN;
            int traces = 0;
            var cols = new PrecipitationColumns(0.5f, (x, z) => { traces++; return float.IsNaN(roof) ? 0f : roof; }, tracesPerFrame: 1, maxAge: 3f);
            cols.BeginFrame(0.0);
            Assert.That(cols.TryGround(0.1f, 0.1f, out float y), Is.True);
            Assert.That(y, Is.EqualTo(0f));
            roof = 6f; // the building streams in
            cols.BeginFrame(1.0);
            Assert.That(cols.TryGround(0.1f, 0.1f, out y), Is.True);
            Assert.That(y, Is.EqualTo(0f), "still fresh");
            cols.BeginFrame(3.5);
            Assert.That(cols.TryGround(0.1f, 0.1f, out y), Is.True);
            Assert.That(y, Is.EqualTo(6f), "traced again");
            Assert.That(traces, Is.EqualTo(2));
            // a stale column with the budget spent: the old height is used meanwhile
            cols.BeginFrame(7.0);
            Assert.That(cols.TryGround(5f, 5f, out _), Is.True);
            Assert.That(cols.TryGround(0.1f, 0.1f, out y), Is.True);
            Assert.That(y, Is.EqualTo(6f));
        }

        [Test]
        public void StreaksEndAboveTheRoof()
        {
            // a streak half 0.12 m long ending on a roof at 3 m dies 0.12 m above it (none of it drawn under the roof)
            float life = PrecipitationColumns.FallSeconds(5f, 3f, -5f, 10f, 0.12f, out bool splash);
            Assert.That(life, Is.EqualTo((5f - 3.12f) / 10f).Within(1e-5));
            Assert.That(splash, Is.True);
            Assert.That(PrecipitationColumns.FallSeconds(5f, 4.95f, -5f, 10f, 0.12f, out _), Is.EqualTo(0f));
            Assert.That(PrecipitationColumns.FallSeconds(5f, PrecipitationColumns.NoHit, -5f, 10f, 0.12f, out splash), Is.EqualTo(1f).Within(1e-5));
            Assert.That(splash, Is.False);
        }

        [Test]
        public void RainShadowSteepOpenSlopeIsNotARoof()
        {
            // snow3: a 63 degree open slope (y = 2x); one point-sampled 1 m cell put the lower half of each cell "under a roof"
            float? World(float x, float z) => 2f * x;
            var g = new RainShadowGrid(16, 1f);
            g.Recenter(8f, 8f);
            g.Trace(World, 16 * 16);
            for (float x = 2.05f; x < 13f; x += 0.37f)
                for (float z = 2.1f; z < 13f; z += 1.3f)
                    Assert.That(g.ShelterAt(x, 2f * x, z), Is.EqualTo(0f).Within(1e-4f), $"open slope at {x}, {z}");
            Assert.That(g.TopAt(4.5f, 4.5f), Is.EqualTo(9f).Within(1e-4f), "a cell centre: its own trace");
            Assert.That(g.TopAt(4.9f, 7.3f), Is.EqualTo(9.8f).Within(1e-4f), "between centres: the plane");
        }

        [Test]
        public void RainShadowKeepsFloorsUnderARoofDry()
        {
            // a 4 x 4 m roof at 10 m over (0..4, 0..4); open ground elsewhere at 0
            float? World(float x, float z) => x >= 0 && x < 4 && z >= 0 && z < 4 ? 10f : 0f;
            var g = new RainShadowGrid(16, 1f);
            Assert.That(g.Recenter(2f, 2f), Is.True);
            Assert.That(g.Trace(World, 4), Is.EqualTo(4));
            // nearest to the camera first: the four cells round (2, 2) are under the roof
            Assert.That(g.HeightAt(1.5f, 1.5f), Is.EqualTo(10f));
            Assert.That(g.HeightAt(2.5f, 2.5f), Is.EqualTo(10f));
            g.Trace(World, 16 * 16);
            Assert.That(g.ShelterAt(2.5f, 0f, 2.5f), Is.EqualTo(1f), "the floor inside");
            Assert.That(g.ShelterAt(2.5f, 10f, 2.5f), Is.EqualTo(0f), "the roof itself");
            Assert.That(g.ShelterAt(2.5f, 9.4f, 2.5f), Is.EqualTo(0f), "a sloped roof within the tolerance");
            Assert.That(g.ShelterAt(6.5f, 0f, 6.5f), Is.EqualTo(0f), "open ground");
            Assert.That(g.ShelterAt(100f, 0f, 100f), Is.EqualTo(0f), "outside the grid: open");
            Assert.That(g.TakeDirty(), Is.True);
            Assert.That(g.TakeDirty(), Is.False);
        }

        [Test]
        public void RainShadowOpenGroundBesideARoofIsOpen()
        {
            // fix-169 (b1021a fix130 probe 0.46): a roof at 3.3 m over z < 4 on gently sloping ground; the open cells beside it
            // must stay open right up to the roof's cell edge (bilinear alone blended the roof in), the floor under it dry
            float Ground(float x) => 0.15f * x;
            float? World(float x, float z) => z < 4f ? 3.3f : Ground(x);
            var g = new RainShadowGrid(16, 1f);
            g.Recenter(6f, 6f);
            g.Trace(World, 16 * 16);
            for (float x = 1f; x < 12f; x += 0.43f)
            {
                for (float z = 4.0f; z < 9f; z += 0.25f)
                    Assert.That(g.ShelterAt(x, Ground(x), z), Is.EqualTo(0f).Within(1e-4f), $"open ground at {x}, {z}");
                for (float z = 1f; z < 3.6f; z += 0.25f)
                    Assert.That(g.ShelterAt(x, Ground(x), z), Is.EqualTo(1f).Within(1e-4f), $"floor under the roof at {x}, {z}");
            }
        }

        [Test]
        public void RainShadowFollowsTheCameraKeepingWhatItKnows()
        {
            var g = new RainShadowGrid(16, 1f);
            g.Recenter(0f, 0f);
            g.Trace((x, z) => x >= 0 && x < 1 && z >= 0 && z < 1 ? 5f : (float?)null, 16 * 16);
            Assert.That(g.HeightAt(0.5f, 0.5f), Is.EqualTo(5f));
            Assert.That(g.HeightAt(3.5f, 0.5f), Is.EqualTo(RainShadowGrid.Open));
            Assert.That(g.Recenter(3f, 0f), Is.False, "still in the middle half");
            Assert.That(g.Recenter(5f, 0f), Is.True);
            Assert.That(g.HeightAt(0.5f, 0.5f), Is.EqualTo(5f), "kept after the move");
            Assert.That(g.HeightAt(12.5f, 0.5f), Is.EqualTo(RainShadowGrid.Open), "new cells: not traced yet");
            Assert.That(g.Recenter(500f, 0f), Is.True);
            Assert.That(g.HeightAt(0.5f, 0.5f), Is.EqualTo(RainShadowGrid.Open), "left behind");
        }

        [Test]
        public void SplashesComeWhenTheirDropLands()
        {
            var q = new SplashQueue(2);
            Assert.That(q.Add(1.0, 0, 0, 0), Is.True);
            Assert.That(q.Add(2.0, 1, 0, 0), Is.True);
            Assert.That(q.Add(3.0, 2, 0, 0), Is.False);
            var due = new List<(float, float, float)>();
            Assert.That(q.TakeDue(1.5, due), Is.EqualTo(1));
            Assert.That(q.Count, Is.EqualTo(1));
        }

        [Test]
        public void WeatherSnapshotsRestoreByStateName()
        {
            var chances = new[]
            {
                new WeatherChance("Rainy", 0.2f, 1 / 24f, 4 / 24f),
                new WeatherChance("Sunny", 0.75f, 4 / 24f, 20 / 24f),
            };
            var a = new WeatherScheduler("Myrtana", chances, () => 0.1f);
            a.Update(3.0);
            a.Update(3.1);
            var snap = a.Capture();
            var b = new WeatherScheduler("Myrtana", new[] { chances[1], chances[0] }, () => 0.9f);
            Assert.That(b.Restore(snap), Is.True);
            b.Update(3.1);
            Assert.That(b.CurrentState, Is.EqualTo(a.CurrentState));
            Assert.That(b.NextState, Is.EqualTo(a.NextState));
            Assert.That(b.Progress, Is.EqualTo(a.Progress).Within(1e-5));
            Assert.That(b.Restore(new WeatherScheduleSnapshot("Myrtana", "Gone", null, 0, 0.1)), Is.False);

            var text = WeatherScheduleSnapshot.Encode(new[] { snap, new WeatherScheduleSnapshot("Varant", "Sunny", null, 1.25, 0.5) });
            var back = WeatherScheduleSnapshot.Decode(text + ";broken");
            Assert.That(back.Count, Is.EqualTo(2));
            Assert.That(back[0].Current, Is.EqualTo(snap.Current));
            Assert.That(back[0].Start, Is.EqualTo(snap.Start));
            Assert.That(back[1].Next, Is.Null);
            Assert.That(WeatherScheduleSnapshot.Decode(null), Is.Empty);
        }
    }
}
