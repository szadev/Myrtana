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
    /// fix-76 "drawing a weapon while running jerks the hero down": the hands change in the middle of the draw (or the
    /// sheathe) and the locomotion switches from the unarmed run set to the weapon's. The G3 run sets of every hand class
    /// share their leg and head tracks, so the switch is seamless when the new set continues the cycle's phase
    /// (<see cref="MotionBlendRules.StartPhase"/>); restarting it at 0 cross-faded an out-of-phase cycle — the head moved
    /// by up to 9.4 cm and the stride stumbled. Reads the installed game's _compiledAnimation volume (G3_DATA or the
    /// Steam path); ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class HeroDrawRunTests
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

        static readonly string[] Sets = { "None_None", "None_1H", "None_2H", "None_Fist", "None_Staff", "None_Cast" };
        const int Samples = 24;

        int Node(string n) => Enumerable.Range(0, _actor.Nodes.Count).First(i => _actor.Nodes[i].Name == n);

        /// <summary>Head, left foot, right foot (cm, model space) over one cycle of the set's run, or null when missing.</summary>
        List<float[]> Run(string set)
        {
            string name = $"Hero_Stand_{set}_P0_Move_Run_N_Fwd_00_%_00_P0_400";
            var e = _fs.GetByName(name + ".xmot");
            if (e == null) return null;
            var mot = Xmot.Read(e.Read(), name);
            var map = ActorPose.MapTracks(_actor, mot);
            var held = ActorPose.HeldHelpers(_actor, map);
            int head = Node("Hero_Head_Head_1"), lf = Node("Hero_Left_Foot_Foot_1"), rf = Node("Hero_Right_Foot_Foot_1");
            var o = new List<float[]>();
            for (int f = 0; f < Samples; f++)
            {
                var p = ActorPose.Sample(_actor, map, held, mot.Duration * f / Samples);
                o.Add(new[] { p[head].Pos.Y, p[lf].Pos.Y, p[lf].Pos.Z, p[rf].Pos.Y, p[rf].Pos.Z });
            }
            return o;
        }

        [Test]
        public void Run_sets_of_all_hands_share_legs_and_head_so_a_set_switch_keeps_the_phase()
        {
            var unarmed = Run("None_None");
            Assert.That(unarmed, Is.Not.Null);
            int compared = 0;
            foreach (var set in Sets.Skip(1))
            {
                var armed = Run(set);
                if (armed == null) continue;
                for (int f = 0; f < Samples; f++)
                    for (int k = 0; k < unarmed[f].Length; k++)
                        Assert.That(armed[f][k], Is.EqualTo(unarmed[f][k]).Within(0.5f),
                                    $"{set} run differs from the unarmed run at phase {f / (float)Samples:0.00} (track {k})");
                compared++;
            }
            Assert.That(compared, Is.GreaterThanOrEqualTo(4));
            // the same cycle: continuing the phase puts the new set exactly where the old one was
            Assert.That(MotionBlendRules.StartPhase(true, true, true, 0.37f), Is.EqualTo(0.37f).Within(1e-6f));
        }

        [Test]
        public void Restarting_the_run_cycle_moves_the_head_by_centimetres()
        {
            var run = Run("None_None");
            float h0 = run[0][0];
            float worst = run.Max(s => Math.Abs(s[0] - h0));
            TestContext.Progress.WriteLine($"run head {run.Min(s => s[0]):F1}…{run.Max(s => s[0]):F1} cm; a restart from the worst phase moves it {worst:F1} cm");
            Assert.That(worst, Is.GreaterThan(5f), "a restart at phase 0 is a visible jerk (why StartPhase continues the cycle)");
        }
    }
}
