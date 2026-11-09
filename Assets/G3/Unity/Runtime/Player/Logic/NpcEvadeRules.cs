using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>A character an NPC walk may meet: centre x/z, footprint radius (m), whether it moves now, an id.</summary>
    public readonly struct EvadeBody
    {
        public readonly float X, Z, Radius;
        public readonly bool Moving, IsPlayer;
        public readonly int Id;

        public EvadeBody(int id, float x, float z, float radius, bool moving, bool isPlayer)
        {
            Id = id;
            X = x;
            Z = z;
            Radius = radius;
            Moving = moving;
            IsPlayer = isPlayer;
        }
    }

    /// <summary>
    /// G3 gCNavigation_PS.NPCEvadeSwitch: which body the NPC goes round and on which side (kept per body, so a second
    /// contact with the same one keeps the side: no ping-pong), plus the inter pose it heads for now.
    /// </summary>
    public struct EvadeState
    {
        public bool Active;
        public int EvadedId;
        /// <summary>+1 = round the left of the body (seen along the walk), −1 = round its right; 0 = none yet.</summary>
        public int Side;
        public float ToX, ToZ;
        /// <summary>Where the evaded body stood when the inter pose was chosen.</summary>
        public float BodyX, BodyZ;
        public float Seconds;
    }

    public enum EvadeStep
    {
        /// <summary>Nobody in the way: walk on to the path point.</summary>
        None,
        /// <summary>Walk to the inter pose beside the body (<see cref="EvadeState.ToX"/>/<see cref="EvadeState.ToZ"/>).</summary>
        Detour,
        /// <summary>Someone stands on the goal, or there is no free side: stop at contact and wait (no shove).</summary>
        Wait,
    }

    /// <summary>
    /// NPCs step round characters that stand in their way (fix-93). G3 CP 1.75 [Game.dll]:
    /// <list type="bullet">
    /// <item>gCNavigationAdmin::RunDynamicNavigation (0x10121530) computes every walking NPC's wanted pose for the frame
    /// (CalcNewWantedPose) and CheckAllControlledDCCs (0x10121190) tests it against the player's and the other NPCs'
    /// dynamic collision circles (same nav zone; never the entity the NPC walks to, +0x150): closer than the sum of the
    /// radii → gCNavigationMap::CorrectDCCPositions (0x10132a10).</item>
    /// <item>CorrectDCCPositions: a body moves when it covers more than 10 cm/s. Both moving: the NPCs pass each other
    /// (CalcMoveInterPose); against the player nothing (his own controller collides). A walker against a stander: if the
    /// stander is on the walker's goal (within the radii) the walk stops (StopMovement, Reset, state 6); otherwise
    /// CalcPreferedStandInterPose (0x10107200): an inter pose beside the stander, perpendicular to the walk, at 1.25 × the
    /// sum of the radii from its centre, on the side kept in NPCEvadeSwitch for that stander; the stretch there must be
    /// walkable (TestNPCToCorDCCStretch: nav zone, neg zones, other circles), else the other side (CalcAltStandInterPose),
    /// else the switch is cleared and the NPC is only held by the bodies' collision.</item>
    /// </list>
    /// Characters never shove: an NPC that cannot go round stops at contact until the way is free.
    /// </summary>
    public static class NpcEvadeRules
    {
        /// <summary>G3: a body that covers more than 10 cm per second moves.</summary>
        public const float MovingSpeed = 0.1f;
        /// <summary>CalcPreferedStandInterPose: the inter pose lies 1.25 × (r1 + r2) from the stander's centre.</summary>
        public const float InterPoseFactor = 1.25f;
        /// <summary>The inter pose counts as reached this close (m) [interp].</summary>
        public const float ReachedMetres = 0.25f;
        /// <summary>A detour is re-chosen after this long, or when the stander moved this far (m) [interp].</summary>
        public const float DetourSeconds = 3f, BodyMovedMetres = 0.5f;

        /// <summary>
        /// The step of a walker of <paramref name="radius"/> at (<paramref name="x"/>, <paramref name="z"/>) heading along
        /// (<paramref name="dirX"/>, <paramref name="dirZ"/>, unit) <paramref name="step"/> m this frame towards its
        /// path point, whose final goal is (<paramref name="goalX"/>, <paramref name="goalZ"/>).
        /// <paramref name="walkable"/> tells whether the walker may stand at a point and get there straight (null = yes).
        /// </summary>
        public static EvadeStep Steer(float x, float z, float radius, float dirX, float dirZ, float step, float goalX, float goalZ,
                                      IReadOnlyList<EvadeBody> bodies, ref EvadeState state, float dt,
                                      Func<float, float, bool> walkable = null)
        {
            if (state.Active)
            {
                state.Seconds += dt;
                bool reached = Dist2(x, z, state.ToX, state.ToZ) <= ReachedMetres * ReachedMetres;
                bool stale = state.Seconds >= DetourSeconds || !TryFind(bodies, state.EvadedId, out var held) ||
                             Dist2(held.X, held.Z, state.BodyX, state.BodyZ) > BodyMovedMetres * BodyMovedMetres || held.Moving;
                if (!reached && !stale) return EvadeStep.Detour;
                state.Active = false;
            }
            if (bodies == null || bodies.Count == 0) return EvadeStep.None;
            // The wanted pose of this frame against every circle (CheckAllControlledDCCs): the nearest one in contact.
            float wx = x + dirX * step, wz = z + dirZ * step;
            int hit = -1;
            float best = float.MaxValue;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b.Radius <= 0f) continue;
                float r = radius + b.Radius;
                float d2 = Math.Min(Dist2(wx, wz, b.X, b.Z), Dist2(x, z, b.X, b.Z));
                if (d2 >= r * r || d2 >= best) continue;
                // only bodies ahead of the walk (one behind does not block it)
                if ((b.X - x) * dirX + (b.Z - z) * dirZ <= 0f) continue;
                best = d2;
                hit = i;
            }
            if (hit < 0) return EvadeStep.None;
            var o = bodies[hit];
            // Both moving: NPCs pass each other (not ported: they just walk on); the moving player is held by his own
            // collision, nothing is corrected for him (CorrectDCCPositions returns for the player's circle).
            if (o.Moving) return o.IsPlayer ? EvadeStep.Wait : EvadeStep.None;
            float contact = radius + o.Radius;
            // The stander is on the walker's goal: the walk stops there (G3 StopMovement + Reset).
            if (Dist2(goalX, goalZ, o.X, o.Z) < contact * contact) return EvadeStep.Wait;
            int side = state.EvadedId == o.Id && state.Side != 0 ? state.Side : PreferredSide(x, z, dirX, dirZ, o);
            for (int attempt = 0; attempt < 2; attempt++, side = -side)
            {
                InterPose(o, contact, dirX, dirZ, side, out float px, out float pz);
                if (walkable != null && !walkable(px, pz)) continue;
                if (Blocked(bodies, hit, px, pz, radius)) continue;
                state.Active = true;
                state.EvadedId = o.Id;
                state.Side = side;
                state.ToX = px;
                state.ToZ = pz;
                state.BodyX = o.X;
                state.BodyZ = o.Z;
                state.Seconds = 0f;
                return EvadeStep.Detour;
            }
            // No free side: the switch is cleared (G3) and the bodies hold the walker.
            state.EvadedId = 0;
            state.Side = 0;
            return EvadeStep.Wait;
        }

        /// <summary>
        /// The side that needs the shorter way round: the walker passes on the side of the walk line it is already on
        /// (the stander to its right → round its left); exactly on the line: its right [interp: G3's side test].
        /// </summary>
        public static int PreferredSide(float x, float z, float dirX, float dirZ, EvadeBody o)
        {
            // cross(dir, body - walker) > 0: the body is to the walker's left → go round its right (−1).
            float cross = dirX * (o.Z - z) - dirZ * (o.X - x);
            return cross > 1e-4f ? -1 : cross < -1e-4f ? 1 : -1;
        }

        /// <summary>The point beside <paramref name="o"/>: its centre plus the walk's perpendicular × side × 1.25 × contact.</summary>
        public static void InterPose(EvadeBody o, float contact, float dirX, float dirZ, int side, out float px, out float pz)
        {
            float d = contact * InterPoseFactor;
            // left of the walk = (−dirZ, dirX)
            px = o.X + -dirZ * side * d;
            pz = o.Z + dirX * side * d;
        }

        static bool Blocked(IReadOnlyList<EvadeBody> bodies, int except, float px, float pz, float radius)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                if (i == except) continue;
                float r = radius + bodies[i].Radius;
                if (Dist2(px, pz, bodies[i].X, bodies[i].Z) < r * r) return true;
            }
            return false;
        }

        static bool TryFind(IReadOnlyList<EvadeBody> bodies, int id, out EvadeBody body)
        {
            if (bodies != null)
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i].Id == id)
                    {
                        body = bodies[i];
                        return true;
                    }
            body = default;
            return false;
        }

        static float Dist2(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx, dz = az - bz;
            return dx * dx + dz * dz;
        }
    }
}
