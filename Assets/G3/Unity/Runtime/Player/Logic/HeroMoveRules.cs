using System;

namespace G3.Player
{
    /// <summary>The hero's player state as far as facing and gait care (G3 PS_* states, Docs/hero.md "Facing and gait").</summary>
    public enum HeroMoveState
    {
        /// <summary>PS_Normal (also swimming, sneaking without a weapon).</summary>
        Normal,
        /// <summary>PS_Melee_Loop: melee weapon or fists drawn, no block.</summary>
        Melee,
        /// <summary>PS_Melee_Loop with the block held (Parade stance walking).</summary>
        MeleeParade,
        /// <summary>A melee move runs (PS_Melee_Attack, PowerAttack, QuickAttack, SimpleWhirl …).</summary>
        MeleeMove,
        /// <summary>PS_Melee_Sneak: sneaking with a melee weapon (Parade stance).</summary>
        MeleeSneak,
        /// <summary>PS_Ranged_Walk_Loop: bow or crossbow drawn, not aiming.</summary>
        Ranged,
        /// <summary>PS_Ranged_Aim … Aim_Loop, PowerAim, Shoot, Aim_Reload: aiming.</summary>
        RangedAim,
        /// <summary>PS_Ranged_Sneak.</summary>
        RangedSneak,
        /// <summary>PS_Magic_Loop: the spell hand drawn.</summary>
        Magic,
        /// <summary>PS_Magic_Cast / QuickCast / PowerCast: a spell is being cast.</summary>
        MagicCast,
    }

    /// <summary>Where the hero's body faces.</summary>
    public enum HeroFacing
    {
        /// <summary>The camera's look direction (G3 movement constraint 0: SetAlignmentLookAt(camera forward)).</summary>
        Camera,
        /// <summary>The combat alignment target (constraint 360 + SetAlignmentTarget: the focus, or the held yaw).</summary>
        AlignTarget,
    }

    /// <summary>The gait the movement keys run in.</summary>
    public enum HeroGait { Sneak, Walk, Run, Sprint }

    /// <summary>What the held Walk / Sprint key does (G3 <c>OnPlayerWalk</c> flags +1 walk, +2 sprint).</summary>
    public enum WalkKeyFlag { None, Walk, Sprint }

    /// <summary>A key direction relative to the body: forward, back, or sideways (left and right have the same speeds).</summary>
    public enum HeroMoveDir { Fwd, Back, Side }

    /// <summary>
    /// Root speeds (m/s) of one standing locomotion set of the hero, <c>Hero_Stand_&lt;left&gt;_&lt;right&gt;_P0_Move_&lt;gait&gt;_N_&lt;dir&gt;</c>;
    /// 0 = the set has no such motion. G3 moves a character by the speed in its motion name, so the drawn hands set the
    /// speed. G3 data (CP 1.75 _compiledAnimation): unarmed walk 160 / run 400, sideways 300 / sprint 600 forward only;
    /// melee, fists, staff, spell hand, 1H+shield, 1H+torch, dual 1H: sprint 500 forward, 400 sideways; bow walk 150,
    /// crossbow walk 200, both without sprint; torch alone without sprint; mage staff + spell hand sprint 600.
    /// </summary>
    public readonly struct HeroGaitSpeeds
    {
        public readonly float WalkFwd, WalkBack, WalkSide, RunFwd, RunBack, RunSide, SprintFwd, SprintSide;

        public HeroGaitSpeeds(float walkFwd, float walkBack, float walkSide, float runFwd, float runBack, float runSide, float sprintFwd, float sprintSide)
        {
            WalkFwd = walkFwd; WalkBack = walkBack; WalkSide = walkSide;
            RunFwd = runFwd; RunBack = runBack; RunSide = runSide;
            SprintFwd = sprintFwd; SprintSide = sprintSide;
        }

        /// <summary>From a lookup of motion speeds by gait ("Walk", "Run", "Sprint") and direction ("Fwd", "Back", "Left", "Right").</summary>
        public static HeroGaitSpeeds From(Func<string, string, float> speed) => new HeroGaitSpeeds(
            speed("Walk", "Fwd"), speed("Walk", "Back"), Math.Max(speed("Walk", "Left"), speed("Walk", "Right")),
            speed("Run", "Fwd"), speed("Run", "Back"), Math.Max(speed("Run", "Left"), speed("Run", "Right")),
            speed("Sprint", "Fwd"), Math.Max(speed("Sprint", "Left"), speed("Sprint", "Right")));

        /// <summary>
        /// The motion speed the keys move with in <paramref name="gait"/> towards <paramref name="dir"/> (0 = no motion):
        /// sprinting sideways uses the Sprint Left/Right motions where the set has them, else the Run ones (the unarmed
        /// set has none: Fwd only); backwards there is no sprint (the Forward key is held), so Run Back.
        /// </summary>
        public float Motion(HeroGait gait, HeroMoveDir dir)
        {
            switch (gait)
            {
                case HeroGait.Sprint:
                    return dir == HeroMoveDir.Fwd ? SprintFwd : dir == HeroMoveDir.Side ? (SprintSide > 0 ? SprintSide : RunSide) : RunBack;
                case HeroGait.Run:
                    return dir == HeroMoveDir.Fwd ? RunFwd : dir == HeroMoveDir.Side ? RunSide : RunBack;
                default:
                    return dir == HeroMoveDir.Fwd ? WalkFwd : dir == HeroMoveDir.Side ? WalkSide : WalkBack;
            }
        }

        /// <summary>The unarmed motion the controller's own speed field stands for (sprint sideways/back: the Run fields).</summary>
        float Base(HeroGait gait, HeroMoveDir dir) =>
            gait == HeroGait.Sprint && dir != HeroMoveDir.Fwd ? Motion(HeroGait.Run, dir) : Motion(gait, dir);

        /// <summary>
        /// Factor on the controller's unarmed speed for the drawn <paramref name="hands"/> set: hands motion / unarmed motion.
        /// 1 when either motion is missing: a set without Sprint (bow, crossbow, torch alone) keeps the unarmed sprint speed
        /// [interp: G3 sets movement mode 5 in PS_Ranged_Walk_Loop too; which motion the engine falls back to is not traced].
        /// </summary>
        public static float Factor(HeroGaitSpeeds hands, HeroGaitSpeeds unarmed, HeroGait gait, HeroMoveDir dir)
        {
            if (gait == HeroGait.Sneak) return 1f;
            float h = hands.Motion(gait, dir), b = unarmed.Base(gait, dir);
            return h > 0 && b > 0 ? h / b : 1f;
        }
    }

    /// <summary>
    /// Facing and gait of the player hero (G3 CP 1.75; Docs/hero.md "Facing and gait").
    /// <list type="bullet">
    /// <item><b>Facing.</b> <c>gCCharacterControl_PS::ProcessMovements</c>: with movement constraint 0 the body is aligned to
    /// the camera's forward every frame; the movement keys are camera-relative, so a side or back key strafes or steps
    /// back with the Left/Right/Back motions and never turns the body. Only the combat alignment (constraint 360: melee
    /// moves, the block with a focus, spell casts) points the body elsewhere. PS_Ranged never aligns.</item>
    /// <item><b>Gait while aiming.</b> PS_Ranged_Aim (and PS_Ranged re-aiming, PS_Ranged_Reload with the button held) calls
    /// FUN_10001cf0: ani state Stand, <c>SetMovementMode(3)</c> = walk. The aim states do not run the gait update
    /// (FUN_10002130: walk toggle, run, sprint), so the hero walks until PS_Ranged_Walk_Loop. The bow and crossbow Walk
    /// motions (150 cm/s) keep the upper body in the archer pose in every direction, the Run motions twist it, so the
    /// aimed bow stays on the crosshair only while walking.</item>
    /// </list>
    /// </summary>
    public static class HeroMoveRules
    {
        /// <summary>Longest controller sub-step, s: one CharacterController.Move per sub-step keeps a move short (≤ 0.6 m at 6 m/s).</summary>
        public const float MaxStepSeconds = 0.1f;
        /// <summary>Longest frame the controller makes up for (Unity's default Time.maximumDeltaTime); beyond it time is lost, as in Time.time.</summary>
        public const float MaxFrameSeconds = 1f / 3f;

        /// <summary>
        /// How many sub-steps of at most <see cref="MaxStepSeconds"/> a frame of <paramref name="dt"/> seconds needs (fix-48:
        /// the old cut to 0.1 s lost the rest of a long frame, so the hero covered less ground than his motion speed).
        /// </summary>
        public static int SubSteps(float dt)
        {
            if (!(dt > 0f)) return 1;
            float t = Math.Min(dt, MaxFrameSeconds);
            return Math.Max(1, (int)Math.Ceiling(t / MaxStepSeconds - 1e-4));
        }

        /// <summary>The state may point the body at an alignment target (else it always faces the camera).</summary>
        public static bool CanAlign(HeroMoveState state)
        {
            switch (state)
            {
                case HeroMoveState.Melee:
                case HeroMoveState.MeleeParade:
                case HeroMoveState.MeleeMove:
                case HeroMoveState.Magic:
                case HeroMoveState.MagicCast:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Where the body faces in <paramref name="state"/> while the combat alignment is <paramref name="aligned"/>.</summary>
        public static HeroFacing Facing(HeroMoveState state, bool aligned) =>
            aligned && CanAlign(state) ? HeroFacing.AlignTarget : HeroFacing.Camera;

        /// <summary>The body yaw: the alignment yaw when the state aligns, else the camera yaw. The movement keys never turn it.</summary>
        public static float BodyYaw(HeroMoveState state, float cameraYaw, float? alignYaw) =>
            Facing(state, alignYaw.HasValue) == HeroFacing.AlignTarget ? alignYaw.Value : cameraYaw;

        /// <summary>Aiming forces walking (no run, no sprint).</summary>
        public static bool ForcesWalk(HeroMoveState state) => state == HeroMoveState.RangedAim;

        /// <summary>The gait of the keys: <paramref name="wanted"/> (walk toggle, sprint key, sneaking) unless the state forces walking.</summary>
        public static HeroGait Gait(HeroMoveState state, HeroGait wanted) =>
            ForcesWalk(state) && wanted != HeroGait.Sneak ? HeroGait.Walk : wanted;

        /// <summary>
        /// The state runs G3's gait update (<c>FUN_10002130</c>: walk, run, sprint): PS_Normal_Loop, PS_Melee_Loop,
        /// PS_Magic_Loop, PS_Ranged_Walk_Loop and the sneak states (they set the sneak mode). The move states (PS_Melee_Attack …,
        /// PS_Magic_Cast …) keep the mode of the loop they started from; aiming sets walk (<see cref="ForcesWalk"/>).
        /// </summary>
        public static bool UpdatesGait(HeroMoveState state) => state != HeroMoveState.MeleeMove && state != HeroMoveState.MagicCast;

        /// <summary>CP 1.75 <c>FUN_10002130</c>: a sprint starts only with more than 20 SP (<c>GetStaminaPoints</c> &gt;= 0x15).</summary>
        public const int SprintStartStamina = 21;

        /// <summary>
        /// What a press of the Walk / Sprint key (G3 <c>OnPlayerWalk</c>) means: while the hero stands (or brakes) it walks,
        /// while he moves it sprints; the flag holds until the key is released (then both are cleared).
        /// </summary>
        public static WalkKeyFlag WalkKeyPress(bool moving) => moving ? WalkKeyFlag.Sprint : WalkKeyFlag.Walk;

        /// <summary>
        /// The gait of the movement keys, G3 <c>FUN_10002130</c> (CP 1.75, run by every loop state, drawn or not):
        /// sneaking first; the walk flag (Walk key pressed standing, or the walk toggle) walks; the sprint flag (Walk key
        /// pressed moving) with the Forward key held sprints when SP &gt;= 21, or SP &gt;= 1 while already sprinting; else run.
        /// The hands (weapon, fists, bow, spell) do not matter: only their motion speeds differ (<see cref="HeroGaitSpeeds"/>).
        /// </summary>
        public static HeroGait KeysGait(bool sneaking, bool walk, bool sprintFlag, bool forwardKey, bool sprinting, int stamina)
        {
            if (sneaking) return HeroGait.Sneak;
            if (walk) return HeroGait.Walk;
            if (sprintFlag && forwardKey && (stamina >= SprintStartStamina || (stamina >= 1 && sprinting))) return HeroGait.Sprint;
            return HeroGait.Run;
        }

        /// <summary>
        /// The locomotion motions for camera-relative keys (x right, y forward; the body keeps facing the camera): the
        /// forward or back motion and the side motion, each with its share (|y| and |x| of |x| + |y|, as the locomotion
        /// selector weights them). Null direction = no share. Back + Left = Back and Left, never a turn.
        /// </summary>
        public static (string along, float alongShare, string side, float sideShare) Motions(float x, float y)
        {
            float ax = Math.Abs(x), ay = Math.Abs(y), sum = ax + ay;
            if (sum < 1e-4f) return (null, 0f, null, 0f);
            string along = ay > 0 ? (y > 0 ? "Fwd" : "Back") : null;
            string side = ax > 0 ? (x < 0 ? "Left" : "Right") : null;
            return (along, ay / sum, side, ax / sum);
        }
    }
}
