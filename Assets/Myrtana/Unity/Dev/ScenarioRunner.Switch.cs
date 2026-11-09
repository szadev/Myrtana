#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Globalization;
using G3.Player;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-101 (owner: "switching from the bow to the sword reacts slowly"): <c>switchtrace</c> presses a weapon's
    /// quick-slot key (<see cref="PlayerCombat.TryReady"/>, the real key path) at a chosen moment and times the switch
    /// frame by frame against G3 (<see cref="WeaponSwitchTimeline"/>).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        /// <summary>
        /// <c>switchtrace item=X [during=idle|reload|aim|attack] [max=s] [tol=0.1]</c>: during=reload shoots first and presses
        /// in the reload, aim presses with the aim held (PS_Ranged_Aim_Loop), attack presses during a melee attack. Logs
        /// press → taken → old weapon off the hand → put away → draw → new weapon in the hand → ready (s), the frames between
        /// the put-away's end and the draw's start, and G3's numbers. FAIL: not ready within max (default 4 s); the key not
        /// taken in the first frames the body is free again (G3's one-slot queue: the reload's, the move's end); a frame or
        /// more between put-away and draw (G3: same tick); taken → ready longer than G3's pair lengths + tol.
        /// </summary>
        IEnumerator SwitchTraceStep(ScenarioStep st)
        {
            var a = Combat.Fighter.Animator;
            string item = ResolveItem(st.Get("item"));
            if (item == null) { Fail("switchtrace: no item " + st.Get("item")); yield break; }
            if (_session.Items.Inventories.Of(ActorId.Player).Count(item) == 0) _session.Items.Inventories.Give(ActorId.Player, item, 1);
            string during = st.Get("during", "idle").ToLowerInvariant();
            float max = st.Float("max", 4f), tol = st.Float("tol", 0.1f);
            HideTips();
            float w0 = Time.time;
            while ((a.Busy || a.InReload || a.InShot) && Time.time - w0 < 4f) yield return null; // settled first
            string from = a.Drawn && a.WeaponInHand ? HandOf(a.Weapon) : "none";

            // ---- the moment of the press
            bool releaseAfter = false;
            float stateEnd = -1f; // when the body left the state the key had to wait for (game time)
            switch (during)
            {
                case "draw":
                {
                    // the owner's case: the key while the string is being drawn (PS_Ranged_PowerAim), the button kept held
                    if (!Combat.Fighter.RangedDrawn) { Fail($"switchtrace during=draw: no bow or crossbow drawn ({a.StateText})"); yield break; }
                    Combat.PrimaryDown();
                    float t0 = Time.time;
                    while (!(a.InAim && a.DrawSeconds > 0.5f) && Time.time - t0 < 3f) yield return null;
                    if (!a.InAim || a.AimHeld) { Combat.PrimaryUp(); Fail($"switchtrace: not drawing the string ({a.StateText})"); yield break; }
                    releaseAfter = true;
                    break;
                }
                case "reload":
                case "aim":
                {
                    if (!Combat.Fighter.RangedDrawn) { Fail($"switchtrace during={during}: no bow or crossbow drawn ({a.StateText})"); yield break; }
                    Combat.PrimaryDown();
                    float t0 = Time.time;
                    while (!a.AimHeld && Time.time - t0 < 4f) yield return null;
                    if (!a.AimHeld) { Combat.PrimaryUp(); Fail($"switchtrace: the aim was not raised ({a.StateText})"); yield break; }
                    if (during == "aim")
                    {
                        releaseAfter = true;
                        break;
                    }
                    Combat.PrimaryUp(); // the shot, then Aim_Recover and the reload
                    t0 = Time.time;
                    while (!a.InReload && Time.time - t0 < 2f) yield return null;
                    if (!a.InReload) { Fail($"switchtrace: no reload after the shot ({a.StateText})"); yield break; }
                    yield return new WaitForSeconds(0.1f); // well inside the reload
                    if (!a.InReload) { Fail($"switchtrace: the reload was over before the press ({a.StateText})"); yield break; }
                    break;
                }
                case "attack":
                {
                    if (!a.Drawn || Combat.Fighter.RangedDrawn || Combat.Fighter.SpellDrawn) { Fail($"switchtrace during=attack: no melee weapon drawn ({a.StateText})"); yield break; }
                    Combat.PrimaryDown();
                    yield return null;
                    Combat.PrimaryUp(); // a tap: Attack
                    float t0 = Time.time;
                    while (!a.InMove && Time.time - t0 < 1f) yield return null;
                    if (!a.InMove) { Fail($"switchtrace: no attack started ({a.StateText})"); yield break; }
                    break;
                }
            }

            // ---- press and trace
            int sheatheEndFrame = -1, drawStartFrame = -1;
            float tSheatheEnd = -1f, tDrawStart = -1f;
            bool sheatheRan = false; // fix-105: a put-away was seen (the draw may start inside its end event)
            void OnEnded(string action)
            {
                if (action == "Sheathe" && sheatheEndFrame < 0) { sheatheEndFrame = Time.frameCount; tSheatheEnd = Time.time; }
            }
            void OnStep(string key, G3SwingPhase phase, float fade)
            {
                // fix-105 (r40 '18 frames between the put-away's end and the draw'): PlayerCombat hears the sheathe's end first
                // (it subscribed earlier) and starts the draw inside that event, before OnEnded above runs, so the draw's first
                // motion came before the recorded end and only its second (the hand change, 0.2 s on) was taken as "the draw"
                if (a.InDraw && drawStartFrame < 0 && (sheatheEndFrame >= 0 || sheatheRan)) { drawStartFrame = Time.frameCount; tDrawStart = Time.time; }
                else if (a.Action == "Sheathe") sheatheRan = true;
            }
            a.MoveEnded += OnEnded;
            a.StepStarted += OnStep;
            float press = Time.time;
            var frameTimes = new System.Collections.Generic.List<float>(); // fix-105: the frames of the trace (s)
            bool taken = Combat.TryReady(item);
            string waitingAtPress = Combat.WaitingWeaponKey;
            float tAccepted = -1f, tOff = -1f, tIn = -1f, tReady = -1f;
            bool wasInHand = a.WeaponInHand, waitState = waitingAtPress != null;
            bool ok = true;
            while (Time.time - press <= max)
            {
                if (waitState && stateEnd < 0f && !(a.Busy || a.InReload || a.InShot || (a.InAim && !a.AimHeld)))
                    stateEnd = Time.time;
                if (tAccepted < 0f && (a.InDraw || a.Action == "Sheathe" || !a.Drawn && from != "none")) tAccepted = Time.time;
                if (a.Action == "Sheathe") sheatheRan = true;
                if (wasInHand && !a.WeaponInHand && tOff < 0f) tOff = Time.time;
                if (!wasInHand && a.WeaponInHand && tIn < 0f) tIn = Time.time;
                wasInHand = a.WeaponInHand;
                if (tAccepted >= 0f && a.Drawn && a.WeaponInHand && a.Hand.Settled && !a.Busy && Combat.WaitingWeaponKey == null &&
                    (from == "none" || tSheatheEnd >= 0f || HandOf(a.Weapon) != from))
                {
                    tReady = Time.time;
                    break;
                }
                yield return null;
                frameTimes.Add(Time.deltaTime);
            }
            a.MoveEnded -= OnEnded;
            a.StepStarted -= OnStep;
            if (releaseAfter) Combat.PrimaryUp();
            if (Combat.GameOverShown || _session.Combat.Vitals.StateOf(ActorId.Player) != VitalState.Alive)
            {
                Fail($"switchtrace: the hero is {_session.Combat.Vitals.StateOf(ActorId.Player)}{(Combat.GameOverShown ? ", game over shown" : "")}");
                yield break;
            }

            string to = HandOf(a.Weapon);
            string R(float t) => t < 0f ? "-" : (t - press).ToString("0.000", CultureInfo.InvariantCulture);
            int joinFrames = sheatheEndFrame >= 0 && drawStartFrame >= 0 ? Mathf.Max(0, drawStartFrame - sheatheEndFrame) : -1;
            Log($"switchtrace {from} -> {to} ({item}, during {during}): key {(taken ? waitingAtPress != null ? "waits" : "acts" : "REFUSED")}, " +
                $"taken {R(tAccepted)}, old off the hand {R(tOff)}, put away {R(tSheatheEnd)}, draw {R(tDrawStart)}, new in the hand {R(tIn)}, " +
                $"ready {R(tReady)} s; join {joinFrames} frame(s); state over {R(stateEnd)}");
            if (tReady < 0f)
            {
                Fail($"switchtrace {from} -> {item}: not ready {F(max)} s after the key ({a.StateText}, waiting {Combat.WaitingWeaponKey ?? "-"})");
                yield break;
            }
            var g3 = WeaponSwitchTimeline.G3(from, to);
            float ours = tReady - tAccepted;
            Log($"switchtrace {from} -> {to}: ours taken → ready {ours:0.000} s, G3 {g3.Ready:0.000} s ({g3})");
            if (joinFrames > 0) { Fail($"switchtrace: {joinFrames} frame(s) between the put-away's end and the draw (G3: the same tick)"); ok = false; }
            // fix-105 (r40/r41: 1.553, 1.621, 1.678 s for the same bow -> 1H switch; the slow one had a 0.34 s first draw motion,
            // G3 0.20, on the first sword draw after Play): each of the four motions of a switch is seen to end in the first frame
            // after its end, so every boundary can add up to one frame over G3's 60 Hz tick — a long frame (a hitch, the
            // unfocused editor's 14 fps) adds its length. Allowed: tol + the four longest frames of the trace over 1/60 s.
            frameTimes.Sort();
            float slack = 0f;
            for (int i = frameTimes.Count - 1; i >= 0 && i >= frameTimes.Count - 4; i--) slack += Mathf.Max(0f, frameTimes[i] - WeaponSwitchTimeline.Frame60);
            float allowed = tol + slack;
            if (ours > g3.Ready + allowed)
            {
                Fail($"switchtrace {from} -> {to}: {ours:0.000} s from taken to ready, G3 {g3.Ready:0.000} s (+{allowed:0.000}: {tol:0.00} + " +
                     $"{slack:0.000} for the 4 longest frames, longest {(frameTimes.Count > 0 ? frameTimes[frameTimes.Count - 1] * 1000f : 0f):0} ms)");
                ok = false;
            }
            if (waitState && stateEnd >= 0f && tAccepted - stateEnd > 3f * Mathf.Max(Time.deltaTime, WeaponSwitchTimeline.Frame60))
            {
                Fail($"switchtrace: the waiting key acted {tAccepted - stateEnd:0.000} s after the body was free (G3: its next loop state)");
                ok = false;
            }
            if (ok) Log($"switchtrace {from} -> {to}: matches G3");
        }

        /// <summary>The <see cref="WeaponSwitchTimeline"/> hand name of an animator weapon field.</summary>
        static string HandOf(string field)
        {
            switch ((field ?? "").ToLowerInvariant())
            {
                case "1h": return "1h";
                case "2h": return "2h";
                case "staff": return "staff";
                case "bow": return "bow";
                case "crossbow": return "crossbow";
                case "cast": return "spell";
                default: return "fist";
            }
        }
    }
}
#endif
