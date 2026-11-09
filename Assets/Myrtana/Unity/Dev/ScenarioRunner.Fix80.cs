#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-80 conditions (Docs/dev-scenarios.md): corpses resting on the ground (<see cref="CorpseRest"/>) and signpost
    /// focus names (<see cref="InteractiveKind.Sign"/>).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        /// <summary>
        /// 'signpost Sign_Faring [dist=1.8] [side=back]': the hero stands in front of the nearest board of that name (along its
        /// face normal, on the ground below) and faces it, so the focus picks it.
        /// </summary>
        System.Collections.IEnumerator SignpostStep(ScenarioStep st)
        {
            string name = st.Get("name");
            if (!TryObject(name, out var def) || def.Kind != InteractiveKind.Sign) { Fail($"signpost: no signpost '{name}'"); yield break; }
            var board = new Vector3(def.Position[0], def.Position[1], def.Position[2]);
            var normal = Vector3.ProjectOnPlane(new Vector3(def.Forward[0], def.Forward[1], def.Forward[2]), Vector3.up);
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.forward;
            normal.Normalize();
            if (string.Equals(st.Get("side"), "back", System.StringComparison.OrdinalIgnoreCase)) normal = -normal;
            var stand = board + normal * st.Float("dist", 1.8f);
            yield return TeleportTo(stand, true, false, null, board);
            Log($"signpost {name}: board {V(board)}, hero {V(Hero.transform.position)}");
        }

        static readonly System.Globalization.CultureInfo Inv80 = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>True for the condition kinds <see cref="Fix80Probe"/> answers.</summary>
        static bool IsFix80Probe(string kind) =>
            kind == "corpse" || kind == "corpsegap" || kind == "corpsemove" || kind == "corpsepelvis" || kind == "corpseanchor" || kind == "dying" ||
            kind == "corpselegs" || kind == "corpsepose" || kind == "corpsefooting" ||
            kind == "signfocus" || kind == "signtext" || kind == "signresolved" || kind == "signanchor";

        /// <summary>fix-232: the up normal of the ground under a body (its CorpseRest survey), else world up.</summary>
        static Vector3 CorpseGroundUp(Transform view) =>
            view != null && view.TryGetComponent<CorpseRest>(out var rest) && rest.GroundNormal(out var up) ? up : Vector3.up;

        string Fix80Probe(string kind, ActorId who)
        {
            switch (kind)
            {
                case "corpse":
                {
                    // how the body came to rest: "ragdoll: …", "pose: tilt …", "restored pose: …"; "falling" before; "-" alive/no view
                    if (!TryActorPosition(who, out _, out var view) || view == null || !view.TryGetComponent<CorpseRest>(out var rest) || !rest.enabled && !rest.Rested) return "-";
                    return rest.Rested ? rest.Info : "falling";
                }
                case "corpsemove":
                {
                    // fix-163: |vertical move| of the body's last settling (a restored corpse: how far its saved origin was off)
                    if (!TryActorPosition(who, out _, out var view) || view == null || !view.TryGetComponent<CorpseRest>(out var rest) || !rest.Rested) return "-";
                    return Mathf.Abs(rest.LastOffset).ToString("0.000", Inv80);
                }
                case "corpsegap":
                case "corpsepelvis":
                case "corpseanchor":
                {
                    if (!TryActorPosition(who, out _, out var view) || view == null) return "-";
                    var rest = view.TryGetComponent<CorpseRest>(out var r) ? r : null;
                    if (rest == null || !rest.Measure(out float gap, out float pelvis, out float offset)) return "-";
                    float value = kind == "corpsegap" ? gap : kind == "corpsepelvis" ? pelvis : offset;
                    return float.IsNaN(value) ? "-" : value.ToString("0.000", Inv80);
                }
                case "corpselegs":
                {
                    // fix-180: the most upright leg's angle from straight down, degrees (a standing body ~0-25, a lying one 45-90);
                    // fix-232: "down" is into the ground under the body (a wolf lying on a 35° slope, legs downhill: 27° from world down)
                    if (!TryActorPosition(who, out _, out var view) || view == null) return "-";
                    float legs = CorpseRest.UprightLegAngle(view.transform, -CorpseGroundUp(view));
                    return float.IsNaN(legs) ? "-" : legs.ToString("0.0", Inv80);
                }
                case "corpsefooting":
                {
                    // fix-180: the relief footing must not move a dead body: its applied offset (m) and pitch, "-" without one
                    if (!TryActorPosition(who, out _, out var view) || view == null || !view.TryGetComponent<global::G3.Unity.G3ReliefFooting>(out var foot)) return "-";
                    return foot.isActiveAndEnabled ? foot.Diagnostics : "off";
                }
                case "corpsepose":
                {
                    // fix-180 summary for logs: shown motion, dead/lies flags, Animator on, pelvis/legs/tilt, rest info
                    if (!TryActorPosition(who, out _, out var view) || view == null) return "-";
                    var a = view.GetComponent<G3CombatAnimator>();
                    var player = view.GetComponent<G3MotionPlayer>();
                    var anim = view.GetComponent<Animator>();
                    float pelvis = float.NaN;
                    if (view.TryGetComponent<CorpseRest>(out var rest)) rest.Measure(out _, out pelvis, out _);
                    return $"key {player?.CurrentKey ?? "-"} dead {a != null && a.IsDead} lies {a != null && a.LiesDead} shown {a != null && a.DeadPoseShown} " +
                           $"anim {(anim != null && anim.enabled)} pelvis {pelvis.ToString("0.00", Inv80)} legs {CorpseRest.UprightLegAngle(view.transform, -CorpseGroundUp(view)).ToString("0", Inv80)} " +
                           $"(world {CorpseRest.UprightLegAngle(view.transform).ToString("0", Inv80)}, ground {Vector3.Angle(CorpseGroundUp(view), Vector3.up).ToString("0", Inv80)}°) " +
                           $"tilt {Vector3.Angle(view.transform.up, Vector3.up).ToString("0", Inv80)} rest {(rest != null && rest.Rested ? rest.Info : "-")}";
                }
                case "dying":
                {
                    // a dying motion plays (dead, not yet in the held LieDead pose): never right after a load (fix-80 #4)
                    var a = TryActorPosition(who, out _, out var view) && view != null ? view.GetComponent<G3CombatAnimator>() : null;
                    return (a != null && a.IsDead && !a.LiesDead).ToString();
                }
                case "signfocus":
                {
                    var pi = FindAnyObjectByType<PlayerInteraction>();
                    return pi != null && pi.FocusObject != null && _session.Interaction.Objects.TryGet(pi.FocusObject, out var def) ? def.Name : "-";
                }
                case "signtext":
                {
                    var pi = FindAnyObjectByType<PlayerInteraction>();
                    return pi != null && pi.FocusObject != null && _session.Interaction.Objects.TryGet(pi.FocusObject, out var def)
                        ? _session.Localizer.Text(def.FocusName) : "-";
                }
                case "signresolved":
                {
                    // the focus name is a real text of the current language, not the FO_ key itself
                    var pi = FindAnyObjectByType<PlayerInteraction>();
                    if (pi == null || pi.FocusObject == null || !_session.Interaction.Objects.TryGet(pi.FocusObject, out var def)) return "False";
                    string text = _session.Localizer.Text(def.FocusName);
                    return (!string.IsNullOrEmpty(text) && !text.StartsWith("FO_", System.StringComparison.Ordinal)).ToString();
                }
                case "signanchor":
                {
                    // height of the focus name's point above the focused board's origin, m (G3: the board's bounds height)
                    var pi = FindAnyObjectByType<PlayerInteraction>();
                    var views = FindAnyObjectByType<InteractiveObjectViews>();
                    if (pi == null || pi.FocusObject == null || views == null || !views.TryGet(pi.FocusObject, out var v) ||
                        !v.TryGetNameAnchor(out var anchor)) return "-";
                    return (anchor.y - v.transform.position.y).ToString("0.000", Inv80);
                }
                default: return "-";
            }
        }
    }
}
#endif
