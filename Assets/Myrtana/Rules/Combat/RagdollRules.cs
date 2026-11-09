namespace Myrtana.Rules
{
    /// <summary>The push a ragdoll gets when it starts: horizontal speed away from the killer and upward lift, m/s.</summary>
    public readonly struct RagdollImpulse
    {
        public RagdollImpulse(float speed, float lift)
        {
            Speed = speed;
            Lift = lift;
        }

        public float Speed { get; }
        public float Lift { get; }
        public static RagdollImpulse None => new RagdollImpulse(0f, 0f);
    }

    /// <summary>
    /// Death as G3 CP 1.75 shows it (Docs/combat.md §5.1, combat-6) [ZS_RagDollDead, ZS_Dead, ZS_PiercedDead]:
    /// <list type="bullet">
    /// <item>a lethal hit on a standing actor, burning, poison and fatal falls: <b>ragdoll</b> (ZS_RagDollDead: physics
    /// with an impulse from the last inflictor; once the body rests it is frozen in that pose);</item>
    /// <item>killing an actor that lies down (the finishing move, a blow on the unconscious): the dying motion
    /// (ZS_RagDollDead redirects to ZS_Dead when the victim is Down);</item>
    /// <item>the assassin's pierce (PiercedDead): its own stumble and the LiePiercedDead pose;</item>
    /// <item>a corpse met again (a loaded game, the view re-entering range): the static LieDead pose.</item>
    /// </list>
    /// Impulses (force / lift in G3 units): melee 15000/150, PowerAttack 17000/180, quick attacks 10000/80, Pierce
    /// 5000/0, Hack 20000/80; projectiles 7000 (bow) / 10000 (crossbow) / 15000 (others, spells), lift 0. Humans get 0.9 of
    /// the lift. The conversion to m/s is <b>[interp]</b>: 15000 → 2.5 m/s, lift 150 → 1.5 m/s.
    /// </summary>
    public static class RagdollRules
    {
        /// <summary>G3 force units per m/s of horizontal speed, and lift units per m/s [interp].</summary>
        public const float ForcePerSpeed = 6000f, LiftPerSpeed = 100f;
        /// <summary>Humans (species 3) get this share of the lift.</summary>
        public const float HumanLiftFactor = 0.9f;

        /// <summary>Whether the death plays as a ragdoll (else the dying motion or the pose).</summary>
        public static bool UsesRagdoll(HitReaction reaction, bool wasDown, bool live) =>
            live && !wasDown && reaction != HitReaction.PiercedDead;

        /// <summary>
        /// fix-202: whether a combat motion's lunge (CombatLunge: the name's metres carried over the motion) moves the body's
        /// origin. Not for a dead body shown as a ragdoll: its dying motion is not shown, and moving the origin dragged the
        /// falling bodies (children of it) 1-1.7 m along the ground while they fell.
        /// </summary>
        public static bool LungeMovesBody(bool dead, bool ragdoll) => !(dead && ragdoll);

        /// <summary>A melee killer's impulse by its action.</summary>
        public static RagdollImpulse Melee(CombatAction action, bool human)
        {
            float force, lift;
            switch (action)
            {
                case CombatAction.PowerAttack: force = 17000; lift = 180; break;
                case CombatAction.QuickAttack:
                case CombatAction.QuickAttackLeft:
                case CombatAction.QuickAttackRight: force = 10000; lift = 80; break;
                case CombatAction.PierceAttack: force = 5000; lift = 0; break;
                case CombatAction.HackAttack: force = 20000; lift = 80; break;
                default: force = 15000; lift = 150; break;
            }
            return Convert(force, lift, human);
        }

        /// <summary>
        /// A death without a known killer (a script's Kill, burning, poison): the default melee push 15000/150 along the
        /// direction of the death hit <b>[interp]</b> (a zero push left a standing body upright until it froze).
        /// </summary>
        public static RagdollImpulse WithoutKiller(bool human) => Melee(CombatAction.Attack, human);

        /// <summary>A projectile's impulse: bow 7000, crossbow 10000, anything else (spells) 15000; no lift.</summary>
        public static RagdollImpulse Projectile(WeaponClass? weapon) =>
            Convert(weapon == WeaponClass.Bow ? 7000 : weapon == WeaponClass.Crossbow ? 10000 : 15000, 0, false);

        static RagdollImpulse Convert(float force, float lift, bool human) =>
            new RagdollImpulse(force / ForcePerSpeed, lift * (human ? HumanLiftFactor : 1f) / LiftPerSpeed);
    }
}
