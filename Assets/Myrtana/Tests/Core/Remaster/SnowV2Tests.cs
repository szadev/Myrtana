using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Remaster snow (Docs/remaster-world.md section 12): snow layers, snowfall/fresh curve, cover, print grid, refill clock, print shapes, foot plants.</summary>
    public class SnowV2Tests
    {
        [SetUp]
        public void Defaults() => WorldRemasterLook.Current = WorldLookSettings.Default;

        [Test]
        public void ScanWeight_OnlyTheBrightGreyGroundScanIsSnow()
        {
            Assert.That(SnowV2.ScanWeight(1, 0.377f, 0.03f), Is.EqualTo(1f).Within(1e-4), "polyhaven snow_02");
            Assert.That(SnowV2.ScanWeight(0, 0.547f, 0.06f), Is.EqualTo(0f), "the bright metal scan is a surface");
            Assert.That(SnowV2.ScanWeight(1, 0.237f, 0.73f), Is.EqualTo(0f), "coast sand");
            Assert.That(SnowV2.ScanWeight(1, 0.155f, 0.45f), Is.EqualTo(0f), "forest floor");
            Assert.That(SnowV2.ScanWeight(2, 0.4f, 0.02f), Is.EqualTo(0f), "rock kind");
            Assert.That(SnowV2.PackedSnow("G3_Nature_Snow_Path_01_Diffuse_01"), Is.EqualTo(1f), "road snow");
            Assert.That(SnowV2.PackedSnow("G3_Architecture_Ground_City_Snow_01_Diffuse_01"), Is.EqualTo(1f), "town snow");
            Assert.That(SnowV2.PackedSnow("G3_Nature_Ground_Snow_01_Diffuse_01"), Is.EqualTo(0f), "powder");
            Assert.That(SnowV2.PackedSnow(null), Is.EqualTo(0f));
        }

        [Test]
        public void Snowfall_FromTheStateBlend_AndSnowstormDoesNotWet()
        {
            Assert.That(SnowV2.SnowfallOf("Snowy(Nordmar)"), Is.EqualTo(1f));
            Assert.That(SnowV2.SnowfallOf("Snowstorm(Nordmar)"), Is.EqualTo(1.2f));
            Assert.That(SnowV2.SnowfallOf("Sunny(Nordmar)"), Is.EqualTo(0f));
            Assert.That(SnowV2.SnowfallOf("Rainy"), Is.EqualTo(0f));
            Assert.That(SnowV2.Snowfall("Sunny(Nordmar)", "Snowy(Nordmar)", 0.25f), Is.EqualTo(0f), "fix-178: no snow while clear weather dominates");
            Assert.That(SnowV2.Snowfall("Sunny(Nordmar)", "Snowy(Nordmar)", 0.75f), Is.EqualTo(0.5f).Within(1e-5), "the dominant snowy state, as G3 shows precipitation");
            Assert.That(SnowV2.Snowfall("Snowy(Nordmar)", "Sunny(Nordmar)", 0.1f), Is.EqualTo(0.8f).Within(1e-5));
            Assert.That(WorldV2.IsRainy("Snowstorm(Nordmar)"), Is.False, "snow does not wet (was: 'storm' matched)");
            Assert.That(WorldV2.IsRainy("Rainy"), Is.True);
            Assert.That(WorldV2.IsRainy("Sandstormy(Varant)"), Is.False, "fix-236: a sand storm does not wet");
            Assert.That(WorldV2.IsRainy("Magic_HailStorm"), Is.True);
        }

        [Test]
        public void FreshSnow_BuildsLinearly_SettlesExponentially()
        {
            var s = WorldRemasterLook.Current;
            float f = 0;
            for (int i = 0; i < 300; i++) f = SnowV2.FreshStep(f, 1f, 1f);   // 5 min of steady snowfall
            Assert.That(f, Is.EqualTo(300f / s.snowFreshBuild).Within(1e-3));
            for (int i = 0; i < 600; i++) f = SnowV2.FreshStep(f, 1f, 1f);
            Assert.That(f, Is.EqualTo(1f), "capped");
            float g = SnowV2.FreshStep(1f, 0f, s.snowFreshMelt);
            Assert.That(g, Is.EqualTo(0.3679f).Within(1e-3), "e^-1 after one settle time");
            Assert.That(SnowV2.FreshStep(0.5f, 0f, -1f), Is.EqualTo(0.5f), "negative dt ignored");
        }

        [Test]
        public void Cover_FlatFirst_SteeperWithMoreSnow_NoneUnderRoofsOrOutsideNordmar()
        {
            Assert.That(SnowV2.Cover(1f, 0.55f, 0), Is.EqualTo(1f).Within(1e-4), "flat top face");
            Assert.That(SnowV2.Cover(0.5f, 0.55f, 0), Is.EqualTo(0f), "60 deg wall stays bare at the lasting cover");
            Assert.That(SnowV2.Cover(0.75f, 1f, 0), Is.GreaterThan(SnowV2.Cover(0.75f, 0.55f, 0)), "fresh snow reaches steeper faces");
            Assert.That(SnowV2.Cover(-1f, 1f, 0), Is.EqualTo(0f), "undersides");
            Assert.That(SnowV2.CoverAmount(false, 1, 0, 0), Is.EqualTo(0.55f).Within(1e-5));
            Assert.That(SnowV2.CoverAmount(true, 1, 0, 0), Is.EqualTo(0f), "bare ground: only fresh snow");
            Assert.That(SnowV2.CoverAmount(true, 1, 1, 0), Is.GreaterThan(0.5f));
            Assert.That(SnowV2.CoverAmount(false, 0, 1, 0), Is.EqualTo(0f), "Myrtana");
            Assert.That(SnowV2.CoverAmount(false, 1, 1, 1), Is.EqualTo(0f), "under a roof");
            Assert.That(SnowV2.RegionWeight("Nordmar"), Is.EqualTo(1f));
            Assert.That(SnowV2.RegionWeight("Varant"), Is.EqualTo(0f));
            Assert.That(SnowV2.RegionWeight(null), Is.EqualTo(0f));
        }

        [Test]
        public void PrintGrid_IsToroidal_EveryCellAPrintReachesKnowsIt()
        {
            var g = new SnowPrintGrid(64, 256, 64);
            Assert.That(g.CellSize, Is.EqualTo(0.25f));
            Assert.That(SnowV2.SlotBase(-1, 0, 256), Is.EqualTo(255 * SnowV2.Slots), "negative cells wrap");
            Assert.That(SnowV2.SlotBase(5, 7, 256), Is.EqualTo(SnowV2.SlotBase(5 + 256, 7 - 256, 256)), "the grid repeats every 64 m");
            int id = g.Add(10.1f, -3.4f, 0, 1, 0.14f, 0.055f, 5f, 0.5f, 1f, SnowV2.Boot, 1, 0.6f);
            float r = SnowV2.Reach(0.14f, 0.055f);
            for (float dx = -r; dx <= r; dx += 0.05f)
                for (float dz = -r; dz <= r; dz += 0.05f)
                    if (dx * dx + dz * dz < r * r * 0.95f)
                        Assert.That(g.At(10.1f + dx, -3.4f + dz), Has.Member(id), $"cell at offset {dx:0.00},{dz:0.00}");
            Assert.That(g.At(10.1f, -3.4f + 1.5f), Has.No.Member(id), "far cells stay free");
            var dirtyCells = new System.Collections.Generic.List<int>();
            var dirtyPrints = new System.Collections.Generic.List<int>();
            g.TakeDirty(dirtyCells, dirtyPrints);
            Assert.That(dirtyPrints, Is.EqualTo(new[] { id }));
            Assert.That(dirtyCells.Count, Is.InRange(4, 12));
            g.TakeDirty(dirtyCells, dirtyPrints);
            Assert.That(dirtyCells, Is.Empty, "taken");
        }

        [Test]
        public void PrintGrid_FullCellsGiveUpGoneStaleThenOldest()
        {
            var g = new SnowPrintGrid(64, 256, 64);
            // 8 prints on the same spot fill its cell
            for (int i = 0; i < SnowV2.Slots; i++) g.Add(0.12f, 0.12f, 1, 0, 0.05f, 0.04f, 0, i * 0.01f, 1, SnowV2.Paw, 1, 0.6f);
            int newest = g.Add(0.12f, 0.12f, 1, 0, 0.05f, 0.04f, 0, 0.5f, 1, SnowV2.Paw, 1, 0.6f);
            var ids = new System.Collections.Generic.List<int>(g.At(0.12f, 0.12f));
            Assert.That(ids, Has.Member(newest));
            Assert.That(ids, Has.No.Member(0), "the oldest went");
            Assert.That(ids.Count, Is.EqualTo(SnowV2.Slots));
            // a print gone (age past AgeGone) is replaced before a younger one
            var h = new SnowPrintGrid(64, 256, 64);
            for (int i = 0; i < SnowV2.Slots; i++) h.Add(0.12f, 0.12f, 1, 0, 0.05f, 0.04f, 0, i == 3 ? 0f : 1.5f, 1, SnowV2.Paw, 1, 0.6f);
            int n = h.Add(0.12f, 0.12f, 1, 0, 0.05f, 0.04f, 0, 1.5f, 1, SnowV2.Paw, 1, 0.6f);
            var hs = new System.Collections.Generic.List<int>(h.At(0.12f, 0.12f));
            Assert.That(hs, Has.Member(n));
            Assert.That(hs, Has.No.Member(3), "age 1.5 >= gone");
            // ring reuse: the overwritten entry no longer reaches its old cells, so its slots count as free
            var k = new SnowPrintGrid(64, 256, 16);
            k.Add(5, 5, 1, 0, 0.14f, 0.055f, 0, 0, 1, SnowV2.Boot, 1, 0.6f);
            for (int i = 0; i < 16; i++) k.Add(20, 20, 1, 0, 0.14f, 0.055f, 0, 0, 1, SnowV2.Boot, 1, 0.6f);
            Assert.That(k.Sample(5, 0, 5, 0).depth, Is.EqualTo(0f), "id 0 now lies at 20, 20");
        }

        [Test]
        public void PrintGrid_SampleFadesWithAgeAndLevel_RimOutsideOnly()
        {
            var g = new SnowPrintGrid(64, 256, 64);
            g.Add(3, 3, 0, 1, 0.14f, 0.055f, 10f, 0.1f, 1, SnowV2.Boot, 1, 0.6f);
            Assert.That(g.Sample(3, 10, 3, 0.1f).depth, Is.GreaterThan(0.6f), "in the sole");
            Assert.That(g.Sample(3, 10, 3, 0.1f).rim, Is.EqualTo(0f).Within(1e-4), "no rim inside");
            Assert.That(g.Sample(3, 10.2f, 3, 0.1f).depth, Is.GreaterThan(0.6f), "same level (the shading point a bit above)");
            Assert.That(g.Sample(3, 13f, 3, 0.1f).depth, Is.EqualTo(0f), "a ledge 3 m above: no print");
            Assert.That(g.Sample(3, 10, 3, 1.2f).depth, Is.EqualTo(0f), "filled in after one refill");
            Assert.That(g.Sample(3, 10, 3 + 0.165f, 0.1f).rim, Is.GreaterThan(0.1f), "rim just past the toe");
            Assert.That(g.Sample(3, 10, 3.5f, 0.1f).depth, Is.EqualTo(0f));
        }

        [Test]
        public void RefillClock_FasterWhileSnowing_PrintsFadeAndAgeWraps()
        {
            var s = WorldRemasterLook.Current;
            Assert.That(SnowV2.ClockStep(0, 0, s.snowTrailRefill), Is.EqualTo(1).Within(1e-4), "one refill without snowfall");
            Assert.That(SnowV2.ClockStep(0, 1, s.snowTrailRefillSnowing), Is.EqualTo(1).Within(1e-4), "one refill under full snowfall");
            Assert.That(SnowV2.Fade(0), Is.EqualTo(1f));
            Assert.That(SnowV2.Fade(0.3f), Is.EqualTo(1f), "fresh prints stay sharp");
            Assert.That(SnowV2.Fade(1f), Is.EqualTo(0f));
            Assert.That(SnowV2.Fade(0.6f), Is.InRange(0.2f, 0.8f));
            float now = SnowV2.Wrapped(4097.25), then = SnowV2.Wrapped(4095.75);
            Assert.That(now, Is.EqualTo(1.25f).Within(1e-3));
            Assert.That(SnowV2.Age(now, then), Is.EqualTo(1.5f).Within(1e-3), "age across the wrap");
            Assert.That(SnowV2.AgeGone, Is.LessThan(SnowV2.ClockWrap / 2), "gone long before the wrap could revive it");
        }

        [Test]
        public void Level_PrintsShowOnTheirOwnLevelOnly()
        {
            Assert.That(SnowV2.HeightMatch(0.1f), Is.EqualTo(1f));
            Assert.That(SnowV2.HeightMatch(-0.2f), Is.EqualTo(1f));
            Assert.That(SnowV2.HeightMatch(3f), Is.EqualTo(0f), "a ledge 3 m above: no print");
        }

        static float D(float u, float v, int kind, float side = 1, float hl = 0.14f, float hw = 0.055f) => SnowV2.Print(u, v, hl, hw, kind, side, SnowV2.Wall).depth;

        [Test]
        public void Footprint_BootSoleHeelToeSoftWallRim()
        {
            Assert.That(D(0.06f, 0, SnowV2.Boot), Is.GreaterThan(0.95f), "the ball");
            Assert.That(D(-0.09f, 0, SnowV2.Boot), Is.GreaterThan(0.95f), "the heel");
            Assert.That(D(-0.01f, 0, SnowV2.Boot), Is.LessThan(D(0.06f, 0, SnowV2.Boot)), "the arch is shallower");
            Assert.That(D(0.2f, 0, SnowV2.Boot), Is.EqualTo(0f), "beyond the toe");
            Assert.That(D(0.07f, 0.05f, SnowV2.Boot), Is.GreaterThan(D(-0.09f, 0.05f, SnowV2.Boot)), "the heel is narrower than the ball");
            Assert.That(D(0.06f, 0.05f, SnowV2.Boot, 1), Is.Not.EqualTo(D(0.06f, 0.05f, SnowV2.Boot, -1)).Within(1e-3), "left and right soles are mirrored");
            Assert.That(D(0.06f, 0.03f, SnowV2.Boot, 1), Is.EqualTo(D(0.06f, -0.03f, SnowV2.Boot, -1)).Within(1e-5));
            // soft wall: across the side of the sole the depth falls over >= ~2 cm, monotonically (no steps or teeth)
            float prev = 1.1f; int ramp = 0;
            for (float v = 0; v < 0.1f; v += 0.002f)
            {
                float d = D(0.06f, v, SnowV2.Boot);
                Assert.That(d, Is.LessThanOrEqualTo(prev + 1e-4f), $"monotone at {v:0.000}");
                if (d > 0.02f && d < 0.98f) ramp++;
                prev = d;
            }
            Assert.That(ramp * 0.002f, Is.GreaterThanOrEqualTo(0.012f), "the wall ramps over >= 1.2 cm");
            var rim = SnowV2.Print(0.165f, 0, 0.14f, 0.055f, SnowV2.Boot, 1, SnowV2.Wall);
            Assert.That(rim.rim, Is.GreaterThan(0.3f), "pushed-up rim at the toe");
            Assert.That(SnowV2.Print(0.06f, 0, 0.14f, 0.055f, SnowV2.Boot, 1, SnowV2.Wall).rim, Is.EqualTo(0f).Within(1e-3), "no rim inside");
            Assert.That(SnowV2.Print(0.06f, 0.15f, 0.14f, 0.055f, SnowV2.Boot, 1, SnowV2.Wall).rim, Is.EqualTo(0f), "rim ends");
        }

        [Test]
        public void Footprint_PawHasPadsAndToes_SizesFromTheSkeleton()
        {
            const float hl = 0.055f, hw = 0.045f;
            float main = D(-0.32f * hl, 0, SnowV2.Paw, 1, hl, hw), toe = D(0.46f * hl, 0.2f * hw, SnowV2.Paw, 1, hl, hw);
            float between = D(0.46f * hl, 0, SnowV2.Paw, 1, hl, hw), gap = D(0.1f * hl, 0, SnowV2.Paw, 1, hl, hw);
            Assert.That(main, Is.GreaterThan(0.9f), "main pad");
            Assert.That(toe, Is.GreaterThan(0.8f), "front toe");
            Assert.That(D(0.12f * hl, 0.62f * hw, SnowV2.Paw, 1, hl, hw), Is.GreaterThan(0.7f), "outer toe");
            Assert.That(between, Is.LessThan(toe - 0.2f), "the toes are separate dents");
            Assert.That(gap, Is.LessThan(main - 0.2f), "toes apart from the main pad");
            Assert.That(gap, Is.GreaterThan(0.2f), "inside the paw's shallow dent");
            Assert.That(D(1.5f * hl, 0, SnowV2.Paw, 1, hl, hw), Is.EqualTo(0f));
            Assert.That(D(0, 0, SnowV2.Round, 1, 0.1f, 0.08f), Is.EqualTo(1f).Within(1e-4), "round trail");
            var human = SnowV2.PrintSize(1.8f, false);
            Assert.That(human.halfLength * 2, Is.InRange(0.26f, 0.32f));
            Assert.That(human.halfWidth * 2, Is.InRange(0.09f, 0.13f));
            var wolf = SnowV2.PrintSize(1.0f, true);
            Assert.That(wolf.halfLength * 2, Is.InRange(0.09f, 0.13f));
            Assert.That(SnowV2.IsFootBone("Hero_Left_Foot_Foot_1"), Is.True);
            Assert.That(SnowV2.IsFootBone("Wolf_Right_Foot_Foot_1"), Is.True);
            Assert.That(SnowV2.IsFootBone("Wolf_Right_Foot_Toe_1"), Is.False);
            Assert.That(SnowV2.IsPawBone("Wolf_Left_Hand_Hand_1"), Is.True);
            Assert.That(SnowV2.IsPawBone("Wolf_Left_Hand_Finger_1"), Is.False);
        }

        [Test]
        public void PrintKind_FollowsTheRig_NotAPassingPose()
        {
            const float none = float.MaxValue;
            Assert.That(SnowV2.RigOfBone("Hero_Left_Foot_Foot_1"), Is.EqualTo("Hero"));
            Assert.That(SnowV2.RigOfBone("Wolf_Right_Hand_Hand_1"), Is.EqualTo("Wolf"));
            Assert.That(SnowV2.RigOfBone("Bip01"), Is.Null);
            // the hero standing: boots; swimming, crouched or just back from a wolf form (hands as low as the feet): still boots
            Assert.That(SnowV2.IsQuadruped("Hero", 0.05f, 0.8f, 1.8f), Is.False);
            Assert.That(SnowV2.IsQuadruped("Hero", 0.3f, 0.3f, 1.8f), Is.False, "fix-193: the human hero never prints paws");
            Assert.That(SnowV2.IsQuadruped("Orc", 0.1f, 0.1f, 2f), Is.False);
            // the wolf (the hero's animal form too): paws, whatever the pose when first seen
            Assert.That(SnowV2.IsQuadruped("Wolf", 0.05f, 0.06f, 0.9f), Is.True);
            Assert.That(SnowV2.IsQuadruped("Wolf", 0.05f, 0.7f, 0.9f), Is.True, "a wolf rearing up still prints paws");
            // unknown rigs: by the pose
            Assert.That(SnowV2.IsQuadruped("Lurker", 0.05f, 0.1f, 1.5f), Is.True);
            Assert.That(SnowV2.IsQuadruped("Lurker", 0.05f, 1.0f, 1.5f), Is.False);
            Assert.That(SnowV2.IsQuadruped(null, 0.05f, 0.1f, 1.5f), Is.True);
            // one kind of bone only: feet
            Assert.That(SnowV2.IsQuadruped("Wolf", 0.05f, none, 0.9f), Is.False);
            Assert.That(SnowV2.IsQuadruped("Scavenger", 0.05f, none, 1.4f), Is.False);
        }

        [Test]
        public void FootPlant_OnePrintPerStep_NoneWhileStanding()
        {
            // a walk at 1.5 m/s, 60 fps: each foot swings (2x body speed, lifted 10 cm) for 0.3 s, then stands 0.3 s
            var foot = new SnowV2.FootPlant();
            float dt = 1 / 60f, x = 0, t = 0;
            int prints = 0;
            for (int step = 0; step < 6; step++)
            {
                for (int i = 0; i < 18; i++) { x += 3f * dt; t += dt; if (foot.Update(x, 0.18f, 0, 1.5f, dt, t)) prints++; }
                for (int i = 0; i < 18; i++) { t += dt; if (foot.Update(x, 0.08f, 0, 1.5f, dt, t)) prints++; }
            }
            Assert.That(prints, Is.EqualTo(6));
            var still = new SnowV2.FootPlant();
            int n = 0;
            for (int i = 0; i < 200; i++) if (still.Update(0, 0.08f, 0, 0, dt, i * dt)) n++;
            Assert.That(n, Is.EqualTo(0), "standing still leaves no new prints");
            var hitch = new SnowV2.FootPlant();
            Assert.That(hitch.Update(0, 0.08f, 0, 1.5f, 0.5f, 0), Is.False, "a hitch frame only resets");
        }

        [Test]
        public void RainShadow_DryEconomy_TracesNewCellsOnceThenRefreshesSlowly()
        {
            var g = new G3.Weather.RainShadowGrid(16, 1f);
            int calls = 0;
            System.Func<float, float, float?> trace = (x, z) => { calls++; return null; };
            g.Recenter(0, 0);
            Assert.That(g.Pending, Is.EqualTo(256));
            Assert.That(g.TracePending(trace, 100), Is.EqualTo(100));
            Assert.That(g.TracePending(trace, 1000), Is.EqualTo(156), "only the cells never traced");
            Assert.That(g.Pending, Is.EqualTo(0));
            Assert.That(g.TracePending(trace, 50), Is.EqualTo(0), "nothing left: the caller refreshes slowly with Trace");
            Assert.That(calls, Is.EqualTo(256));
            g.Recenter(6, 0);   // camera left the middle half: the grid moves, the new strip is pending
            Assert.That(g.Pending, Is.InRange(1, 16 * 8));
            int pending = g.Pending;
            Assert.That(g.TracePending(trace, 1000), Is.EqualTo(pending));
            Assert.That(calls, Is.EqualTo(256 + pending), "known cells are not traced again");
        }

        [Test]
        public void Globals_OffIsZero_SettingsClamped()
        {
            Assert.That(SnowV2.SnowGlobal(false, 1), Is.EqualTo(new float[4]));
            var on = SnowV2.SnowGlobal(true, 2);
            Assert.That(on[0], Is.EqualTo(1f));
            Assert.That(on[1], Is.EqualTo(1f), "fresh clamped");
            var w = SnowV2.Snow3Global;
            Assert.That(w[2] * w[2] + w[3] * w[3], Is.EqualTo(1f).Within(1e-5), "wind direction is a unit vector");
            var bad = new WorldLookSettings { snowTrailCells = 300, snowTrailExtent = 1, snowGlint = -5, snowWhite = 3 }.Clamped();
            Assert.That(bad.snowTrailCells, Is.EqualTo(512), "power of two (the shader's modulo is a mask)");
            Assert.That(bad.snowWhite, Is.EqualTo(0.95f));
            Assert.That(bad.snowTrailExtent, Is.EqualTo(16f));
            Assert.That(bad.snowGlint, Is.EqualTo(0f));
            Assert.That(RemasterOptions.IsWorld(RemasterFeature.Snow), Is.False, "a sub-switch, not a world part (F7 leaves it)");
            Assert.That(RemasterOptions.PrefKey(RemasterFeature.Snow), Is.EqualTo("G3.Remaster.Snow"));
            Assert.That(RemasterOptions.EnvVar(RemasterFeature.Snow), Is.EqualTo("G3_REMASTER_SNOW"));
            Assert.That(RemasterOptions.SnowValue(true, false, "Russian"), Does.Contain("Земл"));
            Assert.That(RemasterOptions.SnowValue(false, false, "English"), Is.EqualTo("Off"));
            Assert.That(RemasterOptions.RowName(RemasterFeature.Snow, "Russian").Length, Is.LessThanOrEqualTo(24));
        }

        [Test]
        public void NormalStrength_CharredWoodIsSofter()
        {
            Assert.That(WorldV2.NormalStrength("G3_Architecture_Wood_Burned_01_A_Diffuse_01", null, false, false), Is.EqualTo(0.6f), "fix-187 campfire stand");
            Assert.That(WorldV2.NormalStrength("G3_Architecture_Wood_04_C_Diffuse_01", null, false, false), Is.EqualTo(1.2f));
            Assert.That(WorldV2.NormalStrength("G3_Nature_Rock_01", null, false, true), Is.EqualTo(1.5f));
            Assert.That(WorldV2.NormalStrength("G3_Nature_Rock_01", null, true, true), Is.EqualTo(1.0f));
        }
    }
}
