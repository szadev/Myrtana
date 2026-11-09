using System;
using System.IO;
using G3.Player;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The world's water zones at runtime (Docs/hero.md "Water and swimming"): the G3 gCWaterZone_PS planes and
    /// footprints imported by <c>G3WaterImporter</c> into <c>Resources/G3Water/G3WaterZones.bytes</c>
    /// (<see cref="WaterMapFile"/>). Water meshes have no colliders, so floor rays pass through the surface.
    /// Without the import there is no water (<see cref="Available"/> false): the hero walks on the sea floor as before.
    /// </summary>
    public static class G3Water
    {
        public const string ResourcePath = "G3Water/G3WaterZones";
        /// <summary>Floor rays start this far above the surface and reach this far below it, m.</summary>
        const float RayAbove = 0.3f, RayDepth = 60f;
        /// <summary>Start height above the surface and depth below it of the <see cref="FloorUnder"/> ray (batched casts).</summary>
        public const float FloorRayAbove = RayAbove, FloorRayDepth = RayDepth;

        static WaterMap _map;
        static bool _loaded;

        /// <summary>The imported zones (loaded on first use), or null.</summary>
        public static WaterMap Map
        {
            get
            {
                if (_loaded) return _map;
                _loaded = true;
                var asset = Resources.Load<TextAsset>(ResourcePath);
                if (asset == null)
                {
                    Debug.LogWarning("[G3Water] No water zones imported (menu G3/World/Import Water Zones): no swimming.");
                    return null;
                }
                try
                {
                    using var ms = new MemoryStream(asset.bytes);
                    _map = WaterMapFile.Read(ms);
                }
                catch (Exception ex) { Debug.LogError($"[G3Water] {ex.Message}"); }
                Resources.UnloadAsset(asset);
                return _map;
            }
        }

        public static bool Available => Map != null;

        /// <summary>Tests / tools: use these zones instead of the imported ones (null = load the import again).</summary>
        public static void SetMap(WaterMap map)
        {
            _map = map;
            _loaded = map != null;
        }

        /// <summary>The water surface height over <paramref name="feet"/> (see <see cref="WaterMap.Sample"/>).</summary>
        public static bool TrySurface(Vector3 feet, out float surfaceY, float maxAbove = WaterMap.MaxSurfaceAboveFeet)
        {
            surfaceY = float.NegativeInfinity;
            var map = Map;
            return map != null && map.Sample(feet.x, feet.y, feet.z, out surfaceY, out _, maxAbove);
        }

        /// <summary>Depth of the feet under the surface, m (≤ 0 or NaN: dry).</summary>
        public static float BodyDepth(Vector3 feet) => TrySurface(feet, out float s) ? s - feet.y : float.NaN;

        /// <summary>
        /// Height of the floor under the water at (x, z), cast from just above the surface (ignores triggers and
        /// <paramref name="ignore"/>'s own colliders); −∞ when nothing is hit within 60 m (open sea, cells not loaded).
        /// </summary>
        public static float FloorUnder(Vector3 p, float surfaceY, Transform ignore = null)
        {
            var origin = new Vector3(p.x, surfaceY + RayAbove, p.z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, RayAbove + RayDepth, ~0, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (ignore != null && h.collider.transform.IsChildOf(ignore)) continue;
                if (h.point.y > best) best = h.point.y;
            }
            return best;
        }

        static readonly RaycastHit[] _hits = new RaycastHit[8];

        /// <summary>Water column at a spot, m (surface − floor; +∞ without floor; NaN without water).</summary>
        public static float ColumnDepth(Vector3 p, float maxAbove = WaterMap.MaxSurfaceAboveFeet)
        {
            if (!TrySurface(p, out float s, maxAbove)) return float.NaN;
            return s - FloorUnder(p, s);
        }
    }

    /// <summary>
    /// Named hook for water effects (splashes, waves around a wading or swimming character). The effect player
    /// (effects agent) subscribes to <see cref="Requested"/>; until then nothing is shown. Names are the g3.efm effects G3
    /// uses: <c>eff_ani_dropinwater_01</c> (falling into water; its sound also comes from the Swim Fall_Hit motion),
    /// <c>eff_ani_wade_waves_01</c> / <c>eff_ani_swim_waves_01</c> (spawned on the character while wading / swimming,
    /// gCCharacterMovement_PS::SetEffectsByMode; stopped when the mode ends).
    /// </summary>
    public static class G3WaterEffects
    {
        public const string DropInWater = "eff_ani_dropinwater_01";
        public const string WadeWaves = "eff_ani_wade_waves_01";
        public const string SwimWaves = "eff_ani_swim_waves_01";

        public enum Kind { OneShot, Start, Stop }

        /// <summary>(kind, g3.efm effect name, position at the water surface, character the effect follows).</summary>
        public static event Action<Kind, string, Vector3, Transform> Requested;

        public static void Play(string effect, Vector3 position, Transform follow = null) => Requested?.Invoke(Kind.OneShot, effect, position, follow);
        public static void Start(string effect, Vector3 position, Transform follow) => Requested?.Invoke(Kind.Start, effect, position, follow);
        public static void Stop(string effect, Transform follow) => Requested?.Invoke(Kind.Stop, effect, follow != null ? follow.position : Vector3.zero, follow);
    }
}
