using System;

namespace G3.Player
{
    /// <summary>
    /// Walking while the block is held (G3 CP 1.75). PS_Melee_Parade only starts <c>_AI_Parade</c>, aligns the body to
    /// the focus (movement constraint 360) and returns to PS_Melee_Loop, which keeps processing the movement keys, so the
    /// hero moves in the Parade stance with its own motion set (<c>Hero_Parade_None_1H_P0_Move_…</c>, likewise 2H, Fist,
    /// Staff, 1H+Shield): Walk Fwd/Back 350, Walk Left/Right 200, Run Left/Right 400 cm/s, Stand, Turn90 — no Run
    /// Fwd/Back and no Sprint. Speeds here are those motion speeds (m/s), as for the standing set (Docs/hero.md).
    /// </summary>
    public static class ParadeMovement
    {
        public const float ForwardSpeed = 3.5f, BackSpeed = 3.5f, SideWalkSpeed = 2.0f, SideRunSpeed = 4.0f;

        /// <summary>Local wish velocity (x right, z forward) for movement keys (clamped to length 1).</summary>
        public static (float x, float z) Wish(float inputX, float inputY, bool run)
        {
            float side = run ? SideRunSpeed : SideWalkSpeed;
            return (inputX * side, inputY * (inputY >= 0 ? ForwardSpeed : BackSpeed));
        }
    }
}
