using System;
using System.Text.Json.Serialization;

namespace Myrtana.Rules
{
    /// <summary>
    /// A lit fire that G3 only shows (fireplaces, braziers, fire basins: a static mesh with a burning <c>gCEffect_PS</c>
    /// and no <c>gCInteraction_PS</c>) which the port lets the hero cook at ([owner] option "Cook on any fire", fix-88;
    /// Docs/interactive-objects.md §11). Lengths in metres in the object's own frame (x = right, y = up, z = forward;
    /// unscaled like the mesh).
    /// </summary>
    public sealed class CookFireDef
    {
        [JsonConstructor]
        public CookFireDef(float[] fire, float[] spot, float reach)
        {
            Fire = fire != null && fire.Length == 3 ? fire : new float[3];
            Spot = spot != null && spot.Length == 3 ? spot : null;
            Reach = reach > 0f ? reach : 0f;
        }

        /// <summary>Where the flames burn (the effect's offset).</summary>
        public float[] Fire { get; }
        /// <summary>
        /// Where the hero sits down to cook, in front of a fireplace's opening (its mesh's front, local −Z, plus a gap);
        /// null for a round fire (brazier, basin), where he sits on his own side at <see cref="Reach"/> from the flames.
        /// </summary>
        public float[] Spot { get; }
        /// <summary>A round fire's distance from the flames to the hero (half its width plus a gap).</summary>
        public float Reach { get; }
    }

    /// <summary>
    /// The rules of the port's cooking fires (fix-88, [owner]). G3 CP 1.75 cooks only at its own stations: the campfire
    /// (PS_Interact_Campfire: the fry page, raw meat or meatbug → fried 1:1) and the cauldron (cooking recipes); the barbecue
    /// is a use loop without a page. Lit fireplaces, fire cages and fire basins are decoration there. With the option on
    /// (the default, [owner]) such a fire is a station that works exactly like a campfire: the same fry page and pairs
    /// (<see cref="StationUseType"/>), the same motion (the hero sits down and fries with meat on a stick: the only hero
    /// cooking motion besides the barbecue's spit crank), one user at a time (no NPC routine point uses them). Off =
    /// original: they cannot be focused or used.
    /// </summary>
    public static class CookFires
    {
        /// <summary>The use type of a port cooking fire (no G3 gEUseType).</summary>
        public const string UseType = "CookFire";
        /// <summary>The G3 station a cooking fire works like.</summary>
        public const string Campfire = "Campfire";
        /// <summary>The gap between a fireplace's front and the hero, m.</summary>
        public const float FrontGap = 0.4f;
        /// <summary>The gap between a round fire's rim and the hero, m.</summary>
        public const float RimGap = 0.45f;
        /// <summary>The tallest round fire the hero can cook at (a fire cage 0.75 m, a Varant fire basin 0.63 m; the 3.5 m
        /// orc fire pelvis is out), m.</summary>
        public const float MaxRoundHeight = 1.2f;

        public static bool IsCookFire(InteractiveObjectDef def) => def != null && def.CookFire != null;

        /// <summary>The station rule the object uses (crafting, motions, props): a cooking fire is a campfire.</summary>
        public static string StationUseType(InteractiveObjectDef def) => IsCookFire(def) ? Campfire : def?.UseType;

        /// <param name="cookOnAnyFire">The option: false = original (cooking fires are decoration).</param>
        public static bool Usable(InteractiveObjectDef def, bool cookOnAnyFire) => cookOnAnyFire || !IsCookFire(def);

        /// <summary>The flames in world space (m).</summary>
        public static float[] FireWorld(InteractiveObjectDef def) => ToWorld(def, def.CookFire.Fire);

        /// <summary>
        /// Where the hero cooks (world m) and his yaw (degrees, 0 = +Z, clockwise seen from above as Unity's): in front of a
        /// fireplace, or on his side of a round fire (<paramref name="heroX"/>, <paramref name="heroZ"/>: where he stands).
        /// </summary>
        public static (float[] position, float yaw) HeroSpot(InteractiveObjectDef def, float heroX, float heroZ)
        {
            var fire = FireWorld(def);
            float[] at;
            if (def.CookFire.Spot != null) at = ToWorld(def, def.CookFire.Spot);
            else
            {
                float dx = heroX - fire[0], dz = heroZ - fire[2], d = (float)Math.Sqrt(dx * dx + dz * dz);
                if (d < 1e-3f) { dx = -def.Forward[0]; dz = -def.Forward[2]; d = (float)Math.Sqrt(dx * dx + dz * dz); }
                if (d < 1e-3f) { dx = 0f; dz = -1f; d = 1f; }
                float reach = def.CookFire.Reach * def.Scale;
                at = new[] { fire[0] + dx / d * reach, def.Position[1], fire[2] + dz / d * reach };
            }
            float yaw = (float)(Math.Atan2(fire[0] - at[0], fire[2] - at[2]) * 180.0 / Math.PI);
            return (at, yaw);
        }

        /// <summary>A local point of the object's frame (x right = up × forward, y up, z forward; scaled) in world space.</summary>
        public static float[] ToWorld(InteractiveObjectDef def, float[] local)
        {
            var f = def.Forward;
            var u = def.Up;
            var r = Normalize(new[] { u[1] * f[2] - u[2] * f[1], u[2] * f[0] - u[0] * f[2], u[0] * f[1] - u[1] * f[0] });
            float s = def.Scale;
            var w = new float[3];
            for (int i = 0; i < 3; i++)
                w[i] = def.Position[i] + (r[i] * local[0] + u[i] * local[1] + f[i] * local[2]) * s;
            return w;
        }

        static float[] Normalize(float[] v)
        {
            float l = (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l > 1e-6f ? new[] { v[0] / l, v[1] / l, v[2] / l } : new float[] { 1, 0, 0 };
        }

        // ------------------------------------------------------------------------------------------- import (G3 data)

        /// <summary>
        /// The cooking fire of a decorative G3 entity, or null: a static mesh (stem <paramref name="mesh"/>, bounds in cm)
        /// that burns (<paramref name="effect"/>, its gCEffect_PS, offset in cm). Fireplaces (mesh name contains
        /// "Fireplace", effect <c>eff_env_fire_fireplace_*</c>; CP 1.75: Fireplace_02/03/04/Large_01, 84) open to local −Z
        /// (the chimney stands at the wall, z = 0); round fires (effect <c>eff_env_fire_campfire_*</c> or
        /// <c>eff_env_fire_bowl_*</c> on a mesh at most <see cref="MaxRoundHeight"/> tall: Fire_Cage_01 451,
        /// Varant_Firebasin_01 81). Cold ones (no effect: Fireplace_Blacksmith_01, Fireplace_Hood_01) and torches, candles,
        /// the orc fire pelvises stay decoration.
        /// </summary>
        public static CookFireDef Classify(string mesh, string effect, float[] offsetCm, float[] boundsMinCm, float[] boundsMaxCm)
        {
            if (string.IsNullOrEmpty(mesh) || string.IsNullOrEmpty(effect) || boundsMinCm == null || boundsMaxCm == null) return null;
            var off = offsetCm ?? new float[3];
            var fire = new[] { off[0] * 0.01f, off[1] * 0.01f, off[2] * 0.01f };
            bool fireplaceEffect = effect.StartsWith("eff_env_fire_fireplace", StringComparison.OrdinalIgnoreCase);
            if (mesh.IndexOf("Fireplace", StringComparison.OrdinalIgnoreCase) >= 0)
                return fireplaceEffect
                    ? new CookFireDef(fire, new[] { fire[0], 0f, boundsMinCm[2] * 0.01f - FrontGap }, 0f)
                    : null;
            bool roundEffect = effect.StartsWith("eff_env_fire_campfire", StringComparison.OrdinalIgnoreCase) ||
                               effect.StartsWith("eff_env_fire_bowl", StringComparison.OrdinalIgnoreCase);
            float height = (boundsMaxCm[1] - boundsMinCm[1]) * 0.01f;
            if (!roundEffect || height > MaxRoundHeight || height <= 0f) return null;
            float half = 0f;
            for (int i = 0; i < 3; i += 2) half = Math.Max(half, Math.Max(Math.Abs(boundsMinCm[i]), Math.Abs(boundsMaxCm[i])) * 0.01f);
            return new CookFireDef(fire, null, half + RimGap);
        }
    }
}
