using System;
using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-93: an NPC walking into the standing hero goes round him (G3 CorrectDCCPositions → CalcPreferedStandInterPose,
    /// NPCEvadeSwitch) instead of walking through him; it waits when he stands on its goal or no side is free.
    /// </summary>
    public class NpcEvadeRulesTests
    {
        const float Npc = 0.35f, Hero = 0.35f, Speed = 1.6f, Dt = 1f / 30f;
        const int HeroId = 1;

        static EvadeBody StandingHero(float x = 0f, float z = 0f, bool moving = false) => new EvadeBody(HeroId, x, z, Hero, moving, isPlayer: true);

        /// <summary>Walks an NPC from (x, z) to the goal like NpcWalker.Step (steer, then clip at the hero); returns the run.</summary>
        static (bool arrived, float closest, float minZ, float maxZ, int frames) Walk(float x, float z, float gx, float gz,
                                                                                 List<EvadeBody> bodies, Func<float, float, bool> walkable = null,
                                                                                 int maxFrames = 600)
        {
            var state = new EvadeState();
            float closest = float.MaxValue, minZ = z, maxZ = z;
            var circles = new List<BodyCircle>();
            foreach (var b in bodies) if (b.IsPlayer) circles.Add(new BodyCircle(b.X, b.Z, b.Radius));
            for (int f = 0; f < maxFrames; f++)
            {
                float tx = gx - x, tz = gz - z, dist = (float)Math.Sqrt(tx * tx + tz * tz);
                float step = Speed * Dt;
                if (dist <= Math.Max(step, 0.2f)) return (true, closest, minZ, maxZ, f);
                float dx = tx / dist, dz = tz / dist;
                var kind = NpcEvadeRules.Steer(x, z, Npc, dx, dz, step, gx, gz, bodies, ref state, Dt, walkable);
                if (kind == EvadeStep.Detour)
                {
                    float ex = state.ToX - x, ez = state.ToZ - z, el = (float)Math.Sqrt(ex * ex + ez * ez);
                    if (el > 1e-3f) { dx = ex / el; dz = ez / el; dist = el; }
                }
                float mx = dx * Math.Min(step, dist), mz = dz * Math.Min(step, dist);
                BodyBlockRules.Clip(x, z, Npc, ref mx, ref mz, circles);
                x += mx;
                z += mz;
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
                foreach (var b in bodies)
                    closest = Math.Min(closest, (float)Math.Sqrt((x - b.X) * (x - b.X) + (z - b.Z) * (z - b.Z)));
            }
            return (false, closest, minZ, maxZ, maxFrames);
        }

        [Test]
        public void AnNpcWalkingIntoTheStandingHero_GoesRoundHim_NeverThrough()
        {
            var run = Walk(-5f, 0f, 5f, 0f, new List<EvadeBody> { StandingHero() });
            Assert.That(run.arrived, Is.True, "reaches its goal past the hero");
            Assert.That(run.closest, Is.GreaterThanOrEqualTo(Npc + Hero - 0.01f), "never inside him");
            Assert.That(Math.Max(-run.minZ, run.maxZ), Is.GreaterThan(0.5f), "stepped aside");
        }

        [Test]
        public void TheSide_IsTheShorterWayRound()
        {
            // The hero stands a little to the walk's left (+z): the NPC passes on his right (−z side of him).
            var left = Walk(-5f, 0f, 5f, 0f, new List<EvadeBody> { StandingHero(0f, 0.2f) });
            Assert.That(left.arrived, Is.True);
            Assert.That(left.minZ, Is.LessThan(-0.4f));
            Assert.That(left.maxZ, Is.LessThan(0.05f));
            var right = Walk(-5f, 0f, 5f, 0f, new List<EvadeBody> { StandingHero(0f, -0.2f) });
            Assert.That(right.maxZ, Is.GreaterThan(0.4f));
            Assert.That(right.minZ, Is.GreaterThan(-0.05f));
        }

        [Test]
        public void TheInterPose_LiesBesideTheBody_At1_25TimesTheRadii()
        {
            var state = new EvadeState();
            var bodies = new List<EvadeBody> { StandingHero(1f, 0f) };
            var kind = NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, bodies, ref state, Dt);
            Assert.That(kind, Is.EqualTo(EvadeStep.Detour));
            Assert.That(state.ToX, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(Math.Abs(state.ToZ), Is.EqualTo((Npc + Hero) * NpcEvadeRules.InterPoseFactor).Within(1e-4f));
            Assert.That(state.EvadedId, Is.EqualTo(HeroId));
        }

        [Test]
        public void TheSideIsKept_ForTheSameBody_NoPingPong()
        {
            var bodies = new List<EvadeBody> { StandingHero(1f, -0.1f) };
            var state = new EvadeState { EvadedId = HeroId, Side = 1 };
            NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, bodies, ref state, Dt);
            Assert.That(state.Side, Is.EqualTo(1), "the remembered side, though the other would be shorter");
        }

        [Test]
        public void ABlockedSide_TakesTheOther_BothBlocked_Waits()
        {
            var bodies = new List<EvadeBody> { StandingHero(1f, 0f) };
            var state = new EvadeState();
            var kind = NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, bodies, ref state, Dt, (px, pz) => pz > 0f);
            Assert.That(kind, Is.EqualTo(EvadeStep.Detour));
            Assert.That(state.ToZ, Is.GreaterThan(0f), "the free (left) side");
            state = new EvadeState();
            kind = NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, bodies, ref state, Dt, (px, pz) => false);
            Assert.That(kind, Is.EqualTo(EvadeStep.Wait), "a wall on both sides: stop at him, no shove");
            var run = Walk(-2f, 0f, 5f, 0f, bodies, (px, pz) => false, maxFrames: 120);
            Assert.That(run.arrived, Is.False);
            Assert.That(run.closest, Is.GreaterThanOrEqualTo(Npc + Hero - 0.01f));
        }

        [Test]
        public void TheHeroOnTheGoal_TheNpcStopsAtHim()
        {
            var bodies = new List<EvadeBody> { StandingHero(5f, 0f) };
            var state = new EvadeState();
            Assert.That(NpcEvadeRules.Steer(4.3f, 0f, Npc, 1f, 0f, 0.1f, 5f, 0f, bodies, ref state, Dt), Is.EqualTo(EvadeStep.Wait));
            var run = Walk(0f, 0f, 5f, 0f, bodies, maxFrames: 200);
            Assert.That(run.arrived, Is.False);
            Assert.That(run.closest, Is.GreaterThanOrEqualTo(Npc + Hero - 0.01f));
        }

        [Test]
        public void AWalkingHero_IsNotEvaded_ButNeverEntered_StandingNpcsAreGoneRound()
        {
            var state = new EvadeState();
            var moving = new List<EvadeBody> { StandingHero(1f, 0f, moving: true) };
            Assert.That(NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, moving, ref state, Dt), Is.EqualTo(EvadeStep.Wait));
            var npc = new List<EvadeBody> { new EvadeBody(7, 1f, 0f, 0.35f, moving: false, isPlayer: false) };
            Assert.That(NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, npc, ref state, Dt), Is.EqualTo(EvadeStep.Detour),
                        "G3 evades standing NPCs as well (CheckAllControlledDCCs over the NPC circles)");
            state = new EvadeState();
            var walkingNpc = new List<EvadeBody> { new EvadeBody(7, 1f, 0f, 0.35f, moving: true, isPlayer: false) };
            Assert.That(NpcEvadeRules.Steer(0.35f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, walkingNpc, ref state, Dt), Is.EqualTo(EvadeStep.None));
        }

        [Test]
        public void ABodyBehindOrBeside_DoesNotStopTheWalk()
        {
            var state = new EvadeState();
            var behind = new List<EvadeBody> { StandingHero(-0.5f, 0f) };
            Assert.That(NpcEvadeRules.Steer(0f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, behind, ref state, Dt), Is.EqualTo(EvadeStep.None));
            var far = new List<EvadeBody> { StandingHero(1f, 1.5f) };
            Assert.That(NpcEvadeRules.Steer(0f, 0f, Npc, 1f, 0f, 0.1f, 10f, 0f, far, ref state, Dt), Is.EqualTo(EvadeStep.None));
        }
    }
}
