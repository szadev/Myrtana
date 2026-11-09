namespace G3.Player
{
    /// <summary>
    /// Pure rules for where an NPC body's feet go on one walker step (no engine types; tested in Tools/G3Tests), fix-60.
    /// G3 moves NPCs in the processing range as PhysX characters (gCCharacterMovement_PS): gravity keeps the feet on
    /// collision, a floor within <see cref="SlopeRules.StepHeight"/> above is stepped onto, and with no floor below the body
    /// falls. Only outside the processing range does G3 slide a body straight at its target's height
    /// (gCNavigation_PS::RunSimpleMovement), where nobody sees it. Our walker probes the floor with a short ray; before
    /// fix-60 a miss carried the path corner's or the G3 route point's height on, so Faring guards walked off the round
    /// tower's top into the air and over the street at the height of their next point.
    /// </summary>
    public static class NpcFootRules
    {
        /// <summary>
        /// The feet's height after one step, or false: the step is refused (the body stays where it is; the walker's stuck
        /// rule lets the caller decide, which puts a routine NPC at its point as G3 does with NPCs that cannot reach it).
        /// </summary>
        /// <param name="fromY">Feet height before the step.</param>
        /// <param name="hasFloor">The floor probe under the new spot hit collision.</param>
        /// <param name="floorY">That floor's height.</param>
        /// <param name="direct">A leg along a G3 nav route (the designer's path may go down any slope: no ledge refusal).</param>
        /// <param name="overWater">The body treats water as solid (bloodfly): it hovers on, the water surface decides.</param>
        /// <param name="maxDown">Deepest step down on a NavMesh walk (m).</param>
        /// <param name="carriedY">Height a body over water keeps when nothing is below it within the probe.</param>
        public static bool TryStep(float fromY, bool hasFloor, float floorY, bool direct, bool overWater, float maxDown, float carriedY, out float y)
        {
            if (overWater)
            {
                y = hasFloor ? floorY : carriedY;
                return true;
            }
            y = fromY;
            if (!hasFloor) return false;                        // no floor: G3 bodies do not walk into the air
            if (!direct && floorY < fromY - maxDown) return false; // a ledge the NavMesh walk does not go down
            y = floorY;
            return true;
        }
    }
}
