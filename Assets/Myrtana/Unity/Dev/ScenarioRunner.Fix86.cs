#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Core;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-86 (Docs/dev-scenarios.md): standing characters stop the hero's walk (G3 character controllers collide;
    /// <see cref="G3.Player.BodyBlockRules"/>, <see cref="CombatBody.WalkBlock"/>).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static readonly System.Globalization.CultureInfo Inv86 = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// 'bump npc [secs=3] [expect=blocked|pass] [tol=0.08] [walk=1]': the hero runs at the NPC's current position every
        /// frame. blocked: passes when he never came closer than contact (his radius + the body's footprint) − tol and the
        /// NPC's body stopped him; pass: passes when he got within 0.4 m (walked over a lying or dead body).
        /// </summary>
        System.Collections.IEnumerator BumpStep(ScenarioStep st)
        {
            string text = st.Get("npc");
            var who = Actor(text);
            if (who.IsPlayer || !TryActorPosition(who, out _, out _)) { Fail($"bump: no NPC '{text}'"); yield break; }
            var fighter = FighterOf(who);
            float own = Hero.TryGetComponent<CharacterController>(out var cc) ? cc.radius : 0.35f;
            float foot = fighter != null && fighter.Body != null ? fighter.Body.Footprint : 0f;
            float contact = own + foot, tol = st.Float("tol", 0.08f), secs = st.Float("secs", 3f);
            bool expectPass = string.Equals(st.Get("expect"), "pass", System.StringComparison.OrdinalIgnoreCase);
            int blocks0 = Hero.BodyBlocks;
            float min = float.MaxValue, t0 = Time.time;
            bool byTarget = false;
            Hero.scriptedWalk = st.Bool("walk", false);
            int talks = 0;
            while (Time.time - t0 < secs)
            {
                // fix-97 (r39 9/50: 'closest 3.4e38 m, end 4.00 m, a dialog opened'): an NPC with an Important info starts
                // talking to the hero within 5 m by itself (G3 AssessActivePerception -> AssessTalk; Hamlar at the Ardea start,
                // timing-dependent: r38 caught it before a step). A player answers and goes on; the talk is not bump time.
                if (_session.Dialog.IsActive)
                {
                    if (talks++ >= 3) break;
                    Hero.scriptedMove = Vector2.zero;
                    float talk = 0f;
                    yield return AnswerUnaskedTalk("bump " + text, x => talk = x);
                    t0 += talk;
                    continue;
                }
                if (!TryActorPosition(who, out var at, out _)) break;
                var to = at - Hero.transform.position;
                to.y = 0f;
                // fix-105 (r40: wolf 'closest 0.90 m, contact 1.06 m'): the body blocks round its capsule's centre, which lies
                // 0.16 m behind a wolf's root (a long animal's capsule, G3Player.ClipAtBodies); the contact is measured there
                var c = fighter != null && fighter.Body != null && fighter.Body.WalkBlock > 0f ? fighter.Body.Center : at;
                var toBody = c - Hero.transform.position;
                toBody.y = 0f;
                min = Mathf.Min(min, expectPass ? to.magnitude : toBody.magnitude);
                if (Hero.LastBlockingBody != null && fighter != null && fighter.Body != null &&
                    Hero.LastBlockingBody.transform == fighter.Body.transform && Hero.BodyBlocks > blocks0) byTarget = true;
                if (to.magnitude < 0.15f) break; // on top of it: walked through or over
                Hero.SetLook(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 5f);
                Hero.scriptedMove = new Vector2(0, 1);
                yield return null;
            }
            Hero.scriptedMove = Vector2.zero;
            Hero.scriptedWalk = false;
            TryActorPosition(who, out var end, out _);
            var left = end - Hero.transform.position;
            left.y = 0f;
            string summary = $"bump {text}: closest {min.ToString("0.00", Inv86)} m{(expectPass ? "" : " (to the body's centre)")}, end {left.magnitude.ToString("0.00", Inv86)} m, " +
                             $"contact {contact.ToString("0.00", Inv86)} m (hero {own.ToString("0.00", Inv86)} + body {foot.ToString("0.00", Inv86)}), " +
                             $"blocks {Hero.BodyBlocks - blocks0}, stopped by it {byTarget}, " +
                             $"walk block {(fighter != null && fighter.Body != null ? fighter.Body.WalkBlock.ToString("0.00", Inv86) : "-")}" +
                             (_session.Dialog.IsActive ? ", a dialog opened" : "");
            bool ok = expectPass ? min < 0.4f : byTarget && min >= contact - tol;
            if (ok) Log(summary + (expectPass ? " (walked over as expected)" : " (blocked as expected)"));
            else Fail(summary + (expectPass ? " (expected: walks over it)" : $" (expected: stopped at contact − {tol.ToString("0.00", Inv86)})"));
        }

        static bool IsFix86Probe(string kind) => kind == "flatdist" || kind == "bodyblocks" || kind == "blockedby";

        string Fix86Probe(string kind, ActorId who)
        {
            switch (kind)
            {
                case "flatdist":
                {
                    if (!TryActorPosition(who, out var p, out _)) return "-";
                    var d = p - Hero.transform.position;
                    d.y = 0f;
                    return d.magnitude.ToString("0.00", Inv86);
                }
                case "bodyblocks": return Hero.BodyBlocks.ToString(Inv86);
                case "blockedby":
                {
                    var c = Hero.LastBlockingBody;
                    var body = c != null ? c.GetComponent<CombatBody>() : null;
                    return body != null && body.Fighter != null ? body.Fighter.Actor.ToString() : "-";
                }
                default: return "-";
            }
        }
    }
}
#endif
