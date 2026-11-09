#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Core;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-108 (Docs/dev-scenarios.md): NPC bodies stand on the floor on steep slopes (NpcGround / G3.Player.NpcGroundRules):
    /// the gap between the feet and the collision floor under the body's origin, idle and walking, and the slide off faces
    /// steeper than 49°.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static readonly System.Globalization.CultureInfo Inv108 = System.Globalization.CultureInfo.InvariantCulture;
        static readonly RaycastHit[] _gapHits = new RaycastHit[16];

        /// <summary>
        /// Feet height over the collision surface nearest them in the vertical column under the body (m; negative: under
        /// it), measured with its own ray (not the code under test); <paramref name="slope"/> = that face's slope (deg).
        /// NaN without a body or a surface within 3 m.
        /// </summary>
        float NpcFootGap(ActorId id, out float slope)
        {
            slope = float.NaN;
            if (id.IsPlayer || !TryActorPosition(id, out var p, out var view) || view == null) return float.NaN;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 3f, Vector3.down, _gapHits, 6f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.NaN;
            for (int i = 0; i < n; i++)
            {
                var h = _gapHits[i];
                if (h.collider is CharacterController || h.rigidbody != null || h.transform.IsChildOf(view)) continue;
                float gap = p.y - h.point.y;
                if (float.IsNaN(best) || Mathf.Abs(gap) < Mathf.Abs(best))
                {
                    best = gap;
                    slope = Vector3.Angle(h.normal, Vector3.up);
                }
            }
            return best;
        }

        /// <summary>
        /// 'slopeground npc at=x,y,z [idle=4] [walkto=x,y,z] [secs=10] [speed=1.6] [max=0.05] [slide=0|1]': the NPC is put at
        /// <c>at</c> (its routine continues: idle bodies are shown by the routine's own floor probe) and watched for idle
        /// seconds, then (walkto=) walks there on its walker; every frame the foot gap (<see cref="NpcFootGap"/>) is sampled.
        /// Passes when the largest |gap| stays ≤ max. slide=1 (a spot on a face steeper than 49°): passes when the body
        /// slid off it — it ends lower, on floor of at most 49°+5° — and the gap is within max at the end.
        /// </summary>
        System.Collections.IEnumerator SlopeGroundStep(ScenarioStep st)
        {
            var who = Actor(st.Get("npc"));
            if (who.IsPlayer || !TryActorPosition(who, out _, out var viewT) || viewT == null || !viewT.TryGetComponent<NpcView>(out var view))
            {
                Fail($"slopeground: no NPC view '{st.Get("npc")}'");
                yield break;
            }
            if (!TryTarget(st.Get("at"), out var at, out bool hasY, out _, out _, out string err)) { Fail("slopeground: at= " + err); yield break; }
            if (!hasY) at.y = NpcWalker.FloorHeight(at, Hero.transform.position.y);
            float idle = st.Float("idle", 4f), max = st.Float("max", 0.05f);
            bool slide = st.Bool("slide", false);
            // put the body there and let the simulation keep it (the routine shows the reported pose)
            view.DevWalk(at, 0.1f, at, viewT.rotation);
            view.DevWalkStop();
            float worstIdle = 0f, t0 = Time.time;
            int samples = 0;
            yield return null;
            while (Time.time - t0 < idle)
            {
                float g = NpcFootGap(who, out _);
                if (!float.IsNaN(g) && Time.time - t0 > 0.5f) { worstIdle = Mathf.Max(worstIdle, Mathf.Abs(g)); samples++; }
                yield return null;
            }
            float endGap = NpcFootGap(who, out float endSlope);
            var endPos = viewT.position;
            string idleText = $"idle {F(idle)} s: worst gap {worstIdle.ToString("0.000", Inv108)} m ({samples} frames), end gap {endGap.ToString("0.000", Inv108)} m " +
                              $"on {endSlope.ToString("0.0", Inv108)}° at {V(endPos)}, slide {view.SlideSpeed.ToString("0.0", Inv108)} m/s";
            if (slide)
            {
                bool ok = endPos.y < at.y - 0.3f && endSlope <= G3.Player.SlopeRules.WalkableAngleMax + 5f && Mathf.Abs(endGap) <= max;
                if (ok) Log($"slopeground {who}: slid off the face from {V(at)}; {idleText}");
                else Fail($"slopeground {who}: expected a slide off the face at {V(at)} onto walkable floor; {idleText}");
                yield break;
            }
            float worstWalk = 0f;
            string walkText = "";
            if (st.Has("walkto"))
            {
                if (!TryTarget(st.Get("walkto"), out var to, out bool toY, out _, out _, out err)) { Fail("slopeground: walkto= " + err); yield break; }
                if (!toY) to.y = NpcWalker.FloorHeight(to, at.y);
                float secs = st.Float("secs", 10f);
                view.DevWalk(to, st.Float("speed", 1.6f));
                float t1 = Time.time;
                int frames = 0;
                while (Time.time - t1 < secs && view.DevWalking)
                {
                    yield return null;
                    float g = NpcFootGap(who, out _);
                    if (!float.IsNaN(g)) { worstWalk = Mathf.Max(worstWalk, Mathf.Abs(g)); frames++; }
                }
                string end = view.DevWalking ? "timeout" : view.DevWalkResult.ToString();
                view.DevWalkStop();
                walkText = $"; walk to {V(to)}: {end}, worst gap {worstWalk.ToString("0.000", Inv108)} m ({frames} frames), end {V(viewT.position)}";
            }
            string summary = $"slopeground {who} at {V(at)}: {idleText}{walkText}";
            if (worstIdle <= max && worstWalk <= max) Log(summary);
            else Fail(summary + $" (expected |gap| <= {F(max)} m)");
        }
    }
}
#endif
