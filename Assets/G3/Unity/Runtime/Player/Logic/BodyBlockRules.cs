using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>A character body as an upright circle on the ground plane (centre x/z and footprint radius, m).</summary>
    public readonly struct BodyCircle
    {
        public readonly float X, Z, Radius;
        public BodyCircle(float x, float z, float radius) { X = x; Z = z; Radius = radius; }
    }

    /// <summary>
    /// The hero does not walk through standing characters (fix-86). G3 CP 1.75: every character carries a physics
    /// character controller (gCCharacterMovement_PS) and the player's moves are traced against them
    /// (gCCharacterSensor_PS::ProcessPlayerMovements / TestSoftCollisions); a contact only raises the script callback
    /// OnCharacterTouchesCharacter, which does nothing (no shove, no complaint), and collision between two characters is
    /// switched off only for an alignment target during _AI_Goto_Ext and a body turned into an animal (MagicAnimalForm).
    /// Our bodies are trigger capsules (every world query ignores them), so the hero's horizontal move is clipped here:
    /// he stops at contact (hero radius + body footprint) and slides round the body like a capsule. Overlapping already
    /// (the body walked into him) he can step back or aside, never deeper; nobody is pushed.
    /// </summary>
    public static class BodyBlockRules
    {
        /// <summary>Clip passes (a slide round one body can run into the next).</summary>
        const int Passes = 3;

        /// <summary>
        /// Clips the horizontal move (<paramref name="dx"/>, <paramref name="dz"/>) of a body of <paramref name="radius"/>
        /// at (<paramref name="x"/>, <paramref name="z"/>) against <paramref name="bodies"/>. Returns whether a body changed it.
        /// </summary>
        public static bool Clip(float x, float z, float radius, ref float dx, ref float dz, IReadOnlyList<BodyCircle> bodies)
        {
            if (bodies == null || bodies.Count == 0 || (dx == 0f && dz == 0f)) return false;
            bool clipped = false;
            for (int pass = 0; pass < Passes; pass++)
            {
                bool any = false;
                for (int i = 0; i < bodies.Count; i++)
                    if (ClipOne(x, z, radius, ref dx, ref dz, bodies[i])) any = clipped = true;
                if (!any) return clipped;
            }
            // Still into a body after the passes (wedged between two): stand.
            for (int i = 0; i < bodies.Count; i++)
            {
                if (!EndsDeeper(x, z, radius, dx, dz, bodies[i])) continue;
                dx = dz = 0f;
                return true;
            }
            return clipped;
        }

        /// <summary>Contact distance between the centres of two bodies, m.</summary>
        public static float Contact(float radius, BodyCircle body) => radius + body.Radius;

        /// <summary>
        /// The move comes closer to the body than contact on its way (not only at its end: one long step must not jump
        /// through a thin body); overlapping already: it ends deeper than it started.
        /// </summary>
        static bool Enters(float x, float z, float radius, float dx, float dz, BodyCircle b)
        {
            float r = Contact(radius, b) - 1e-4f;
            float sx = x - b.X, sz = z - b.Z;
            float s2 = sx * sx + sz * sz;
            float ex = sx + dx, ez = sz + dz;
            float e2 = ex * ex + ez * ez;
            if (s2 < r * r) return e2 < s2 - 1e-8f;
            float a = dx * dx + dz * dz;
            float t = a > 0f ? -(sx * dx + sz * dz) / a : 0f;
            if (t <= 0f) return false; // moving away or along
            if (t > 1f) t = 1f;
            float cx = sx + dx * t, cz = sz + dz * t;
            return cx * cx + cz * cz < r * r;
        }

        /// <summary>The move ends inside contact and closer to the body than it started.</summary>
        static bool EndsDeeper(float x, float z, float radius, float dx, float dz, BodyCircle b)
        {
            float r = Contact(radius, b) - 2e-3f;
            float sx = x - b.X, sz = z - b.Z, ex = sx + dx, ez = sz + dz;
            float e2 = ex * ex + ez * ez;
            return e2 < r * r && e2 < sx * sx + sz * sz - 1e-8f;
        }

        static bool ClipOne(float x, float z, float radius, ref float dx, ref float dz, BodyCircle b)
        {
            if (b.Radius <= 0f || !Enters(x, z, radius, dx, dz, b)) return false;
            float r = Contact(radius, b);
            float sx = x - b.X, sz = z - b.Z;
            float s2 = sx * sx + sz * sz;
            if (s2 < r * r)
            {
                // Overlapping already: only the part of the move that goes deeper is taken away.
                float s = (float)Math.Sqrt(s2);
                if (s < 1e-5f) return false; // on the same spot: any direction frees him
                float nx = sx / s, nz = sz / s;
                float into = dx * nx + dz * nz;
                if (into >= 0f) return false;
                dx -= nx * into;
                dz -= nz * into;
                return true;
            }
            // Outside: up to the contact point, the rest slides along the body (tangent at contact).
            float a = dx * dx + dz * dz;
            float bb = 2f * (sx * dx + sz * dz);
            float c = s2 - r * r;
            float disc = bb * bb - 4f * a * c;
            float t = disc <= 0f ? 0f : (-bb - (float)Math.Sqrt(disc)) / (2f * a);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            float cx = sx + dx * t, cz = sz + dz * t;
            float cl = (float)Math.Sqrt(cx * cx + cz * cz);
            float mx = cl > 1e-5f ? cx / cl : 0f, mz = cl > 1e-5f ? cz / cl : 0f;
            float rx = dx * (1f - t), rz = dz * (1f - t);
            float inward = rx * mx + rz * mz;
            if (inward < 0f) { rx -= mx * inward; rz -= mz * inward; }
            dx = dx * t + rx;
            dz = dz * t + rz;
            return true;
        }
    }
}
