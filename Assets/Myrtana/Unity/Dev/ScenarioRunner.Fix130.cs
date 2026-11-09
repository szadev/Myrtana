#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Globalization;
using G3.Unity;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-130 ops and probes (Docs/dev-scenarios.md): 'climbto' (the hero runs at a target jumping whenever he stalls, as a
    /// player climbs a crate stack; the highest his feet got is logged and checked against maxy=, a roof he must not end
    /// on) and the rain probes rainin / rainshelter / indoors / rainsurface.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static readonly CultureInfo Inv130 = CultureInfo.InvariantCulture;

        /// <summary>The highest the hero's feet got during the last 'climbto' (condition climbmaxy).</summary>
        float _climbMaxY = float.NaN;

        /// <summary>
        /// 'climbto target=x,y,z [secs=12] [near=0.8] [maxy=] [expect=blocked|any]': runs at the target (flat steering, the
        /// real controller), jumping when he has gained no ground for 0.35 s on the ground. Reached = within near m flat and
        /// 0.6 m up/down of the target. Fails when the feet ever got above maxy (the top of a roof over the climb), or when
        /// not reached (expect=blocked: passes when not reached, still under maxy; expect=any: only maxy is checked).
        /// </summary>
        IEnumerator ClimbToStep(ScenarioStep st)
        {
            string text = st.Get("target");
            if (!TryTarget(text, out var target, out bool hasY, out _, out _, out var err) || !hasY) { Fail("climbto: " + (err ?? "needs target=x,y,z")); yield break; }
            float secs = st.Float("secs", 12f), near = st.Float("near", 0.8f);
            float maxY = st.Has("maxy") ? st.Float("maxy", float.MaxValue) : float.MaxValue;
            bool blocked = string.Equals(st.Get("expect"), "blocked", System.StringComparison.OrdinalIgnoreCase);
            bool any = string.Equals(st.Get("expect"), "any", System.StringComparison.OrdinalIgnoreCase);
            var start = Hero.transform.position;
            int guards0 = Hero.CeilingGuards, steps0 = Hero.StepUps, jumps = 0;
            float t0 = Time.time, stallFor = 0f, bestLeft = float.MaxValue;
            _climbMaxY = start.y;
            bool reached = false;
            while (Time.time - t0 < secs)
            {
                var hero = Hero.transform.position;
                _climbMaxY = Mathf.Max(_climbMaxY, hero.y);
                var to = target - hero;
                float dy = to.y;
                to.y = 0;
                float left = to.magnitude;
                if (left <= near && Mathf.Abs(dy) <= 0.6f && Hero.IsGrounded) { reached = true; break; }
                if (left < bestLeft - 0.05f || dy < -0.3f && left < bestLeft + 0.05f) { bestLeft = Mathf.Min(bestLeft, left); stallFor = 0f; }
                else stallFor += Time.deltaTime;
                if (stallFor > 0.35f && Hero.IsGrounded)
                {
                    Hero.scriptedJump = true;
                    jumps++;
                    stallFor = 0f;
                    bestLeft = left;
                }
                if (left > 0.05f) Hero.SetLook(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 5f);
                Hero.scriptedMove = left > near * 0.5f ? new Vector2(0, 1) : Vector2.zero;
                Hero.scriptedWalk = left < 1.5f;
                yield return null;
            }
            Hero.scriptedMove = Vector2.zero;
            Hero.scriptedWalk = false;
            for (int i = 0; i < 30; i++) { _climbMaxY = Mathf.Max(_climbMaxY, Hero.transform.position.y); yield return null; }
            var end = Hero.transform.position;
            string summary = $"climbto {text}: {(reached ? "reached" : "not reached")} at {V(end)} in {F(Time.time - t0)} s, " +
                             $"highest feet {_climbMaxY.ToString("0.00", Inv130)}, {jumps} jumps, step-ups {Hero.StepUps - steps0}, " +
                             $"guards {Hero.CeilingGuards - guards0}, grounded={Hero.IsGrounded}";
            if (_climbMaxY > maxY) { Fail(summary + $" — the feet got above {maxY.ToString("0.00", Inv130)} (onto the roof)"); yield break; }
            if (!any && reached == blocked) { Fail(summary + (blocked ? " (expected blocked)" : "")); yield break; }
            Log(summary);
        }

        /// <summary>
        /// rainin:x,y,z,hx,hy,hz (precipitation drops alive in that box: centre, half sizes), rainshelter:x,y,z (0 open .. 1
        /// under a roof, the rain shadow the materials read; "-" before the first rain), rainsurface:x,z (the highest static
        /// surface over x z as the rain trace sees it), indoors (0..1, the camera under a roof: rain loops muffled),
        /// rainshadow (cells traced since placed, "off" when not applied), climbmaxy (highest feet of the last climbto).
        /// </summary>
        string Fix130Probe(string kind, string arg)
        {
            var d = G3WeatherDirector.Instance;
            float[] n = Numbers(arg);
            switch (kind)
            {
                case "climbmaxy": return float.IsNaN(_climbMaxY) ? "-" : _climbMaxY.ToString("0.00", Inv130);
                case "rainin":
                    if (d == null || n.Length < 6) return "-";
                    return d.DropsInside(new Bounds(new Vector3(n[0], n[1], n[2]), 2f * new Vector3(n[3], n[4], n[5]))).ToString(Inv130);
                case "rainshelter":
                    var sh = d != null && n.Length >= 3 ? d.RainShelterAt(new Vector3(n[0], n[1], n[2])) : null;
                    return sh.HasValue ? sh.Value.ToString("0.00", Inv130) : "-";
                case "rainsurface":
                    if (d == null || n.Length < 2) return "-";
                    var y = d.RainSurface(n[0], n[n.Length >= 3 ? 2 : 1]);
                    return y.HasValue ? y.Value.ToString("0.00", Inv130) : "none";
                case "indoors": return d == null ? "-" : d.Indoors.ToString("0.00", Inv130);
                case "rainshadow": return d == null || !d.RainShadowOn ? "off" : d.RainShadowTraced.ToString(Inv130);
                default: return "-";
            }
        }

        static float[] Numbers(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return new float[0];
            var parts = arg.Split(',');
            var r = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, Inv130, out r[i])) return new float[0];
            return r;
        }
    }
}
#endif
