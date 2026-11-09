using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// fix-76: pure rules that keep the third-person camera steady (no engine types; tested in Myrtana.Tests
    /// CameraStabilityTests). G3's camera (gCCameraAI_PS::UpdatePlayerPosition, Game.dll 0x2008f7d0) follows the hero's
    /// head (GetHeadMat) through an implicit low-pass, new = (cur + a·T)/(1 + a) with a = dt/(0.025·s)·(3q + 1)/(q + 1)
    /// (q = gap² in m²: large gaps catch up faster), split into the part along the view and the part across it; it cuts
    /// on a teleport (EntityMovedBigDistance); its traces skip shapes the camera already touches (S_FilterShape) and keep
    /// 17.5 cm off walls (MaintainWallDistance). The port keeps that structure and damps harder where the owner saw jumps
    /// (Docs/hero.md "Camera stability").
    /// </summary>
    public static class CameraStability
    {
        // ------------------------------------------------------------------------------------------- head offset

        /// <summary>Low-pass of the raw head offset while the body follows its locomotion (s): the run's 2.5 Hz head bob
        /// (9.4 cm peak to peak, Hero_Stand_*_Move_Run) comes through as ±1 cm, a stance change in about half a second.</summary>
        public const float HeadSteadySeconds = 0.3f;
        /// <summary>Low-pass while a one-shot or an upper-body overlay moves the head (attacks, draw/sheathe, stumbles,
        /// get-ups, object uses), s: a swing's dip barely moves the camera, a long pose (sitting, lying) still settles.</summary>
        public const float HeadBusySeconds = 0.8f;
        /// <summary>Head changes inside this band (m) are ignored: what is left of the bob after the low-pass.</summary>
        public const float HeadDeadband = 0.015f;
        /// <summary>Critically damped spring of the camera's head offset onto the filtered value, s (no velocity steps).</summary>
        public const float HeadSpringSeconds = 0.25f;

        /// <summary>State of the head-offset filter (<see cref="FollowHead"/>).</summary>
        public struct HeadFilter
        {
            public float Smooth, Target, Value, Velocity;
            public bool Started;
        }

        /// <summary>
        /// One step of the head offset the pivot follows (m): <paramref name="raw"/> = <see cref="CameraPivotRules.HeadOffset"/>
        /// of the head bone now; low-passed (<see cref="HeadSteadySeconds"/>, or <see cref="HeadBusySeconds"/> while
        /// <paramref name="busy"/>), held inside <see cref="HeadDeadband"/>, then eased by a critically damped spring.
        /// The first call takes the raw value.
        /// </summary>
        public static float FollowHead(ref HeadFilter f, float raw, bool busy, float dt)
        {
            if (!f.Started)
            {
                f = new HeadFilter { Smooth = raw, Target = raw, Value = raw, Started = true };
                return raw;
            }
            if (dt <= 0f) return f.Value;
            f.Smooth = CameraPivotRules.Follow(f.Smooth, raw, dt, busy ? HeadBusySeconds : HeadSteadySeconds);
            float gap = f.Smooth - f.Target;
            if (Math.Abs(gap) > HeadDeadband) f.Target = f.Smooth - Math.Sign(gap) * HeadDeadband;
            f.Value = CameraBoom.SmoothDamp(f.Value, f.Target, ref f.Velocity, HeadSpringSeconds, dt);
            return f.Value;
        }

        // ------------------------------------------------------------------------------------------- feet height

        /// <summary>Ease of a height pop (a step-up, a stair, a step down a ledge, a small teleport) out of the pivot, s.</summary>
        public const float RootPopSeconds = 0.18f;
        /// <summary>Feet height change per metre walked that still counts as continuous (45°; G3's walkable floor goes to
        /// 49°), so slopes are followed without lag.</summary>
        public const float SlopeAllowance = 1.0f;
        /// <summary>The pivot never lags the feet by more than this (m): above G3's 65 cm step, so a step-up is always eased,
        /// never clamped into a jump.</summary>
        public const float RootMaxLag = 0.8f;

        /// <summary>State of the feet-height filter (<see cref="FollowRoot"/>).</summary>
        public struct RootFilter
        {
            public float LastFeet, Offset, Velocity;
            public bool Started;
        }

        /// <summary>
        /// The feet height the pivot uses (m). Height the hero gains or loses continuously — walking a slope at
        /// <paramref name="horizontalSpeed"/> (up to <see cref="SlopeAllowance"/> per metre), jumping and falling at his
        /// intended vertical speed <paramref name="intendedVy"/> (pass 0 on the ground: the controller's stick-down speed is
        /// no movement) — passes straight through (no lag on slopes, the jump arc
        /// stays framed). The rest of a frame's change is a pop (the controller lifts him onto a step in one frame, stairs,
        /// a drop off a ledge's edge): the pivot keeps the old height for it and eases it out over
        /// <see cref="RootPopSeconds"/> (critically damped, at most <see cref="RootMaxLag"/> behind).
        /// </summary>
        public static float FollowRoot(ref RootFilter f, float feetY, float horizontalSpeed, float intendedVy, float dt)
        {
            if (!f.Started || float.IsNaN(feetY))
            {
                f = new RootFilter { LastFeet = feetY, Started = !float.IsNaN(feetY) };
                return feetY;
            }
            float dy = feetY - f.LastFeet;
            f.LastFeet = feetY;
            if (dt <= 0f) return feetY - f.Offset;
            float allow = (Math.Abs(horizontalSpeed) * SlopeAllowance + Math.Abs(intendedVy)) * dt + 0.005f;
            if (Math.Abs(dy) > allow) f.Offset += dy - Math.Sign(dy) * allow;
            if (f.Offset != 0f)
            {
                f.Offset = CameraBoom.SmoothDamp(f.Offset, 0f, ref f.Velocity, RootPopSeconds, dt);
                if (Math.Abs(f.Offset) < 1e-4f) { f.Offset = 0f; f.Velocity = 0f; }
                if (f.Offset > RootMaxLag) f.Offset = RootMaxLag;
                else if (f.Offset < -RootMaxLag) f.Offset = -RootMaxLag;
            }
            return feetY - f.Offset;
        }

        // ------------------------------------------------------------------------------------------- collision

        /// <summary>After an obstacle stops blocking, the boom waits this long before easing out (s): a pole or a trunk
        /// that crosses the boom for a few frames does not pump the camera in and out.</summary>
        public const float RecoverHold = 0.2f;
        /// <summary>Ease-out after an obstacle, s (critically damped). Pulling in is always at once (no clipping).</summary>
        public const float RecoverSeconds = 0.45f;

        /// <summary>The boom's free length as the camera uses it (<see cref="FollowFree"/>).</summary>
        public struct FreeLimit
        {
            public float Value, Velocity, Hold;
            public bool Recovering, Started;
        }

        /// <summary>
        /// Hysteresis of the free boom length: a shorter <paramref name="free"/> is taken at once; after an obstacle
        /// (<paramref name="blocked"/> or recovering from one) a longer one only after <see cref="RecoverHold"/>, then
        /// eased (<see cref="RecoverSeconds"/>). Without an obstacle the limit follows the wished distance directly (zoom
        /// changes keep the G3 distance smoothing, CameraModes.SmoothDistance).
        /// </summary>
        public static float FollowFree(ref FreeLimit s, float free, bool blocked, float dt,
                                       float hold = RecoverHold, float seconds = RecoverSeconds)
        {
            if (!s.Started || free <= s.Value)
            {
                s.Started = true;
                if (blocked || free < s.Value - 1e-4f) s.Hold = hold;
                s.Recovering = blocked || (s.Recovering && free < s.Value - 1e-4f);
                s.Value = free;
                s.Velocity = 0f;
                return free;
            }
            if (!s.Recovering && !blocked)
            {
                s.Value = free;
                s.Velocity = 0f;
                return free;
            }
            s.Recovering = true;
            if (dt <= 0f) return s.Value;
            if (s.Hold > 0f)
            {
                s.Hold -= dt;
                return s.Value;
            }
            s.Value = CameraBoom.SmoothDamp(s.Value, free, ref s.Velocity, seconds, dt);
            if (s.Value >= free - 1e-3f)
            {
                s.Value = free;
                s.Velocity = 0f;
                s.Recovering = blocked;
            }
            return s.Value;
        }

        /// <summary>A character body or a loose physics object (ragdoll limb, dropped item) only stops the boom when it is
        /// within this distance (m) of the camera's end: one walking through the middle of the boom is looked past.</summary>
        public const float SoftReach = 0.6f;

        /// <summary>
        /// Whether a sweep hit pulls the camera in (<see cref="CameraBoom.Blocks"/>), with soft obstacles: characters and
        /// loose physics bodies (<paramref name="soft"/>) block only near the end of the boom (<see cref="SoftReach"/> of
        /// <paramref name="length"/>), so an NPC crossing between the camera and the hero no longer snaps it in and out,
        /// and the camera still never ends up inside a body [interp: G3's trace is blocked by NPCs; changed for stability].
        /// </summary>
        public static bool Blocks(float hitDistance, bool ownBody, float planWidth, float minWidth, bool soft, float length)
        {
            if (hitDistance <= 0f || ownBody) return false;
            if (soft) return hitDistance >= length - SoftReach;
            return planWidth >= minWidth;
        }

        // ------------------------------------------------------------------------------------------- close to the hero (fix-94)

        /// <summary>G3 keeps the camera this far off a surface it is pushed against, m (MaintainWallDistance, 17.5 cm, along
        /// the polygon's normal).</summary>
        public const float WallDistance = 0.175f;

        /// <summary>
        /// How far (m) to move the camera along the surface normal so it ends <paramref name="wall"/> off the plane of the
        /// surface its boom hit, given its signed distance <paramref name="planeDistance"/> to that plane now (0 = no push).
        /// G3 traces a ray from the head and then keeps the camera 17.5 cm off the polygons within 35 cm (along their
        /// normals), so on a slope behind the hero the camera slides up the slope instead of the whole sweep radius being
        /// taken off the boom; a thick sweep pulled the camera in to 0.2 m / sin(grazing angle) short of the slope.
        /// </summary>
        public static float WallPush(float planeDistance, float wall = WallDistance) =>
            planeDistance >= wall ? 0f : wall - Math.Max(planeDistance, -wall);

        /// <summary>
        /// Where the boom's centre line meets the surface a sweep of <paramref name="radius"/> touched after
        /// <paramref name="sweepDistance"/> m (<paramref name="cosToNormal"/> = boom direction · surface normal, negative
        /// toward the surface): G3's ray reaches the surface itself and only then keeps <see cref="WallDistance"/> off it
        /// (<see cref="WallPush"/>). The extension is capped at 5 radii for a boom grazing the surface.
        /// </summary>
        public static float RayHitDistance(float sweepDistance, float radius, float cosToNormal) =>
            sweepDistance + radius / Math.Max(0.2f, -cosToNormal);

        /// <summary>The hero is fully drawn while the camera is at least this far from his eyes, m (G3 100 cm).</summary>
        public const float HeroFadeFar = 1.0f;
        /// <summary>... and not at all at or under this, m (G3 20 cm).</summary>
        public const float HeroFadeNear = 0.2f;

        /// <summary>
        /// G3's render alpha of the hero for a camera <paramref name="eyeDistance"/> m from his eyes (the first-person point):
        /// a half cosine from 1 at <see cref="HeroFadeFar"/> to 0 at <see cref="HeroFadeNear"/> (gCCameraAI_PS::OnProcess →
        /// SetChildrenRenderAlphaValue on the hero and what he carries). G3 never hides only the head in third person:
        /// the whole hero fades out.
        /// </summary>
        public static float HeroAlpha(float eyeDistance)
        {
            float t = (eyeDistance - HeroFadeNear) / (HeroFadeFar - HeroFadeNear);
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return (float)((Math.Sin(Math.PI * t - Math.PI / 2) + 1) / 2);
        }

        /// <summary>
        /// Whether the third-person camera hides the whole hero (his renderers cast shadows only). The port's characters
        /// have no fade, so the hero goes where G3's alpha passes one half (0.6 m from the eyes), with hysteresis
        /// (hidden below alpha 0.4, shown again above 0.6) so a camera hovering there does not flicker [interp].
        /// </summary>
        public static bool HidesHero(float eyeDistance, bool hiddenNow) => HeroAlpha(eyeDistance) < (hiddenNow ? 0.6f : 0.4f);

        // ------------------------------------------------------------------------------------------- moving latch

        /// <summary>The hero counts as standing (G3 IsStanding, MinRangeOnMove) only after this long below the moving speed, s.</summary>
        public const float StandLatchSeconds = 0.35f;

        /// <summary>
        /// Moving with hysteresis for the wished distance (CameraModeParams.WishedDistance): moving at once, standing only
        /// after <see cref="StandLatchSeconds"/> — a stop between two key presses or a stalled step does not pump the camera
        /// between the zoom and MinRangeOnMove.
        /// </summary>
        public static bool MovingLatch(bool movingNow, ref float stillSeconds, float dt)
        {
            if (movingNow) { stillSeconds = 0f; return true; }
            if (dt > 0f) stillSeconds += dt;
            return stillSeconds < StandLatchSeconds;
        }

        /// <summary>fix-256: the MinRangeOnMove floor fades in over this long when the hero starts moving, s [interp].</summary>
        public const float MoveRangeOutSeconds = 0.6f;
        /// <summary>fix-256: … and fades out over this long once he stands (after the latch), s [interp].</summary>
        public const float MoveRangeInSeconds = 1.2f;

        /// <summary>
        /// fix-256 (owner: "I run, release the run and the camera zooms in abruptly" at a close zoom): the weight (0…1) of
        /// MinRangeOnMove in the wished distance (<see cref="CameraModeParams.EasedWishedDistance"/>). The latched moving
        /// flag switched the wished distance from MinRangeOnMove (3 m) to the zoom (2 m) in one frame and
        /// <see cref="CameraModes.SmoothDistance"/> closes about half the gap per frame — a snap. The weight moves linearly
        /// in <paramref name="linear"/> and is returned smoothstepped: the boom eases out and in without a jump.
        /// </summary>
        public static float MoveRangeWeight(ref float linear, bool moving, float dt)
        {
            if (dt > 0f)
                linear = moving ? Math.Min(1f, linear + dt / MoveRangeOutSeconds) : Math.Max(0f, linear - dt / MoveRangeInSeconds);
            float w = Math.Max(0f, Math.Min(1f, linear));
            return w * w * (3f - 2f * w);
        }

        // ------------------------------------------------------------------------------------------- transitions

        /// <summary>A small teleport (an object use's spot, a form ending next to the hero) is blended up to this far (m);
        /// a farther one cuts (G3 EntityMovedBigDistance: the camera jumps with the hero).</summary>
        public const float CutDistance = 4f;
        /// <summary>Blend of the camera pose after a small teleport or a look snap, s.</summary>
        public const float SnapBlendSeconds = 0.4f;
        /// <summary>Blend from the dialog camera back to the player camera, s.</summary>
        public const float DialogBlendSeconds = 0.5f;
        /// <summary>Blend on a G3 camera mode switch (Inventory ↔ Ranged: distance, FOV and look-at lift change), s.</summary>
        public const float ModeBlendSeconds = 0.35f;
        /// <summary>Time constant of a look snap's yaw/pitch offset returning to 0 (the camera swings round), s.</summary>
        public const float LookSnapSeconds = 0.35f;

        /// <summary>Whether a teleport of <paramref name="distance"/> m cuts the camera (else it blends).</summary>
        public static bool Cuts(float distance) => distance > CutDistance;

        /// <summary>Weight of the new pose after <paramref name="elapsed"/> of a <paramref name="duration"/> blend (smoothstep, 0…1).</summary>
        public static float BlendWeight(float elapsed, float duration)
        {
            if (duration <= 0f) return 1f;
            float t = elapsed / duration;
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }
    }

    /// <summary>
    /// fix-76: jitter metrics of a recorded camera path (the dev runner's <c>camprobe</c>; pure, tested). Each frame gives
    /// the frame time, the camera position relative to the hero's feet in the hero's yaw frame (so walking and turning
    /// are not counted), the camera's world height, its yaw relative to the hero's look and its pitch, and optionally the
    /// pelvis height above the feet with the locomotion phase. Metrics are maxima over the frames (a cut restarts the
    /// differences):
    /// <list type="bullet">
    /// <item><c>step</c>: largest per-frame change of the relative position, scaled to a 60 Hz frame (m);</item>
    /// <item><c>jump</c>: largest second difference of the relative position (m) — the velocity changing in one frame, what
    /// a pop or a snap looks like;</item>
    /// <item><c>yjerk</c>: largest second difference of the camera's world height (m): bobbing, step-ups;</item>
    /// <item><c>rotjump</c>: largest second difference of the relative yaw/pitch (degrees);</item>
    /// <item><c>pelvisdip</c>: largest drop of the pelvis below its own median at the same gait phase (m; medians of 48
    /// phase bins, interpolated): a cycle restarted out of phase;</item>
    /// <item><c>phasejump</c>: largest second difference of the locomotion phase (cycles);</item>
    /// <item><c>drift</c> / <c>yawdrift</c>: largest distance (m) / yaw difference (degrees) of the relative camera from
    /// its first frame — for a span in which the camera should not move at all (a talk with nothing to say).</item>
    /// </list>
    /// </summary>
    public sealed class CameraJitterStats
    {
        public const int PhaseBins = 48;

        struct Frame
        {
            public float Dt, X, Y, Z, WorldY, Yaw, Pitch, Pelvis, Phase;
            public bool HasPelvis, Cut;
        }

        readonly List<Frame> _frames = new List<Frame>();

        public int Count => _frames.Count;

        public void Clear() => _frames.Clear();

        /// <summary>Adds one frame; <paramref name="cut"/> = the camera cut before it (teleport): no difference across it.</summary>
        public void Add(float dt, float relX, float relY, float relZ, float worldY, float relYaw, float pitch, bool cut = false,
                        float pelvis = float.NaN, float phase = float.NaN)
        {
            _frames.Add(new Frame
            {
                Dt = dt, X = relX, Y = relY, Z = relZ, WorldY = worldY, Yaw = relYaw, Pitch = pitch, Cut = cut,
                Pelvis = pelvis, Phase = phase, HasPelvis = !float.IsNaN(pelvis) && !float.IsNaN(phase),
            });
        }

        static float Wrap180(float d)
        {
            d %= 360f;
            if (d > 180f) d -= 360f;
            else if (d < -180f) d += 360f;
            return d;
        }

        static float WrapHalf(float d)
        {
            d %= 1f;
            if (d >= 0.5f) d -= 1f;
            else if (d < -0.5f) d += 1f;
            return d;
        }

        /// <summary>The metric by name (see the class summary); NaN for an unknown name or too few frames.</summary>
        public float Metric(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "step": return Step();
                case "jump": return Second((a, b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z), true);
                case "yjerk": return Second((a, b) => (a.WorldY - b.WorldY, 0f, 0f), false);
                case "rotjump": return Second((a, b) => (Wrap180(a.Yaw - b.Yaw), a.Pitch - b.Pitch, 0f), false);
                case "phasejump": return PhaseJump();
                case "pelvisdip": return PelvisDip();
                case "drift": return Drift(false);
                case "yawdrift": return Drift(true);
                case "frames": return _frames.Count;
                default: return float.NaN;
            }
        }

        public static readonly string[] Names = { "step", "jump", "yjerk", "rotjump", "pelvisdip", "phasejump", "drift", "yawdrift" };

        float Drift(bool yaw)
        {
            if (_frames.Count == 0) return float.NaN;
            var f0 = _frames[0];
            float max = 0f;
            foreach (var f in _frames)
            {
                float d;
                if (yaw) d = Math.Abs(Wrap180(f.Yaw - f0.Yaw));
                else
                {
                    float dx = f.X - f0.X, dy = f.Y - f0.Y, dz = f.Z - f0.Z;
                    d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                if (d > max) max = d;
            }
            return max;
        }

        /// <summary>All metrics on one line, for the log.</summary>
        public string Summary()
        {
            var parts = new List<string> { $"frames {_frames.Count}" };
            foreach (var n in Names)
            {
                float v = Metric(n);
                parts.Add(float.IsNaN(v) ? $"{n} -" : $"{n} {v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            return string.Join(", ", parts);
        }

        float Step()
        {
            float max = float.NaN;
            for (int i = 1; i < _frames.Count; i++)
            {
                var a = _frames[i];
                var b = _frames[i - 1];
                if (a.Cut || a.Dt <= 0f) continue;
                float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                float d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) * (1f / 60f) / a.Dt;
                if (float.IsNaN(max) || d > max) max = d;
            }
            return max;
        }

        /// <summary>Largest |d_k − d_{k−1}·dt_k/dt_{k−1}| of a per-frame difference d (the frame-rate-fair second difference).</summary>
        float Second(Func<Frame, Frame, (float, float, float)> delta, bool vector)
        {
            float max = float.NaN;
            for (int i = 2; i < _frames.Count; i++)
            {
                var c = _frames[i];
                var b = _frames[i - 1];
                var a = _frames[i - 2];
                if (c.Cut || b.Cut || c.Dt <= 0f || b.Dt <= 0f) continue;
                var (x1, y1, z1) = delta(c, b);
                var (x0, y0, z0) = delta(b, a);
                float k = c.Dt / b.Dt;
                float ex = x1 - x0 * k, ey = y1 - y0 * k, ez = z1 - z0 * k;
                float v = vector ? (float)Math.Sqrt(ex * ex + ey * ey + ez * ez) : Math.Max(Math.Abs(ex), Math.Max(Math.Abs(ey), Math.Abs(ez)));
                if (float.IsNaN(max) || v > max) max = v;
            }
            return max;
        }

        float PhaseJump()
        {
            float max = float.NaN;
            for (int i = 2; i < _frames.Count; i++)
            {
                var c = _frames[i];
                var b = _frames[i - 1];
                var a = _frames[i - 2];
                if (!c.HasPelvis || !b.HasPelvis || !a.HasPelvis || c.Cut || b.Cut || c.Dt <= 0f || b.Dt <= 0f) continue;
                float d1 = WrapHalf(c.Phase - b.Phase), d0 = WrapHalf(b.Phase - a.Phase);
                float v = Math.Abs(d1 - d0 * c.Dt / b.Dt);
                if (float.IsNaN(max) || v > max) max = v;
            }
            return max;
        }

        static float Unit(float phase)
        {
            float p = phase % 1f;
            return p < 0f ? p + 1f : p;
        }

        static int Bin(float phase) => Math.Min(PhaseBins - 1, (int)(Unit(phase) * PhaseBins));

        /// <summary>The median curve at <paramref name="phase"/>: linear between the bin centres (wrapping), NaN without data.</summary>
        static float Reference(float[] median, float phase)
        {
            float x = Unit(phase) * PhaseBins - 0.5f;
            int i0 = (int)Math.Floor(x);
            float w = x - i0;
            int a = ((i0 % PhaseBins) + PhaseBins) % PhaseBins, b = (a + 1) % PhaseBins;
            float ma = median[a], mb = median[b];
            if (float.IsNaN(ma)) return mb;
            if (float.IsNaN(mb)) return ma;
            return ma + (mb - ma) * w;
        }

        float PelvisDip()
        {
            var bins = new List<float>[PhaseBins];
            foreach (var f in _frames)
            {
                if (!f.HasPelvis) continue;
                int b = Bin(f.Phase);
                (bins[b] ??= new List<float>()).Add(f.Pelvis);
            }
            var median = new float[PhaseBins];
            for (int i = 0; i < PhaseBins; i++)
            {
                if (bins[i] == null || bins[i].Count == 0) { median[i] = float.NaN; continue; }
                bins[i].Sort();
                median[i] = bins[i][bins[i].Count / 2];
            }
            float max = float.NaN;
            foreach (var f in _frames)
            {
                if (!f.HasPelvis) continue;
                float m = Reference(median, f.Phase);
                if (float.IsNaN(m)) continue;
                float dip = Math.Max(0f, m - f.Pelvis);
                if (float.IsNaN(max) || dip > max) max = dip;
            }
            return max;
        }
    }
}
