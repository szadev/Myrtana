using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>
    /// The effect events of a motion's .xmot effect track (Docs/formats-animation.md §8.7) that one animation step passes,
    /// and the effect each one starts. G3 fires an event when the motion's time crosses its key: a looping motion fires its
    /// events again every cycle (the grindstone's <c>EFF_Ani_Sharpeningstone_Wheel_01</c> at 0 s of every 2 s loop), a
    /// one-shot only once. <c>[F]</c> events play only forwards, <c>[R]</c> only backwards (a sheathe is the draw motion
    /// played backwards). The effect name (case-insensitive, as in g3.efm) starts the sound (<c>G3MotionSounds</c>) and the
    /// visuals (<c>G3EffectSystem</c>); the human steps are left to the footstep rules.
    /// </summary>
    public static class MotionEventWindow
    {
        /// <summary>One key of an effect track: seconds into the motion and the effect name as stored (may carry [F]/[R]).</summary>
        public readonly struct TrackEvent
        {
            public readonly float Time;
            public readonly string Effect;

            public TrackEvent(float time, string effect)
            {
                Time = time;
                Effect = effect;
            }
        }

        /// <summary>
        /// The effect an event starts when the motion plays forwards or backwards, without its direction mark; null when the
        /// event belongs to the other direction or is empty.
        /// </summary>
        public static string EffectFor(string trackEvent, bool reverse)
        {
            if (string.IsNullOrEmpty(trackEvent)) return null;
            bool forwardOnly = trackEvent.StartsWith("[F]", StringComparison.Ordinal);
            bool reverseOnly = trackEvent.StartsWith("[R]", StringComparison.Ordinal);
            if ((reverse && forwardOnly) || (!reverse && reverseOnly)) return null;
            string name = forwardOnly || reverseOnly ? trackEvent.Substring(3) : trackEvent;
            return name.Length > 0 ? name : null;
        }

        /// <summary>The motion sound of an effect is played from the effect track (false: human steps, G3Footsteps' job).</summary>
        public static bool PlaysFromTrack(string effect) =>
            !string.IsNullOrEmpty(effect) && !effect.StartsWith("eff_step_human", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Adds to <paramref name="into"/> the effects of the events whose normalised time lies in the step's unwrapped phase
        /// range [<paramref name="from"/>, <paramref name="to"/>) (a loop's phase keeps counting past 1 within a step; the
        /// range is empty before the first step). An event at the very end of a motion counts as just before it, so a
        /// one-shot clamped at 1 still fires it. Returns the number added.
        /// </summary>
        public static int Collect(IReadOnlyList<TrackEvent> events, float length, float from, float to, bool loop, bool reverse,
                                  List<string> into)
        {
            if (events == null || events.Count == 0 || to <= from || into == null) return 0;
            length = Math.Max(length, 1e-4f);
            int added = 0;
            for (float cycle = (float)Math.Floor(from); cycle < to; cycle += 1)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    string name = EffectFor(e.Effect, reverse);
                    if (name == null) continue;
                    float at = Math.Min(e.Time / length, 0.999f);
                    float t = cycle + (reverse ? Math.Max(0f, 1f - at - 1e-4f) : at);
                    if (t >= from && t < to)
                    {
                        into.Add(name);
                        added++;
                    }
                }
                if (!loop) break;
            }
            return added;
        }
    }
}
