using System;

namespace G3.Player
{
    /// <summary>
    /// One <c>gCCameraModeParams</c> of the G3 <c>PC_Camera</c> entity (gCCameraAI_PS), in metres and degrees. The
    /// "…MinRange / …MaxRange" pairs of the original are the value at the near and the far end of the zoom range; the
    /// camera interpolates them by its current distance [interp: from the names, not traced].
    /// </summary>
    public readonly struct CameraModeParams
    {
        public CameraModeParams(float bestRange, float minRange, float minRangeOnMove, float maxRange, float bestElevation,
            float fovMinRange, float fovMaxRange, float rotOffsetXMinRange, float rotOffsetXMaxRange,
            float targetOffsetYMinRange, float targetOffsetYMaxRange)
        {
            BestRange = bestRange;
            MinRange = minRange;
            MinRangeOnMove = minRangeOnMove;
            MaxRange = maxRange;
            BestElevation = bestElevation;
            FovMinRange = fovMinRange;
            FovMaxRange = fovMaxRange;
            RotOffsetXMinRange = rotOffsetXMinRange;
            RotOffsetXMaxRange = rotOffsetXMaxRange;
            TargetOffsetYMinRange = targetOffsetYMinRange;
            TargetOffsetYMaxRange = targetOffsetYMaxRange;
        }

        /// <summary>Distance the mode starts with (SetMode resets the wished distance to it).</summary>
        public float BestRange { get; }
        /// <summary>Closest wheel zoom.</summary>
        public float MinRange { get; }
        /// <summary>While the hero is not standing, the camera keeps at least this distance (GetCurrentWishedDistance).</summary>
        public float MinRangeOnMove { get; }
        /// <summary>Farthest wheel zoom.</summary>
        public float MaxRange { get; }
        public float BestElevation { get; }
        /// <summary>Vertical field of view at MinRange / MaxRange (G3's FOV is vertical, as Unity's).</summary>
        public float FovMinRange { get; }
        public float FovMaxRange { get; }
        public float RotOffsetXMinRange { get; }
        public float RotOffsetXMaxRange { get; }
        public float TargetOffsetYMinRange { get; }
        public float TargetOffsetYMaxRange { get; }

        /// <summary>0 at MinRange, 1 at MaxRange (clamped).</summary>
        public float RangeFactor(float distance) =>
            MaxRange <= MinRange ? 0 : Clamp01((distance - MinRange) / (MaxRange - MinRange));

        public float Fov(float distance) => Lerp(FovMinRange, FovMaxRange, RangeFactor(distance));

        /// <summary>
        /// G3 eases the "…MinRange/…MaxRange" offsets by the zoom with a half cosine (gCCameraAI_PS::UpdatePlayerPosition,
        /// FUN_2007eda0: (sin(pi*t - pi/2) + 1) / 2).
        /// </summary>
        public static float OffsetEase(float t) => (float)((Math.Sin(Math.PI * t - Math.PI / 2) + 1) * 0.5);

        /// <summary>The look-at point's height above the hero's head at this distance (TargetOffsetY, eased by the zoom).</summary>
        public float TargetOffsetY(float distance) => Lerp(TargetOffsetYMinRange, TargetOffsetYMaxRange, OffsetEase(RangeFactor(distance)));

        /// <summary>Wheel zoom kept within MinRange…MaxRange.</summary>
        public float ClampZoom(float distance) => Math.Max(MinRange, Math.Min(MaxRange, distance));

        /// <summary>
        /// The distance the camera wants: the zoom, but at least MinRangeOnMove while the hero moves (G3 pulls the camera
        /// back when he starts walking and lets it come closer again when he stands).
        /// </summary>
        public float WishedDistance(float zoom, bool moving) => moving ? Math.Max(zoom, MinRangeOnMove) : zoom;

        /// <summary>fix-256: <see cref="WishedDistance"/> with the MinRangeOnMove floor weighted by
        /// <paramref name="moveWeight"/> (CameraStability.MoveRangeWeight): 0 = the zoom, 1 = the moving distance.</summary>
        public float EasedWishedDistance(float zoom, float moveWeight) =>
            zoom + (Math.Max(zoom, MinRangeOnMove) - zoom) * Math.Max(0f, Math.Min(1f, moveWeight));

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>The camera modes the player uses (gECameraMode, index into gCCameraAI_PS::SetMode's table).</summary>
    public enum CameraMode
    {
        Normal = 0, Melee = 1, Ranged = 2, Magic = 3, Dialog = 4, Inventory = 5, Swim = 6, Dive = 7, Interaction = 8,
        Fall = 9, Death = 10, FirstPerson = 11, Shoulder = 12,
    }

    /// <summary>
    /// G3 CP 1.75 player camera modes (PC_Camera gCCameraAI_PS, values from the world's SysDyn entity; Docs/hero.md
    /// "Camera"). Which mode is used comes from the player state scripts (Script_Game, read for understanding only):
    /// PS_Normal, PS_Melee, PS_Magic, PS_Ranged (and PS_Ghost, PS_DuskToDawn) all call <c>PSCamera::SetMode(2)</c>, i.e.
    /// <b>CamModeRanged for every weapon state</b> — G3 does not switch the camera when a weapon is drawn. PS_HUD and
    /// PS_Interact use mode 5 (CamModeInventory). First person in mode 2 uses CamMode1stPersonRnged (FOV 70). No script
    /// sets Swim, Fall or Death.
    /// </summary>
    public static class CameraModes
    {
        /// <summary>CamModeRanged: BestRange 300, MinRange 200, MinRangeOnMove 300, MaxRange 1500, BestElev 30, FOV 60…65,
        /// RotOffsetX 0…−3, TargetOffsetY 50…120 cm.</summary>
        public static readonly CameraModeParams Ranged = new CameraModeParams(3f, 2f, 3f, 15f, 30f, 60f, 65f, 0f, -3f, 0.5f, 1.2f);
        /// <summary>CamModeNormal (not used by any player state): BestRange 150, MinRange 100, FOV 65…55.</summary>
        public static readonly CameraModeParams Normal = new CameraModeParams(1.5f, 1f, 3f, 15f, 30f, 65f, 55f, -10f, 5f, 0f, 0f);
        /// <summary>CamModeMelee (not used by any player state): BestRange 450, MinRange 150, FOV 60…65.</summary>
        public static readonly CameraModeParams Melee = new CameraModeParams(4.5f, 1.5f, 3f, 15f, 30f, 60f, 65f, 5f, -5f, 0f, 0f);
        /// <summary>CamModeInventory (PS_HUD, PS_Interact): BestRange 150, MinRange 100, FOV 45.</summary>
        public static readonly CameraModeParams Inventory = new CameraModeParams(1.5f, 1f, 3f, 15f, 30f, 45f, 45f, -5f, -5f, 0f, 0f);
        /// <summary>
        /// Sideways offset of the third-person camera, m: none. Every PC_Camera mode has CamOffsetX and TargetOffsetX 0, so
        /// G3's camera hangs straight behind the head and the screen centre (the bow crosshair) lies on the vertical plane
        /// through the hero and his look — what gCFocus_PS measures the ranged focus azimuth in (camera heading, from the
        /// hero). fix-90: the port's 0.35 m right-shoulder offset put the crosshair beside that plane, so the focus could
        /// pick a foe left of the hero over the one under the crosshair. The rig's shoulderOffset applies only without the
        /// G3 modes.
        /// </summary>
        public const float ShoulderOffset = 0f;

        /// <summary>The camera's sideways offset: G3's (none) with the G3 modes, else the rig's own.</summary>
        public static float Shoulder(bool g3Modes, float rigShoulder) => g3Modes ? ShoulderOffset : rigShoulder;

        /// <summary>CamMode1stPersonRnged / FirstPersonFight / FirstPerson: FOV 70.</summary>
        public const float FirstPersonFov = 70f;

        /// <summary>The mode a player state script sets (PSCamera::SetMode argument).</summary>
        public static CameraMode OfPlayerState(bool hudOrInteraction) => hudOrInteraction ? CameraMode.Inventory : CameraMode.Ranged;

        /// <summary>
        /// fix-122 [owner]: the mode with the port option "Camera in windows". Default (<paramref name="originalWindowCamera"/>
        /// false, "as in play, no zoom"): a HUD page (loot, inventory, trade, character, map, documents) or an object use
        /// (approach, kneel, loot) keeps the play mode — no switch, no zoom reset, no blend. Original: G3's CamModeInventory.
        /// The dialog has its own camera (DialogCameraRig) either way.
        /// </summary>
        public static CameraMode OfPlayerState(bool hudOrInteraction, bool originalWindowCamera) =>
            OfPlayerState(hudOrInteraction && originalWindowCamera);

        /// <summary>
        /// fix-122: the play camera is held as it is while a page or a use runs (the default "no zoom" option): the wished
        /// distance, the head-following pivot height and the look-snap offsets (the use's turns to its spot) stay where they
        /// were, so the view neither zooms, drops with the kneel nor swings round; obstacles still pull it in.
        /// </summary>
        public static bool HoldsPlayCamera(bool hudOrInteraction, bool originalWindowCamera) => hudOrInteraction && !originalWindowCamera;

        /// <summary>
        /// fix-122: a value held while <paramref name="held"/> (<see cref="HoldsPlayCamera"/>): the live one otherwise, and
        /// the live one when nothing was kept yet (NaN).
        /// </summary>
        public static float Hold(bool held, float live, ref float kept)
        {
            if (!held || float.IsNaN(kept)) kept = live;
            return kept;
        }

        /// <summary>
        /// gCCameraAI_PS::UpdateDistance (Game.dll 0x20083670), in metres: the camera distance moves towards the wished one
        /// by new = (cur + a·D) / (1 + a), a = dt / (0.025·s) · (4q + 1) / (q + 1), q = (D − cur)² / 100000 (in cm²),
        /// D = min(wished, limit); s = max(speed, 0.01) (going out always at speed × 1); the result never exceeds the limit
        /// (the free distance behind the hero: an obstacle pulls the camera in at once). <paramref name="speed"/> is the camera
        /// entity's member at +0x364 [interp: not identified; <see cref="DistanceSpeed"/>].
        /// </summary>
        public static float SmoothDistance(float current, float wished, float limit, float dt, float speed = DistanceSpeed)
        {
            if (current < 0) return Math.Min(wished, limit);
            if (wished == 0) return Math.Min(current, 0f);
            float target = Math.Min(Math.Abs(wished), limit);
            float s = Math.Max(speed, 0.01f);
            float gapCm = (target - current) * 100f;
            float q = gapCm * gapCm / 100000f;
            float a = dt / (0.025f * s) * (4f * q + 1f) / (q + 1f);
            float next = (current + a * target) / (1f + a);
            return Math.Min(next, limit);
        }

        /// <summary>The speed of <see cref="SmoothDistance"/> [interp].</summary>
        public const float DistanceSpeed = 1f;

        public static CameraModeParams Params(CameraMode mode)
        {
            switch (mode)
            {
                case CameraMode.Normal: return Normal;
                case CameraMode.Melee: return Melee;
                case CameraMode.Inventory: return Inventory;
                default: return Ranged;
            }
        }
    }
}
