namespace Myrtana.Rules
{
    /// <summary>G3 gEProjectilePath (Game.dll strings and SDK GameEnum.h): the flight model of a gCProjectile_PS.</summary>
    public enum ProjectilePathStyle { Line = 0, Curve = 1, Physics = 2, Missile = 3 }

    /// <summary>
    /// How a shot flies after release (fix-93). G3 [CP 1.75]:
    /// <list type="bullet">
    /// <item>_AI_Shoot (Script_Game 0x1004a710) sets TargetDirection to the aim instruction's direction (the target as
    /// aimed at) and PathStyle 2 (Physics) for the player, 0 (Line) for an NPC; no target entity, no lead.</item>
    /// <item>gCProjectile_PS::Shoot (Game 0x10152210) takes GetDirection once (target entity's Spine_Spine_1, else
    /// TargetDirection, else TargetPosition; scaled by ShootVelocity km/h → cm/s) and runs one ShootIteration.</item>
    /// <item>gCProjectile_PS::ShootIteration (Game 0x101523a0) calls GetDirection again only for Missile (every
    /// TargetUpdateMSec); Line and Physics only turn the mesh along the flight; Curve does nothing.</item>
    /// </list>
    /// So an NPC's arrow or bolt is aimed once, at release, and flies straight on: a step aside after release makes it
    /// miss (owner, fix-93: "in the original you could dodge"). Before fix-93 the port re-aimed NPC arrows every frame (combat-6 misread).
    /// Spell missiles (MagicProjectile, Script_Game 0x10052db0) overwrite the template's PathStyle: 3 Missile only when the
    /// cast has a focus AND the caster's action is 45 PowerCast (a power-cast held to full mana), else 0 Line; Shoot then
    /// aims the Line missile once at the focus (or along the caster's forward without one). So a plain fireball, ice lance
    /// … flies straight at where the target stood; only a powered cast homes (fix-159, owner: "the fireball turns after the target").
    /// </summary>
    public static class ProjectileFlight
    {
        /// <summary>
        /// The path style of a shot: spell missiles home (Missile) only when power-cast at a target, else fly Line;
        /// arrows/bolts of NPCs fly Line, the player's Physics.
        /// </summary>
        public static ProjectilePathStyle StyleOf(bool player, bool spell, bool hasTarget, bool powerCast = false)
        {
            if (spell) return hasTarget && powerCast ? ProjectilePathStyle.Missile : ProjectilePathStyle.Line;
            return player ? ProjectilePathStyle.Physics : ProjectilePathStyle.Line;
        }

        /// <summary>
        /// A spell's missile can home at all: only a Power-type spell is ever cast with action PowerCast (G3 GetCastAction;
        /// Immediate spells quick-cast, Regular ones summon), and only a PowerCast missile flies Missile.
        /// </summary>
        public static bool CanHome(SpellDef spell) => spell != null && spell.CastType == SpellCastType.Power;

        /// <summary>The flight is re-aimed at the target after release (G3 ShootIteration: Missile only).</summary>
        public static bool ReAims(ProjectilePathStyle style) => style == ProjectilePathStyle.Missile;

        /// <summary>The rigid body falls under gravity (the player's Physics shots; Line flies straight [interp: no drop seen]).</summary>
        public static bool Falls(ProjectilePathStyle style) => style == ProjectilePathStyle.Physics;

        /// <summary>
        /// The release velocity of a shot at <paramref name="speed"/> m/s from <paramref name="from"/> straight at
        /// <paramref name="aimPoint"/> where the target is now: no lead for its motion (G3 GetDirection at Shoot).
        /// </summary>
        public static P3 ReleaseVelocity(P3 from, P3 aimPoint, float speed)
        {
            var d = aimPoint - from;
            float l = d.Length;
            return l < 1e-6f ? new P3(0, 0, speed) : d * (speed / l);
        }

        /// <summary>The velocity after <paramref name="dt"/> s of a non-homing flight (Line: unchanged; Physics: gravity).</summary>
        public static P3 Advance(ProjectilePathStyle style, P3 velocity, float dt) =>
            Falls(style) ? velocity + new P3(0, -RangedRules.Gravity * dt, 0) : velocity;
    }
}
