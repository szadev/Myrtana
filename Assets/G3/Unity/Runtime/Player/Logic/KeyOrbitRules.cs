using System;
using System.Linq;

namespace G3.Player
{
    /// <summary>
    /// The port's option for the "Camera rotation left / right" keys (G3 default Q / E; fix-73, Docs/controls-options.md
    /// section 7). G3 turns the hero with them (<c>G3Player.keyTurnSpeed</c>, 150°/s); with the option on they orbit the
    /// third-person camera round the hero's pivot instead and the hero keeps facing. Pure rules (tested in Tools/G3Tests):
    /// <list type="bullet">
    /// <item>when the keys orbit (<see cref="Orbits"/>): only out of combat (no weapon, bow, spell or fists drawn: aiming
    /// and the lock-on keep G3's turn), in third person, not while looking behind;</item>
    /// <item>the orbit offset (<see cref="Step"/>): turns at <see cref="Speed"/> while a key is held, stays where it is
    /// while the hero stands (to look at him from the front or the side), eases back behind him once he moves or the keys
    /// stop orbiting (weapon drawn, first person, a HUD page) — as G3's camera swings back behind a moving hero.</item>
    /// </list>
    /// </summary>
    public static class KeyOrbitRules
    {
        /// <summary>Orbit speed while a key is held, degrees per second [interp: between G3's 150°/s hero turn and a slow pan].</summary>
        public const float Speed = 110f;

        /// <summary>Time constant of the swing back behind the hero, s (the camera's orbit return, G3PlayerCamera.orbitReturnTime).</summary>
        public const float ReturnTime = 0.35f;

        /// <summary>
        /// Whether the turn keys orbit the camera now: the option is on, the hero is out of combat
        /// (<see cref="HeroMoveState.Normal"/>), the camera is third person and not looking behind.
        /// </summary>
        public static bool Orbits(bool option, HeroMoveState state, bool thirdPerson, bool lookingBehind) =>
            option && state == HeroMoveState.Normal && thirdPerson && !lookingBehind;

        /// <summary>
        /// The next orbit offset (degrees, −180…180) from <paramref name="offset"/>: <paramref name="keyTurn"/> (−1 left,
        /// +1 right, 0 none) turns it at <see cref="Speed"/>; without keys it holds, or with <paramref name="returnBehind"/>
        /// eases back to 0 (critically damped, <paramref name="velocity"/> kept between frames).
        /// </summary>
        public static float Step(float offset, float keyTurn, bool returnBehind, float dt, ref float velocity)
        {
            if (dt <= 0f) return offset;
            if (keyTurn != 0f)
            {
                velocity = 0f;
                return Wrap(offset + Math.Max(-1f, Math.Min(1f, keyTurn)) * Speed * dt);
            }
            if (!returnBehind) { velocity = 0f; return offset; }
            if (offset == 0f) { velocity = 0f; return 0f; }
            float next = CameraBoom.SmoothDamp(Wrap(offset), 0f, ref velocity, ReturnTime, dt);
            if (Math.Abs(next) < 0.05f) { velocity = 0f; return 0f; }
            return next;
        }

        /// <summary>Degrees wrapped into −180…180.</summary>
        public static float Wrap(float degrees)
        {
            float d = degrees % 360f;
            if (d > 180f) d -= 360f;
            else if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>
        /// The keys bound to "Camera rotation left / right" for the option's label ("Q / E" by default; a rebinding shows
        /// the new keys, mouse axes left out). A side without a key shows "-".
        /// </summary>
        public static string KeysText(KeyMap keys)
        {
            string Name(SessionKey key)
            {
                var input = keys?.Inputs(key).FirstOrDefault(i => !i.IsNone && !i.IsMouseAxis) ?? KeyInput.None;
                return input.IsNone ? "-" : input.Name;
            }
            return Name(SessionKey.RotateCamLeft) + " / " + Name(SessionKey.RotateCamRight);
        }
    }
}
