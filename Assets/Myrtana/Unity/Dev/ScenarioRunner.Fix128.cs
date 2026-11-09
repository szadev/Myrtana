#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Core;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-128 (Docs/dev-scenarios.md): inside caves NPC bodies stand only on true cave floors (CaveGround /
    /// G3.Player.CaveFloorRules): op <c>caveplace</c> puts an NPC somewhere (in the rock behind a cave wall, on a roof
    /// top) and waits for the cave guard; probes <c>npccave:&lt;npc&gt;</c> (the judge of its place), <c>cavestand:x,y,z</c>,
    /// <c>cavesnaps</c>, <c>caverooftris</c> (roof-top triangles left out of the NavMesh), <c>npcy:&lt;npc&gt;</c>.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static readonly System.Globalization.CultureInfo Inv128 = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary><c>caveplace npc at=x,y,z [wait=4]</c>: the NPC's body put at the point as is (no ground snap), then wait.</summary>
        System.Collections.IEnumerator CavePlaceStep(ScenarioStep st)
        {
            var who = Actor(st.Get("npc"));
            if (who.IsPlayer || !TryActorPosition(who, out _, out var viewT) || viewT == null || !viewT.TryGetComponent<NpcView>(out var view))
            {
                Fail($"caveplace: no NPC view '{st.Get("npc")}'");
                yield break;
            }
            if (!TryTarget(st.Get("at"), out var at, out bool hasY, out _, out _, out string err) || !hasY) { Fail("caveplace: at=x,y,z " + err); yield break; }
            int snaps = G3.Player.CaveFloorRules.MayStand(CaveGround.Judge(at, out _)) ? -1 : CaveGround.Snaps;
            view.DevWalk(at, 0.1f, at, viewT.rotation);
            view.DevWalkStop();
            viewT.position = at; // exactly there, also inside the rock
            Physics.SyncTransforms();
            float wait = st.Float("wait", 4f), t0 = Time.time;
            while (Time.time - t0 < wait) yield return null;
            var end = viewT.position;
            Log($"caveplace {who}: put at {V(at)} ({CaveGround.Judge(at, out _)}), after {F(wait)} s at {V(end)} ({CaveGround.Judge(end, out _)}), " +
                $"cave snaps {(snaps < 0 ? "-" : (CaveGround.Snaps - snaps).ToString(Inv128))}");
        }

        string CaveProbe(string kind, ActorId who, string arg)
        {
            switch (kind)
            {
                case "cavesnaps": return CaveGround.Snaps.ToString(Inv128);
                case "caverooftris": return CellNavMeshes.CaveRoofTris.ToString(Inv128);
                case "cavestand":
                {
                    var v = (arg ?? "").Split(',');
                    if (v.Length < 3 || !float.TryParse(v[0], System.Globalization.NumberStyles.Float, Inv128, out float x) ||
                        !float.TryParse(v[1], System.Globalization.NumberStyles.Float, Inv128, out float y) ||
                        !float.TryParse(v[2], System.Globalization.NumberStyles.Float, Inv128, out float z)) return "bad point";
                    return CaveGround.Judge(new Vector3(x, y, z), out _).ToString();
                }
                case "npccave":
                    if (who.IsPlayer || !TryActorPosition(who, out var p, out var t) || t == null) return "none";
                    return CaveGround.Judge(t.position, out _).ToString();
                case "npcy":
                    if (who.IsPlayer || !TryActorPosition(who, out _, out var tt) || tt == null) return "NaN";
                    return tt.position.y.ToString("0.00", Inv128);
            }
            return null;
        }
    }
}
#endif
