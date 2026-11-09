#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Linq;
using G3.Unity;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-94 ops and probes (Docs/dev-scenarios.md): the hero with his back to a steep slope (the third-person camera
    /// pressed against it: G3 rides it off the surface and fades the whole hero, never only his head), and loot page
    /// titles that would show a raw FO_ id.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        float _slopeMinEye = -1f;
        int _slopeFrames, _slopeHiddenFrames, _slopeFirstPersonFrames;

        /// <summary>
        /// 'slopeback [radius=80] [min=45] [max=70] [near=1] [far=2.5] [pitch=0] [secs=2]': finds ground steeper than min° (up to
        /// max°) within radius m of the hero, stands him on flat ground at its foot with his back to it (the slope near…far m
        /// behind his head, horizontally), holds the look (pitch: + down)
        /// for secs and records the camera: the smallest eye distance and the frames in which the hero was hidden or in
        /// the first-person body view (probes slopemineye, slopehidden, slopefp).
        /// </summary>
        IEnumerator SlopeBackStep(ScenarioStep st)
        {
            float radius = st.Float("radius", 80f), minDeg = st.Float("min", 45f), maxDeg = st.Float("max", 70f);
            float pitch = st.Float("pitch", 0f), secs = st.Float("secs", 2f);
            float near = st.Float("near", 1f), far = st.Float("far", 2.5f);
            var origin = Hero.transform.position;
            var heroRoot = Hero.transform;
            bool found = false;
            Vector3 foot = default, slope = default;
            float yaw = 0f, behind = 0f;
            for (float r = 4f; r <= radius && !found; r += 4f)
            {
                int n = Mathf.Max(8, Mathf.RoundToInt(r * 0.8f));
                for (int i = 0; i < n && !found; i++)
                {
                    var c = origin + Quaternion.Euler(0, i * 360f / n, 0) * Vector3.forward * r;
                    if (!GroundBelow(c, heroRoot, out var hit)) continue;
                    float deg = Vector3.Angle(hit.normal, Vector3.up);
                    if (deg < minDeg || deg > maxDeg) continue;
                    var down = new Vector3(hit.normal.x, 0f, hit.normal.z).normalized; // downhill, away from the face
                    for (float k = 0.6f; k <= 6f && !found; k += 0.3f)
                    {
                        if (!GroundBelow(hit.point + down * k, heroRoot, out var g) || g.normal.y < 0.8f) continue;
                        if (G3Water.TrySurface(g.point, out float sy, 5f) && sy > g.point.y) continue;
                        var head = g.point + Vector3.up * 1.6f;
                        if (Physics.OverlapCapsule(g.point + Vector3.up * 0.6f, head, 0.3f, ~0, QueryTriggerInteraction.Ignore).Any(col => !col.transform.IsChildOf(heroRoot))) continue;
                        // the slope must stand right behind the head
                        if (!Physics.Raycast(head, -down, out var back, far, ~0, QueryTriggerInteraction.Ignore) || back.normal.y > 0.8f || back.distance < near) continue;
                        found = true;
                        foot = g.point;
                        slope = hit.point;
                        behind = back.distance;
                        yaw = Mathf.Atan2(down.x, down.z) * Mathf.Rad2Deg;
                    }
                }
            }
            if (!found) { Fail($"slopeback: no {minDeg:0}-{maxDeg:0}° slope with flat ground at its foot within {radius:0} m"); yield break; }
            Hero.Teleport(foot + Vector3.up * 0.15f);
            Hero.SetLook(yaw, pitch);
            yield return new WaitForSeconds(0.5f);
            var cam = FindAnyObjectByType<G3PlayerCamera>();
            _slopeMinEye = float.MaxValue;
            _slopeFrames = _slopeHiddenFrames = _slopeFirstPersonFrames = 0;
            float until = Time.time + secs;
            while (Time.time < until)
            {
                Hero.SetLook(yaw, pitch);
                yield return null;
                if (cam == null) continue;
                _slopeFrames++;
                if (cam.EyeDistance >= 0f) _slopeMinEye = Mathf.Min(_slopeMinEye, cam.EyeDistance);
                if (cam.HeroHidden) _slopeHiddenFrames++;
                if (cam.IsFirstPersonView) _slopeFirstPersonFrames++;
            }
            if (_slopeMinEye == float.MaxValue) _slopeMinEye = -1f;
            Log($"slopeback: foot {V(foot)}, slope {V(slope)} {F(behind)} m behind the head, yaw {F(yaw)}, pitch {F(pitch)}: " +
                $"boom {F(cam != null ? cam.CurrentDistance : -1f)} m, eye min {F(_slopeMinEye)} m, hidden {_slopeHiddenFrames}/{_slopeFrames}, " +
                $"first-person view {_slopeFirstPersonFrames}/{_slopeFrames}");
        }

        /// <summary>The nearest ground hit straight below (x, z) from 1000 m, skipping the hero's own colliders.</summary>
        static bool GroundBelow(Vector3 p, Transform heroRoot, out RaycastHit best)
        {
            best = default;
            bool any = false;
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, 1000f, p.z), Vector3.down, 2000f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(heroRoot) && (!any || h.distance < best.distance)) { best = h; any = true; }
            return any;
        }

        static string F2(float v) => v.ToString("0.00", Inv);

        /// <summary>The player camera's distance from the hero's eyes now (m; "-" without one).</summary>
        string CamEyeProbe() => FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera c && c.EyeDistance >= 0f ? F2(c.EyeDistance) : "-";

        /// <summary>The boom length in use now (m).</summary>
        string CamDistProbe() => FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera c && c.CurrentDistance >= 0f ? F2(c.CurrentDistance) : "-";

        /// <summary>How the hero is drawn: full, firstperson (head shadows only) or hidden (all shadows only).</summary>
        string HeroViewProbe() => FindAnyObjectByType<G3PlayerCamera>() is G3PlayerCamera c
            ? c.HeroHidden ? "hidden" : c.IsFirstPersonView ? "firstperson" : "full" : "-";

        string SlopeProbe(string what) =>
            what == "eye" ? F2(_slopeMinEye) : what == "hidden" ? _slopeHiddenFrames.ToString() : _slopeFirstPersonFrames.ToString();

        /// <summary>The NPC's attitude toward a probe orc (Orc) and a probe rebel (Reb) without an enclave: "orc/reb".</summary>
        string PoliticsProbe(Myrtana.Core.ActorId who)
        {
            var a = _session.Crime.Attitudes;
            if (!a.Facts.TryGetProfile(who, out var self)) return "-";
            var orc = new Myrtana.Simulation.AttitudeProfile { Id = new Myrtana.Core.ActorId("Probe_Orc"), Species = "Orc", Humanoid = true, Alignment = "Orc" };
            var reb = new Myrtana.Simulation.AttitudeProfile { Id = new Myrtana.Core.ActorId("Probe_Reb"), Species = "Human", Humanoid = true, Alignment = "Reb" };
            return $"{a.Of(self, orc)}/{a.Of(self, reb)}";
        }

        /// <summary>Containers whose loot page title would be a raw id (an entity name or the localizer's "[FO_…]", fix-94).</summary>
        string RawLootTitles() =>
            _session.Content.Database.All<InteractiveObjectDef>()
                    .Select(o => HudFocus.LootTitle(_session, default, o.Id))
                    .Count(t => t.Contains("FO_") || t.Contains("G3_") || t.StartsWith("[")).ToString();
    }
}
#endif
