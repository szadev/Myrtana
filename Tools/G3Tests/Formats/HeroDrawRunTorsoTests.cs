using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Player;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-89 "drawing or sheathing while running crumples the hero": the draw/sheathe overlays (<c>Hold*_O_</c>) of most
    /// hand sets track only the arms (the 1H draw only the right arm). Their keys are relative to <c>Spine_5</c> through the
    /// folded <c>Spine_END</c>/<c>Shoulder_ROOT</c> helpers. The importer used to hold every untracked ancestor at identity
    /// (the whole spine: Spine_2…5 at the pelvis) and the player masked the overlay to the whole <c>Spine_2</c> subtree, so
    /// the spine, neck, head and the other arm took that collapsed pose. G3 (EMotionFX mixing) drives only the nodes an
    /// overlay tracks; the rest keeps running. Layered that way, the torso must stay inside the run's own range
    /// (<see cref="TorsoPoseStats"/>). Reads the installed game's _compiledAnimation volume; ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class HeroDrawRunTorsoTests
    {
        const string Actor = "G3_Hero_Body_Player";
        static G3FileSystem _fs;
        static EmfxActor _actor;

        [OneTimeSetUp]
        public void Open()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledAnimation.pak"))) Assert.Ignore("Gothic 3 data not installed");
            _fs = new G3FileSystem(data, "_compiledAnimation");
            _actor = Xact.Read(_fs.GetByName(Actor + ".xact").Read()).Actor;
        }

        [OneTimeTearDown]
        public void Close() => _fs?.Dispose();

        int Node(string n) => Enumerable.Range(0, _actor.Nodes.Count).First(i => _actor.Nodes[i].Name == n);

        MotionResource Motion(string name)
        {
            var e = _fs.GetByName(name + ".xmot");
            return e == null ? null : Xmot.Read(e.Read(), name);
        }

        /// <summary>The set's forward run (the shield set's ends in P1_400).</summary>
        string Run(string set)
        {
            string prefix = $"Hero_Stand_{set}_P0_Move_Run_N_Fwd_00_%_00_";
            var f = _fs.Files.FirstOrDefault(x => Path.GetFileName(x.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                  x.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase));
            return f != null ? Path.GetFileNameWithoutExtension(f.Path) : prefix + "P0_400";
        }

        /// <summary>Hand sets and their draw/sheathe overlays (part words of <see cref="HoldMotionRules"/>).</summary>
        static readonly (string set, string hold)[] Holds =
        {
            ("None_1H", "HoldRight"), ("None_2H", "HoldRight"), ("None_Fist", "HoldRight"), ("None_Staff", "HoldRight"),
            ("None_Cast", "HoldRight"), ("Bow_None", "HoldLeft"), ("Shield_1H", "HoldLeft"), ("1H_1H", "Hold"),
        };

        const int RunSamples = 40, OverlaySamples = 12;

        /// <summary>
        /// Torso stats of the set's run alone (the range) and with the overlay layered on top at several run phases.
        /// <paramref name="legacy"/>: the old import and mask (all untracked ancestors held, the whole Spine_2 subtree).
        /// </summary>
        TorsoPoseStats Measure(string set, string overlay, bool legacy)
        {
            var run = Motion(Run(set));
            var over = Motion(overlay);
            Assume.That(run, Is.Not.Null, Run(set));
            Assume.That(over, Is.Not.Null, overlay);
            var runMap = ActorPose.MapTracks(_actor, run);
            var runHeld = ActorPose.HeldHelpers(_actor, runMap);
            var ovMap = ActorPose.MapTracks(_actor, over);
            bool[] ovHeld, drives;
            if (legacy)
            {
                ovHeld = new bool[ovMap.Length];
                for (int i = 0; i < ovMap.Length; i++)
                    if (ovMap[i] != null)
                        for (int p = _actor.Nodes[i].Parent; p >= 0 && ovMap[p] == null; p = _actor.Nodes[p].Parent) ovHeld[p] = true;
                int spine2 = Node("Hero_Spine_Spine_2");
                drives = Enumerable.Range(0, ovMap.Length).Select(i => Under(i, spine2)).ToArray();
            }
            else
            {
                ovHeld = ActorPose.HeldHelpers(_actor, ovMap);
                drives = ActorPose.Drives(ovMap, ovHeld);
            }
            int pelvis = Node("Hero_Spine_Spine_1"), chest = Node("Hero_Spine_Spine_3"), head = Node("Hero_Head_Head_1");
            var stats = new TorsoPoseStats();
            void Add(ActorPose.Xf[] p, bool ov) =>
                stats.Add(p[pelvis].Pos.X, p[pelvis].Pos.Y, p[pelvis].Pos.Z, p[chest].Pos.X, p[chest].Pos.Y, p[chest].Pos.Z,
                          p[head].Pos.X, p[head].Pos.Y, p[head].Pos.Z, ov);
            for (int f = 0; f < RunSamples; f++)
                Add(ActorPose.Sample(_actor, runMap, runHeld, run.Duration * f / RunSamples), false);
            for (int start = 0; start < 4; start++) // the draw starts anywhere in the cycle
                for (int f = 0; f <= OverlaySamples; f++)
                {
                    float to = over.Duration * f / OverlaySamples;
                    float tb = (run.Duration * start / 4 + to) % run.Duration;
                    Add(ActorPose.SampleLayered(_actor, runMap, runHeld, tb, ovMap, ovHeld, drives, to), true);
                }
            return stats;
        }

        bool Under(int node, int ancestor)
        {
            for (int p = node; p >= 0; p = _actor.Nodes[p].Parent)
                if (p == ancestor) return true;
            return false;
        }

        IEnumerable<(string set, string motion)> Overlays()
        {
            foreach (var (set, hold) in Holds)
                foreach (var phase in new[] { "Begin", "End" })
                {
                    string name = $"Hero_Stand_{set}_P0_{hold}_{phase}_O_Fwd_00_%_00_P0_0";
                    if (_fs.GetByName(name + ".xmot") != null && _fs.GetByName(Run(set) + ".xmot") != null) yield return (set, name);
                }
        }

        [Test]
        public void Arm_only_draw_overlay_folds_only_helpers_never_the_spine()
        {
            var over = Motion("Hero_Stand_None_1H_P0_HoldRight_Begin_O_Fwd_00_%_00_P0_0");
            var map = ActorPose.MapTracks(_actor, over);
            Assert.That(map[Node("Hero_Spine_Spine_5")], Is.Null, "the 1H draw overlay tracks only the right arm");
            var held = ActorPose.HeldHelpers(_actor, map);
            var heldNames = Enumerable.Range(0, held.Length).Where(i => held[i]).Select(i => _actor.Nodes[i].Name).ToList();
            TestContext.Progress.WriteLine("held: " + string.Join(", ", heldNames));
            Assert.That(heldNames, Has.Member("Hero_Spine_Spine_END").And.Member("Hero_Right_Arm_Shoulder_ROOT"));
            Assert.That(heldNames.Where(n => !ActorPose.IsHelper(n)), Is.Empty, "no real bone is held at identity");
            var drives = ActorPose.Drives(map, held);
            foreach (var bone in new[] { "Hero_Spine_Spine_2", "Hero_Spine_Spine_5", "Hero_Head_Head_1", "Hero_Left_Arm_Arm_1" })
                Assert.That(drives[Node(bone)], Is.False, bone + " keeps the run");
            Assert.That(drives[Node("Hero_Right_Arm_Arm_1")], Is.True);
        }

        [Test]
        public void Full_body_motions_hold_the_same_helpers_as_before()
        {
            // the old rule: every untracked ancestor up to the nearest tracked one; full-body motions only pass helpers
            int checkedMotions = 0;
            foreach (var f in _fs.Files.Where(f => f.Path.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase)))
            {
                string stem = Path.GetFileNameWithoutExtension(f.Path);
                var name = MotionName.Parse(stem);
                if (name == null || name.Rig != "Hero" || name.Variant != "N" || checkedMotions >= 400) continue;
                var map = ActorPose.MapTracks(_actor, Xmot.Read(f.Read(), stem));
                if (map[Node("Hero_Spine_Spine_1")] == null) continue;
                var held = ActorPose.HeldHelpers(_actor, map);
                for (int i = 0; i < map.Length; i++)
                {
                    if (map[i] == null) continue;
                    int top = _actor.Nodes[i].Parent;
                    while (top >= 0 && map[top] == null) top = _actor.Nodes[top].Parent;
                    if (top < 0) continue;
                    for (int p = _actor.Nodes[i].Parent; p != top; p = _actor.Nodes[p].Parent)
                        Assert.That(held[p], Is.True, $"{stem}: {_actor.Nodes[p].Name}");
                }
                checkedMotions++;
            }
            Assert.That(checkedMotions, Is.GreaterThan(100));
        }

        [Test]
        public void Draw_and_sheathe_overlays_keep_the_torso_inside_the_run_range()
        {
            int n = 0;
            foreach (var (set, motion) in Overlays())
            {
                var s = Measure(set, motion, legacy: false);
                var old = Measure(set, motion, legacy: true);
                TestContext.Progress.WriteLine($"{motion}: shrink {s.Metric("shrink"):F2} cm, bend +{s.Metric("bend"):F1}° " +
                                               $"(run {s.Metric("runmin"):F1}…{s.Metric("runmax"):F1} cm, bend ≤{s.Metric("runbend"):F1}°); " +
                                               $"old import+mask: shrink {old.Metric("shrink"):F1} cm, bend +{old.Metric("bend"):F1}°");
                Assert.That(s.Metric("shrink"), Is.LessThan(1.0f), motion + ": the pelvis–head distance falls below the run's");
                Assert.That(s.Metric("bend"), Is.LessThan(3.0f), motion + ": the torso bends more than in the run");
                n++;
            }
            Assert.That(n, Is.GreaterThanOrEqualTo(10));
        }

        [Test]
        public void Old_import_and_mask_crumpled_the_one_handed_draw()
        {
            var old = Measure("None_1H", "Hero_Stand_None_1H_P0_HoldRight_Begin_O_Fwd_00_%_00_P0_0", legacy: true);
            TestContext.Progress.WriteLine("old 1H draw over the run: " + old.Summary());
            Assert.That(old.Metric("shrink"), Is.GreaterThan(10f), "the collapsed spine pulls the head towards the pelvis");
        }
    }
}
