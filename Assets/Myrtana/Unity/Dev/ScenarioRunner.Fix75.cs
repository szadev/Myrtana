#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Globalization;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-75 ops: dropped items lie flat (<c>dropprobe</c>), the inventory's digit keys put the selected / hovered stack on
    /// the quick bar (<c>invquick</c>), the block's latency during an attack (<c>blocklag</c>), the secondary button ends an
    /// object use or its approach (<c>cancel</c>) and the approach walk itself (<c>useprobe</c>).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        /// <summary>
        /// `dropprobe [min=1] [tilt=20]` — every item dropped during the game that is shown near the hero: the angle of its
        /// longest mesh axis to the ground plane (lying: ~0), the angle of its thinnest axis to the ground normal, and the
        /// gap between its lowest point and the ground under it. Fails when fewer than `min` are shown, one stands (tilt
        /// over `tilt` degrees), sinks in (gap &lt; -3 cm) or floats (gap &gt; 10 cm).
        /// </summary>
        void DropProbeStep(ScenarioStep st)
        {
            var views = FindAnyObjectByType<WorldItemViews>();
            if (views == null) { Fail("dropprobe: no world item views"); return; }
            int count = 0, bad = 0;
            float maxTilt = st.Float("tilt", 20f);
            foreach (var kv in views.ShownDropped())
            {
                var go = kv.Value;
                if (!go.TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null || !go.TryGetComponent<MeshRenderer>(out var mr)) continue;
                count++;
                var size = mf.sharedMesh.bounds.size;
                ItemRestPose.Axes(size.x, size.y, size.z, out int thin, out int length);
                var t = go.transform;
                var along = t.TransformDirection(Axis(length));
                var up = t.TransformDirection(Axis(thin));
                WorldItemRest.TryGround(mr.bounds.center, out var ground, out var normal);
                float tilt = 90f - Vector3.Angle(along, normal);
                float upAngle = Mathf.Min(Vector3.Angle(up, normal), Vector3.Angle(-up, normal));
                float gap = mr.bounds.min.y - ground.y;
                bool ok = Mathf.Abs(tilt) <= maxTilt && gap >= -0.03f && gap <= 0.10f;
                if (!ok) bad++;
                Log(string.Format(CultureInfo.InvariantCulture, "dropprobe {0} ({1}): long axis {2:0.0}° to the ground, up axis {3:0.0}° off the normal, gap {4:0.000} m, size {5:0.00}x{6:0.00}x{7:0.00} -> {8}",
                                  go.name, kv.Key, tilt, upAngle, gap, size.x, size.y, size.z, ok ? "lies" : "BAD"));
            }
            if (count < st.Int("min", 1)) Fail($"dropprobe: {count} dropped item view(s) shown, expected >= {st.Int("min", 1)}");
            else if (bad > 0) Fail($"dropprobe: {bad} of {count} dropped item(s) do not lie flat on the ground");
            else Result($"dropprobe: {count} dropped item(s) lie flat");
        }

        static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        /// <summary>
        /// `invquick item=X digit=N [hover=1] [expect=assigned|refused] [close=1]` — opens the inventory (I), selects the
        /// stack (hover=1: as the mouse moving onto it), checks that the item panel shows it, presses the digit key's path
        /// (G3 OnPlayerAssignStackKey) and checks the quick slot. The item is given first when the hero lacks it.
        /// </summary>
        IEnumerator InvQuickStep(ScenarioStep st)
        {
            var screens = FindAnyObjectByType<GameScreens>();
            var page = screens != null ? screens.ByAction(G3.Player.SessionKey.InventoryMode) as G3InventoryScreen : null;
            if (page == null) { Fail("invquick: no G3 inventory page (G3 GUI not imported?)"); yield break; }
            string item = ResolveItem(st.Get("item"));
            if (item == null) yield break;
            var inv = _session.Items.Inventories.Of(ActorId.Player);
            if (inv.Count(item) == 0) _session.Items.Inventories.Give(ActorId.Player, item, 1);
            HideTips();
            if (screens.Current != page) screens.Toggle(G3.Player.SessionKey.InventoryMode);
            yield return new WaitForSecondsRealtime(0.4f);
            if (screens.Current != page) { Fail("invquick: the inventory did not open"); yield break; }
            if (!page.ShowItem(item)) { Fail($"invquick: {item} is on no inventory tab"); yield break; }
            bool hover = st.Bool("hover", false);
            if (!(hover ? page.Hover(item) : page.SelectItem(item))) { Fail($"invquick: could not {(hover ? "hover" : "select")} {item}"); yield break; }
            yield return null;
            if (page.PanelItem != item) { Fail($"invquick: the item panel shows {page.PanelItem ?? "-"}, not the {(hover ? "hovered" : "selected")} {item}"); yield break; }
            int digit = st.Int("digit", 1);
            int slot = QuickSlots.SlotOfDigit(digit);
            string before = _session.Items.QuickSlots.ItemAt(slot);
            int oldSlot = _session.Items.QuickSlots.SlotOf(item);
            bool assigned = page.AssignQuickSlot(slot);
            yield return null;
            string now = _session.Items.QuickSlots.ItemAt(slot);
            Log($"invquick: key {digit} on {item} ({(hover ? "hovered" : "selected")}, panel {page.PanelItem}): {(assigned ? "assigned" : "refused")}, " +
                $"slot {digit} was {before ?? "-"} now {now ?? "-"}, old slot {(oldSlot >= 0 ? QuickSlots.DigitOfSlot(oldSlot).ToString() : "-")} now {_session.Items.QuickSlots.ItemAt(oldSlot >= 0 ? oldSlot : slot) ?? "-"}");
            bool expectAssigned = st.Get("expect", "assigned") != "refused";
            if (expectAssigned && now != item) Fail($"invquick: slot {digit} holds {now ?? "-"}, expected {item}");
            else if (!expectAssigned && (assigned || now != before)) Fail($"invquick: {item} should have been refused (slot {digit}: {now ?? "-"})");
            else if (expectAssigned && oldSlot >= 0 && oldSlot != slot && _session.Items.QuickSlots.ItemAt(oldSlot) == item) Fail("invquick: the stack stayed in its old slot too");
            if (st.Bool("close", true) && screens.Current == page) screens.CloseCurrent();
            yield return new WaitForSecondsRealtime(0.2f);
        }

        /// <summary>
        /// `blocklag [after=0.1] [max=700] [combo=1] [quick=1] [hold=1]` — with a melee weapon drawn: an attack starts (a
        /// tap), `combo=1` clicks the next one into the queue, `after` s later the block key goes down and is held. Logs the
        /// time until the parade is up (G3: the running Hit ends first; from rest 50 ms + a frame) and fails over `max` ms
        /// or when the queued attack still started (the block replaces it). `quick=1`: a tap with the block held (quick
        /// attack), then the block must be up again (G3 FUN_10002130). The block is held `hold` s more, then released.
        /// </summary>
        IEnumerator BlockLagStep(ScenarioStep st)
        {
            if (Combat == null || !Combat.Fighter.Animator.Drawn) { Fail("blocklag: draw a melee weapon first"); yield break; }
            var a = Combat.Fighter.Animator;
            double t = Time.timeAsDouble;
            Combat.PrimaryDown(t - 0.04);
            Combat.PrimaryUp(t);
            yield return null;
            var first = Combat.LastStartedMove;
            if (st.Bool("combo", false))
            {
                yield return new WaitForSeconds(0.05f);
                double c = Time.timeAsDouble;
                Combat.PrimaryDown(c - 0.04);
                Combat.PrimaryUp(c);
            }
            float after = st.Float("after", 0.1f);
            yield return new WaitForSeconds(after);
            int movesAtPress = Combat.MovesStarted;
            float pressed = Time.time;
            Combat.SecondaryDown();
            while (!a.HoldsParade && Time.time - pressed < 3f) yield return null;
            float ms = (Time.time - pressed) * 1000f;
            int startedAfter = Combat.MovesStarted - movesAtPress;
            Log($"blocklag: {first} running, block pressed {after * 1000:0} ms in -> parade {(a.HoldsParade ? "up" : "NOT up")} after {ms:0} ms, moves started meanwhile {startedAfter}");
            float max = st.Float("max", 700f);
            if (!a.HoldsParade) { Fail("blocklag: the parade never came up"); Combat.SecondaryUp(); yield break; }
            if (ms > max) Fail($"blocklag: parade up after {ms:0} ms > {max:0} ms");
            if (startedAfter > 0) Fail($"blocklag: {startedAfter} move(s) started after the block press (the queued attack must be replaced)");
            if (st.Bool("quick", false))
            {
                yield return new WaitForSeconds(0.2f);
                double q = Time.timeAsDouble;
                Combat.PrimaryDown(q - 0.04);
                Combat.PrimaryUp(q);
                yield return null;
                var quick = Combat.LastStartedMove;
                float from = Time.time;
                while (Time.time - from < 0.1f) yield return null; // leave the parade first
                while (!a.HoldsParade && Time.time - from < 3f) yield return null;
                Log($"blocklag: {quick} with the block held -> parade {(a.HoldsParade ? "up again" : "NOT up")} {(Time.time - from) * 1000:0} ms after the tap");
                if (!a.HoldsParade) Fail("blocklag: the block did not come back after the quick attack while held (G3 FUN_10002130)");
            }
            float hold = st.Float("hold", 1f);
            float until = Time.time + hold;
            while (Time.time < until)
            {
                if (!a.HoldsParade) { Fail($"blocklag: the parade dropped while held ({until - Time.time:0.0} s before the release)"); break; }
                yield return null;
            }
            Combat.SecondaryUp();
            yield return new WaitForSeconds(0.3f);
        }

        /// <summary>
        /// `cancel [expect=1]` — the secondary button during an object use or its approach (G3 action 0x66 → PS_Interact_End;
        /// [owner] fix-75): waits until the use is over (10 s) and logs where the hero ended and whether the object is free.
        /// </summary>
        IEnumerator CancelStep(ScenarioStep st)
        {
            var use = FindAnyObjectByType<HeroObjectUse>();
            if (use == null) { Fail("cancel: no HeroObjectUse"); yield break; }
            bool approaching = use.Approaching, sitting = _session.Regeneration.Sitting;
            if (!use.RequestCancel())
            {
                if (st.Bool("expect", true)) Fail("cancel: no object use runs");
                yield break;
            }
            float until = Time.time + 10f;
            while (use.Busy && Time.time < until) yield return null;
            if (use.Busy) { Fail("cancel: the use still runs 10 s after the secondary button"); yield break; }
            Log($"cancel: use ended ({(approaching ? "during the approach" : sitting ? "seated: stood up" : "during the use")}), cancelled in the walk {use.Cancelled}, " +
                $"sitting {_session.Regeneration.Sitting}, parade {(Combat != null && Combat.Fighter.Animator.HoldsParade)}");
            if (Combat != null && Combat.Fighter.Animator.HoldsParade) Fail("cancel: the block went up after the secondary button ended the use");
            string users = use.LastObject != null ? ObjectUsers(use.LastObject) : "-";
            Log($"cancel: {use.LastObject ?? "-"} users now {users}");
            if (users.Contains(ActorId.Player.ToString())) Fail($"cancel: the hero still holds {use.LastObject} ({users})");
        }

        /// <summary>
        /// `useprobe [walked=1] [snap=0.3]` — the last approach to a used object: metres walked and what was placed at once
        /// (fails when he walked less than `walked` m or more than `snap` m was left), the hero's distance to the object and
        /// the angle between his facing and the direction to it.
        /// </summary>
        void UseProbeStep(ScenarioStep st)
        {
            var use = FindAnyObjectByType<HeroObjectUse>();
            if (use == null) { Fail("useprobe: no HeroObjectUse"); return; }
            string line = string.Format(CultureInfo.InvariantCulture, "useprobe: walked {0:0.00} m, snapped {1:0.00} m, last step {2}", use.ApproachWalked, use.ApproachSnapped, use.LastApproach);
            if (st.Has("obj") && TryObject(st.Get("obj"), out var obj))
            {
                var p = new Vector3(obj.Position[0], obj.Position[1], obj.Position[2]);
                var to = Vector3.ProjectOnPlane(p - Hero.transform.position, Vector3.up);
                line += string.Format(CultureInfo.InvariantCulture, ", {0:0.00} m from {1}, facing it within {2:0} deg", to.magnitude, obj.Name,
                                      Vector3.Angle(Vector3.ProjectOnPlane(Hero.transform.forward, Vector3.up), to));
            }
            // fix-103: where the use spot was moved out of the object, and whether the hero stands free now
            bool room = use.HeroHasRoom(out string blocker);
            line += string.Format(CultureInfo.InvariantCulture, ", spot moved {0:0.00} m, pushed out at the end {1:0.00} m, room {2}{3}",
                                  use.SpotMoved, use.LastDepenetration, room, room ? "" : " (inside " + blocker + ")");
            Log(line);
            if (st.Bool("clear", false) && !room) Fail($"useprobe: the hero stands inside {blocker}");
            if (st.Bool("clear", false) && use.SpotMoved < 0f) Fail("useprobe: no free use spot was found (the hero stayed where he was)");
            if (use.ApproachWalked < st.Float("walked", 1f)) Fail($"useprobe: walked only {use.ApproachWalked:0.00} m (expected an approach walk)");
            if (use.ApproachSnapped > st.Float("snap", 0.3f)) Fail($"useprobe: {use.ApproachSnapped:0.00} m left to place at once");
        }
    }
}
#endif
