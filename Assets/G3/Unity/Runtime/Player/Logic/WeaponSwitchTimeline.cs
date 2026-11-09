using System;
using System.Globalization;

namespace G3.Player
{
    /// <summary>The two motions of one hand set's draw (<c>Hold*_Begin</c>, <c>Hold*_End</c>), s; a sheathe plays them backwards.</summary>
    public readonly struct HoldPairLength
    {
        public readonly float Begin, End;

        public HoldPairLength(float begin, float end)
        {
            Begin = begin;
            End = end;
        }

        public float Total => Begin + End;
    }

    /// <summary>
    /// Moments of a weapon switch, s from the key press: the command is taken (<see cref="Accepted"/>), the old weapon
    /// leaves the hand (<see cref="SheatheHand"/>, G3 HoldStacks) and the put-away ends, the draw starts, the new weapon
    /// is in the hand (<see cref="DrawHand"/>) and the draw ends: attacks are allowed (<see cref="Ready"/>).
    /// </summary>
    public readonly struct SwitchTimes
    {
        public readonly float Accepted, SheatheHand, SheatheEnd, DrawStart, DrawHand, Ready;

        public SwitchTimes(float accepted, float sheatheHand, float sheatheEnd, float drawStart, float drawHand, float ready)
        {
            Accepted = accepted;
            SheatheHand = sheatheHand;
            SheatheEnd = sheatheEnd;
            DrawStart = drawStart;
            DrawHand = drawHand;
            Ready = ready;
        }

        public override string ToString()
        {
            string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
            return $"accepted {F(Accepted)}, old weapon off the hand {F(SheatheHand)}, put away {F(SheatheEnd)}, draw {F(DrawStart)}, " +
                   $"new weapon in the hand {F(DrawHand)}, ready {F(Ready)} s";
        }
    }

    /// <summary>
    /// The hero's weapon switch timeline as G3 (CP 1.75) runs it (Docs/combat.md §9.8, fix-101). A weapon key
    /// (<c>OnPlayerSelectWeapon</c>) or quick-slot key (<c>OnPlayerUseQuickSlot</c>) sets the task PS_Melee / PS_Ranged /
    /// PS_Magic / PS_Normal, whose entry runs one blocking <c>_AI_HoldInventoryItems</c>: the held hands are put away first
    /// (<c>_AI_HoldLeftInventoryItem_Base(-1)</c> for a bow alone, <c>_AI_HoldInventoryItems_Base(-1, -1)</c> for bow and
    /// nocked arrow, <c>_AI_HoldRightInventoryItem_Base(-1)</c> for a right-hand weapon), each to its end, then the new
    /// stacks are drawn — no direct swap motion, no skipped put-away. Each Base plays its two motions around HoldStacks at
    /// speed 1, every one to its end (<c>sAIPlayAniInstr</c>), the next instruction in the same script tick. So a switch
    /// takes the old set's pair plus the new set's pair; the old weapon leaves the hand after the put-away's first motion
    /// (End backwards), the new one enters after the draw's first motion (Begin forwards).
    /// </summary>
    public static class WeaponSwitchTimeline
    {
        /// <summary>One frame at 60 fps, s.</summary>
        public const float Frame60 = 1f / 60f;

        /// <summary>
        /// Hero hold pairs (standing, the <c>_N_</c> motions of _compiledAnimation.pak; the moving <c>_O_</c> overlays have
        /// the same totals, 1H 0.36 + 0.40, spell 0.32 + 0.24). Hand names: bow, crossbow, 1H, 2H, staff, fist, spell, none.
        /// </summary>
        public static HoldPairLength Pair(string hand)
        {
            switch ((hand ?? "none").ToLowerInvariant())
            {
                case "bow":
                case "crossbow": return new HoldPairLength(0.40f, 0.36f); // Bow_None / CrossBow_None HoldLeft (with the arrow: Bow_Arrow Hold, same)
                case "1h": return new HoldPairLength(0.20f, 0.56f);       // None_1H HoldRight
                case "2h":
                case "staff":
                case "fist": return new HoldPairLength(0.36f, 0.40f);     // None_2H / None_Staff / None_Fist HoldRight
                case "spell": return new HoldPairLength(0.28f, 0.28f);    // None_Cast HoldRight
                case "none": return new HoldPairLength(0f, 0f);
                default: throw new ArgumentException("unknown hand " + hand, nameof(hand));
            }
        }

        /// <summary>G3: the press is taken at <paramref name="acceptedAt"/> (a loop state; 0 = at once) and the draw follows the put-away at once.</summary>
        public static SwitchTimes G3(string from, string to, float acceptedAt = 0f) => Build(from, to, acceptedAt, 0f);

        /// <summary>
        /// A switch whose draw starts <paramref name="joinDelay"/> after the put-away ended (ours before fix-101: one frame,
        /// the queued draw ran in the next frame's PlayerCombat.Update).
        /// </summary>
        public static SwitchTimes Build(string from, string to, float acceptedAt, float joinDelay)
        {
            var old = Pair(from);
            var neu = Pair(to);
            float sheatheEnd = acceptedAt + old.Total;
            float drawStart = sheatheEnd + (old.Total > 0f ? joinDelay : 0f);
            return new SwitchTimes(acceptedAt, acceptedAt + old.End, sheatheEnd, drawStart, drawStart + neu.Begin, drawStart + neu.Total);
        }
    }

    /// <summary>What the hero's body is doing when a weapon or quick-slot key comes (<see cref="WeaponSwitchInput.Decide"/>).</summary>
    public struct WeaponSwitchState
    {
        /// <summary>A combat one-shot or held pose has the body: a draw, a sheathe, a melee move, the parade, a hit reaction, a fall.</summary>
        public bool MoveRuns;
        /// <summary>The bow/crossbow reloads (PS_Ranged_Reload), shoots (PS_Ranged_Shoot) or raises the aim (PS_Ranged_Aim, PowerAim).</summary>
        public bool Reload, Shot, AimRaising;
        /// <summary>The aim is held (PS_Ranged_Aim_Loop).</summary>
        public bool AimHeld;
        /// <summary>A spell is loaded or released (PS_Magic_Cast …).</summary>
        public bool Cast;
        /// <summary>The first half of a switch runs: the old weapon is being put away and the new one waits (still one G3 command).</summary>
        public bool SwitchRuns;
    }

    public enum WeaponSwitchVerdict
    {
        /// <summary>A loop state: the key acts now.</summary>
        Now,
        /// <summary>PS_Ranged_Aim_Loop: the key acts now; the held aim simply ends with it (no lowering motion).</summary>
        EndAimThenNow,
        /// <summary>Another state runs: the key waits in the one-slot action queue for the next loop state.</summary>
        Wait,
    }

    /// <summary>
    /// When G3 acts on a weapon or quick-slot key (fix-101). OnPlayerGameKeyPressed does not run the key: it puts it into
    /// the player's one-slot action queue (FUN_10007940 = clear + add; the weapon key as action 0x44, quick slots
    /// 0x83…0x8f) and only the loop states read that queue: PS_Normal_Loop, PS_Melee_Loop, PS_Magic_Loop,
    /// PS_Ranged_Walk_Loop and PS_Ranged_Aim_Loop call OnPlayerSelectWeapon / OnPlayerUseQuickSlot. A key during a draw,
    /// a melee move, the parade, the reload, the shot, the aim's raise or a cast therefore acts the moment that state
    /// returns to its loop; a later button press (OnPlayerAction / OnPlayerSecondaryAction enqueue too) replaces it, and
    /// ResetAll (a stumble, a fall) clears it. Ours before fix-101 refused the key in all those states, so the press was
    /// lost and had to be repeated.
    /// </summary>
    public static class WeaponSwitchInput
    {
        public static WeaponSwitchVerdict Decide(in WeaponSwitchState s)
        {
            if (s.MoveRuns || s.SwitchRuns || s.Reload || s.Shot || s.AimRaising || s.Cast) return WeaponSwitchVerdict.Wait;
            return s.AimHeld ? WeaponSwitchVerdict.EndAimThenNow : WeaponSwitchVerdict.Now;
        }
    }

    /// <summary>
    /// The one-slot queue of a weapon or quick-slot key (<see cref="WeaponSwitchInput"/>): the latest key wins, a button
    /// press or a reset clears it, <see cref="Poll"/> hands the waiting key out once the body is in a loop state.
    /// </summary>
    public sealed class WeaponSwitchQueue<T> where T : class
    {
        T _key;

        /// <summary>The waiting key (null = none).</summary>
        public T Waiting => _key;
        /// <summary>When it came (the owner's clock).</summary>
        public double Since { get; private set; }

        /// <summary>A key came: it acts now (<see cref="WeaponSwitchVerdict.Now"/> / <see cref="WeaponSwitchVerdict.EndAimThenNow"/>) or waits, replacing a waiting one.</summary>
        public WeaponSwitchVerdict Offer(T key, in WeaponSwitchState state, double now)
        {
            var verdict = WeaponSwitchInput.Decide(state);
            if (verdict == WeaponSwitchVerdict.Wait)
            {
                _key = key;
                Since = now;
            }
            else _key = null;
            return verdict;
        }

        /// <summary>The waiting key when the body reached a loop state (taken out of the queue), else null.</summary>
        public T Poll(in WeaponSwitchState state, out WeaponSwitchVerdict verdict)
        {
            verdict = WeaponSwitchInput.Decide(state);
            if (_key == null || verdict == WeaponSwitchVerdict.Wait) return null;
            var key = _key;
            _key = null;
            return key;
        }

        /// <summary>A button press took the slot, or ResetAll: the waiting key is gone. True when one was waiting.</summary>
        public bool Clear()
        {
            bool had = _key != null;
            _key = null;
            return had;
        }
    }
}

namespace G3.Player
{
    /// <summary>What happens to the draw that waits for the put-away of a weapon switch (<see cref="PendingDrawRules.Decide"/>).</summary>
    public enum PendingDrawStep
    {
        /// <summary>The put-away (or whatever cut it: a stumble, a fall) still has the body.</summary>
        Wait,
        /// <summary>The hands are empty: the new weapon is drawn now.</summary>
        Draw,
        /// <summary>The put-away was cut short and the old weapon is still in the hand: the switch is over, nothing is drawn.</summary>
        Drop,
    }

    /// <summary>
    /// fix-197 (owner: "aiming the bow, an animal bites, then he can neither switch nor sheathe"): the second half of a
    /// weapon switch (the draw after the put-away, <see cref="WeaponSwitchTimeline"/>) waits for empty hands. A hit
    /// reaction, a fall, a talk gesture or an object use that cuts the put-away before G3 HoldStacks leaves the old weapon
    /// in the hand; G3 replaced the PS_* task by the ZS_* one, so the switch command is gone. Ours kept the draw waiting
    /// for empty hands that never came, and every later weapon or quick-slot key waited behind it ("a switch runs").
    /// </summary>
    public static class PendingDrawRules
    {
        public static PendingDrawStep Decide(bool bodyBusy, bool weaponDrawn)
        {
            if (bodyBusy) return PendingDrawStep.Wait;
            return weaponDrawn ? PendingDrawStep.Drop : PendingDrawStep.Draw;
        }
    }
}
