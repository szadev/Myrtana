using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>What the focus scan measures its angles from (G3 gEFocusLookAt, set by the player states).</summary>
    public enum FocusReference
    {
        /// <summary>The camera's view direction (first person; PS_Magic, PS_Ranged).</summary>
        Camera,
        /// <summary>The hero's facing (PS_Melee_Loop while the block is held).</summary>
        Hero,
        /// <summary>The direction of the held movement keys in the camera frame, else the hero's facing (PS_Melee_Loop).</summary>
        Keys,
    }

    /// <summary>The scan settings of one combat mode (the hero's gCFocus_PS *ModeCharacterScan* values) [data].</summary>
    public enum FocusScanMode { Melee, Magic, Ranged }

    /// <summary>One character the focus scan looks at, measured from the hero.</summary>
    public struct FocusCandidate
    {
        /// <summary>Signed horizontal angle from the reference direction to the target's centre, degrees.</summary>
        public float AzimuthDeg;
        /// <summary>Vertical angle to the target's centre, degrees.</summary>
        public float ElevationDeg;
        /// <summary>Centre distance hero → target, cm.</summary>
        public float DistanceCm;
        /// <summary>Half the larger horizontal side of the target's bounding box, cm.</summary>
        public float HalfExtentCm;
        public bool FreeLineOfSight;
        /// <summary>
        /// G3 GetFocusEntityPriority of the candidate (<see cref="CombatFocusPriority"/>): −1 = never focused; higher wins
        /// before the angle. Default 0 (all equal).
        /// </summary>
        public int Priority;
    }

    /// <summary>
    /// The hero's combat focus as in G3 CP 1.75 (Docs/combat.md §9.2). There is no lock: the engine's
    /// gCFocus_PS picks the focus anew every frame from where the hero looks (soft focus), and the player states only
    /// choose the reference direction and fall back to the previous focus when the scan finds nothing. Which characters
    /// qualify and which come first is the script callback GetFocusEntityPriority (<see cref="CombatFocusPriority"/>:
    /// companions, friends and allies fighting the hero's foe are never focused in a fight); the hero never turns to the
    /// focus except while aligned (<see cref="CombatAlignment"/>).
    /// The camera does not look at it.
    /// </summary>
    public static class CombatFocus
    {
        /// <summary>MeleeModeCharacterScanRange / Azi of PC_Hero (cm, degrees); elevation ±90 [data].</summary>
        public const float MeleeScanRangeCm = 500f, MeleeScanAziDeg = 90f;
        /// <summary>MagicModeCharacterScanRange / Azi of PC_Hero (cm, degrees); elevation ±90 [data].</summary>
        public const float MagicScanRangeCm = 3500f, MagicScanAziDeg = 45f;
        /// <summary>RangedModeCharacterScanRange / Azi of PC_Hero (cm, degrees); elevation ±90 [data]. PS_Ranged measures from the camera, without the range rating.</summary>
        public const float RangedScanRangeCm = 3500f, RangedScanAziDeg = 45f;
        /// <summary>Hard focus (first person): HardFocusMaxAzi / MaxElev (degrees); the range is the mode's [data].</summary>
        public const float HardAziDeg = 10f, HardElevDeg = 20f;
        /// <summary>Range rating weights (gCFocus_PS::FindFocusEntity): score = 8·distance/reach + 2·|azimuth|/maxAzimuth.</summary>
        public const float RangeWeight = 8f, AngleWeight = 2f;
        /// <summary>NormalModePreferedRange: the reach used by the range rating without a melee weapon, cm [data].</summary>
        public const float PreferredRangeCm = 150f;
        /// <summary>PS_Melee_Loop keeps the previous focus (when the scan finds nothing) only within this distance, cm.</summary>
        public const float KeepPreviousCm = 500f;

        /// <summary>PS_Melee_Loop: first person → camera; block held → hero facing; otherwise → movement keys.</summary>
        public static FocusReference MeleeReference(bool firstPerson, bool blockHeld) =>
            firstPerson ? FocusReference.Camera : blockHeld ? FocusReference.Hero : FocusReference.Keys;

        /// <summary>
        /// The yaw the scan measures from: camera yaw; hero yaw; or for Keys the held keys (x right, y forward)
        /// turned into the camera frame (FocusLookAtKeysFOR = Camera), the hero yaw when no key is held.
        /// </summary>
        public static float ReferenceYaw(FocusReference reference, float cameraYaw, float heroYaw, float keysX, float keysY)
        {
            switch (reference)
            {
                case FocusReference.Camera: return cameraYaw;
                case FocusReference.Hero: return heroYaw;
                default:
                    if (keysX * keysX + keysY * keysY < 1e-6f) return heroYaw;
                    return cameraYaw + (float)(Math.Atan2(keysX, keysY) * 180.0 / Math.PI);
            }
        }

        /// <summary>Signed difference <paramref name="to"/> − <paramref name="from"/> in (−180, 180], degrees.</summary>
        public static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d <= -180f) d += 360f;
            return d;
        }

        /// <summary>
        /// Whether the stance's loop makes the focus the hero's CurrentTarget (the "hero's target" of
        /// <see cref="FocusFacts.TargetsHerosTarget"/>) [fix-90: PS_Melee_Loop and PS_Magic_Loop call
        /// PSNpc::SetCurrentTarget(focus) every tick; no PS_Ranged state does]. So with a bow a creature fighting the
        /// focused bandit is not dropped as his "ally" — it stays focusable and wins when it is nearer the crosshair.
        /// </summary>
        public static bool SetsHeroTarget(FocusScanMode mode) => mode != FocusScanMode.Ranged;

        /// <summary>
        /// Whether the PS_Normal focus (items, interactive objects, characters to talk to) runs: PC_Hero gCFocus_PS gives
        /// items and interactive objects priority −1 in the melee, ranged and magic modes (Melee/Ranged/MagicModeItemPriority
        /// and …InteractPriority −1) and GetFocusEntityPriority returns −1 for non-NPCs there [data + RE fix-90]; characters
        /// are then the combat focus's (<see cref="Pick"/>). False while a weapon, bow or spell is drawn.
        /// </summary>
        public static bool InteractionFocus(bool weaponDrawn) => !weaponDrawn;

        public static float ScanRangeCm(FocusScanMode mode) =>
            mode == FocusScanMode.Magic ? MagicScanRangeCm : mode == FocusScanMode.Ranged ? RangedScanRangeCm : MeleeScanRangeCm;

        /// <summary>The maximum azimuth of the scan (GetCurrentMaxAngles): hard focus 10°, else the mode's.</summary>
        public static float MaxAzimuth(FocusScanMode mode, bool hard) =>
            hard ? HardAziDeg : mode == FocusScanMode.Magic ? MagicScanAziDeg : mode == FocusScanMode.Ranged ? RangedScanAziDeg : MeleeScanAziDeg;

        /// <summary>
        /// FocusCheck: within range measured to the target's edge (distance² − halfExtent² ≤ range²), inside the
        /// angles (soft: |azimuth| ≤ the mode's, elevation ±90; hard: 10° / ±20°) and in free line of sight.
        /// </summary>
        public static bool InScan(in FocusCandidate c, FocusScanMode mode, bool hard)
        {
            float range = ScanRangeCm(mode);
            float edge2 = Math.Max(0f, c.DistanceCm * c.DistanceCm - c.HalfExtentCm * c.HalfExtentCm);
            if (edge2 > range * range) return false;
            if (Math.Abs(c.AzimuthDeg) > MaxAzimuth(mode, hard)) return false;
            float elev = hard ? HardElevDeg : 90f;
            if (c.ElevationDeg > elev || c.ElevationDeg < -elev) return false;
            return c.FreeLineOfSight;
        }

        /// <summary>
        /// gCFocus_PS::FindFocusEntity for characters [Game.dll FindFocusEntity / FocusCheck / HasLowerPriority]: the
        /// candidates are walked in their given order (G3: the order of the interaction query, ours: the fighter list).
        /// FocusCheck drops a candidate outside the scan or with priority −1; one with the priority of the current best
        /// must be nearer the reference direction (smaller |azimuth|); a lower priority than the current best is skipped,
        /// a higher one takes over whatever its angle. Every candidate taken over that way is a "chain" member; with the
        /// range rating on (melee, not hard focus) the nearest chain member wins instead of the last one when its score
        /// 8·d/reach + 2·|azi|/maxAzi is lower. So the range rating can only fall back on a near candidate that was the
        /// best one at some point of the walk, not on any near one. <paramref name="reachCm"/>: weapon length plus half
        /// the hero's width (no weapon: <see cref="PreferredRangeCm"/> plus half the width). Returns the index or −1.
        /// [quirk not ported: G3 rates the signed azimuth here, which favours one side.]
        /// </summary>
        public static int Pick(IReadOnlyList<FocusCandidate> candidates, FocusScanMode mode, bool hard, bool rangeRating, float reachCm)
        {
            int best = -1, nearest = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Priority < 0 || !InScan(c, mode, hard)) continue;
                if (best >= 0)
                {
                    var b = candidates[best];
                    if (c.Priority < b.Priority) continue;
                    if (c.Priority == b.Priority && Math.Abs(c.AzimuthDeg) >= Math.Abs(b.AzimuthDeg)) continue;
                }
                best = i;
                if (nearest < 0 || c.DistanceCm < candidates[nearest].DistanceCm) nearest = i;
            }
            if (best < 0 || !rangeRating || hard || nearest == best) return best;
            float reach = reachCm > 0 ? reachCm : 1f;
            float maxAzi = MaxAzimuth(mode, false);
            float Score(FocusCandidate c) => c.DistanceCm / reach * RangeWeight + Math.Abs(c.AzimuthDeg) / maxAzi * AngleWeight;
            return Score(candidates[nearest]) < Score(candidates[best]) ? nearest : best;
        }

        /// <summary>
        /// PS_Melee_Loop when the scan finds nothing: the previous focus stays if it is not dead, a downed one only while
        /// the block is held, and in any case within 500 cm.
        /// </summary>
        public static bool KeepsPrevious(VitalState state, float distanceCm, bool blockHeld) =>
            state != VitalState.Dead && (state != VitalState.Down || blockHeld) && distanceCm <= KeepPreviousCm;
    }

    /// <summary>
    /// The hero's alignment to a target in G3 CP 1.75 (Entity::SetAlignmentTarget + movement constraint 360). The
    /// mouse always turns the camera; normally the body follows the camera's direction. While aligned the body turns to
    /// the target instead, at TurnSpeedMax, and the movement keys stay relative to the camera, so the hero strafes
    /// around the target. Docs/combat.md §9.2.
    /// </summary>
    public static class CombatAlignment
    {
        /// <summary>PC_Hero gCCharacterMovement_PS.TurnSpeedMax, degrees per second (TurnAcceleration 1e6: no ramp) [data].</summary>
        public const float TurnSpeedMax = 200f;
        /// <summary>TurnSpeedModifier: applied to TurnSpeedMax while sprinting (movement mode 5) [data].</summary>
        public const float SprintTurnModifier = 0.5f;

        /// <summary>
        /// Moves whose state aligns the hero to the current focus when it starts (third person only): PS_Melee_Attack,
        /// PowerAttack, QuickAttack, SimpleWhirl; with magic PS_Magic_Cast, QuickCast, PowerCast. Whirl, Pierce, Hack,
        /// Finishing and JumpBack keep whatever alignment there is.
        /// </summary>
        public static bool AlignsOnStart(CombatAction move) =>
            move == CombatAction.Attack || move == CombatAction.PowerAttack || move == CombatAction.SimpleWhirl ||
            move.IsQuick();

        public enum LoopCommand { Keep, AlignToFocus, Release }

        /// <summary>
        /// PS_Melee_Loop each tick between moves: block held with a focus → align to it; otherwise a held movement key
        /// releases the alignment (back to facing the camera direction); otherwise it stays (the hero keeps facing the
        /// last attacked target while standing).
        /// </summary>
        public static LoopCommand MeleeLoop(bool blockHeld, bool hasFocus, bool moveKeyHeld) =>
            blockHeld && hasFocus ? LoopCommand.AlignToFocus : moveKeyHeld ? LoopCommand.Release : LoopCommand.Keep;

        /// <summary>PS_Magic_Loop: only a held movement key releases the alignment of a cast.</summary>
        public static LoopCommand MagicLoop(bool moveKeyHeld) => moveKeyHeld ? LoopCommand.Release : LoopCommand.Keep;

        /// <summary>One step of the body's yaw towards <paramref name="wantYaw"/> at TurnSpeedMax (× 0.5 sprinting).</summary>
        public static float Turn(float bodyYaw, float wantYaw, float dt, bool sprinting)
        {
            float max = TurnSpeedMax * (sprinting ? SprintTurnModifier : 1f) * Math.Max(0f, dt);
            float d = CombatFocus.DeltaAngle(bodyYaw, wantYaw);
            if (Math.Abs(d) <= max) return wantYaw;
            return bodyYaw + Math.Sign(d) * max;
        }
    }
}
