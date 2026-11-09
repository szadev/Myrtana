using Myrtana.Rules;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Placeholder looks of magic until the G3 effects (g3.efm particle and light commands) are imported: a coloured point
    /// light with a few additive particles for spells in the hand, missiles, impacts and status conditions. Kept small
    /// (≤ 60 particles, one light each). The colour follows the damage type or the spell's deity.
    /// </summary>
    public static class SpellFx
    {
        static Material s_material;

        public static readonly Color Fire = new Color(1f, 0.45f, 0.1f), Ice = new Color(0.45f, 0.8f, 1f), Lightning = new Color(0.75f, 0.6f, 1f),
                                     Heal = new Color(1f, 0.9f, 0.45f), Summon = new Color(0.6f, 0.25f, 0.9f), Neutral = new Color(0.9f, 0.9f, 1f);

        /// <summary>The colour of a spell: its missile's damage type, else healing gold, summoning violet, or by deity.</summary>
        public static Color ColorOf(SpellDef spell)
        {
            if (spell == null) return Neutral;
            if (spell.Missile != null) return ColorOf(spell.Missile.DamageType);
            if (spell.Effect != null && (spell.Effect.Contains("Heal") || spell.Effect.Contains("Cure") || spell.Effect.Contains("Refresh"))) return Heal;
            if (spell.Kind == SpellKind.Summon || (spell.Effect?.StartsWith("MagicSummon") ?? false)) return Summon;
            switch (spell.Deity)
            {
                case SpellDeity.Innos: return Fire;
                case SpellDeity.Adanos: return Ice;
                case SpellDeity.Beliar: return Lightning;
                default: return Neutral;
            }
        }

        public static Color ColorOf(DamageType type) =>
            type == DamageType.Fire ? Fire : type == DamageType.Ice ? Ice : type == DamageType.Lightning ? Lightning : Neutral;

        /// <summary>A glowing ball (spell in the hand, missile): a light and a small particle swarm under <paramref name="parent"/>.</summary>
        public static GameObject Orb(Transform parent, Color color, float radius, float intensity = 2f)
        {
            var go = new GameObject("Spell orb");
            go.transform.SetParent(parent, false);
            AddLight(go, color, radius * 12f, intensity);
            var ps = AddParticles(go, color, radius, 40, rate: 60, speed: 0.15f, life: 0.4f, world: false);
            ps.Play();
            return go;
        }

        /// <summary>A short flash at an impact or a cast: light and a burst of particles, removed after a moment.</summary>
        public static void Burst(Vector3 position, Color color, float radius = 0.6f)
        {
            var go = new GameObject("Spell burst");
            go.transform.position = position;
            var light = AddLight(go, color, radius * 10f, 4f);
            var ps = AddParticles(go, color, radius * 0.3f, 60, rate: 0, speed: radius * 4f, life: 0.6f, world: true);
            ps.Emit(50);
            go.AddComponent<FadeOut>().Init(light, 0.7f);
        }

        /// <summary>A condition on a body (burning, frozen, a heal): a glow with rising particles until removed.</summary>
        public static GameObject Aura(Transform parent, Vector3 localCenter, Color color, float height)
        {
            var go = new GameObject("Status aura");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCenter;
            AddLight(go, color, 3f, 1.5f);
            var ps = AddParticles(go, color, height * 0.3f, 50, rate: 40, speed: 0.6f, life: 0.8f, world: true);
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(0.5f, height, 0.5f);
            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.y = new ParticleSystem.MinMaxCurve(0.8f);
            ps.Play();
            return go;
        }

        static Light AddLight(GameObject go, Color color, float range, float intensity)
        {
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            return light;
        }

        static ParticleSystem AddParticles(GameObject go, Color color, float radius, int max, float rate, float speed, float life, bool world)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = rate > 0;
            main.playOnAwake = false;
            main.maxParticles = max;
            main.startLifetime = life;
            main.startSpeed = speed;
            main.startSize = Mathf.Max(0.05f, radius * 0.8f);
            main.startColor = color;
            main.simulationSpace = world ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            var emission = ps.emission;
            emission.rateOverTime = rate;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.01f, radius);
            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(color, 1) },
                             new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            fade.color = gradient;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Material();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }

        /// <summary>Additive unlit particles (URP).</summary>
        static Material Material()
        {
            if (s_material != null) return s_material;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            s_material = new Material(shader) { name = "Spell particles (placeholder)" };
            s_material.mainTexture = SoftDot();
            if (s_material.HasProperty("_Surface"))
            {
                s_material.SetFloat("_Surface", 1); // transparent
                s_material.SetFloat("_Blend", 2);   // additive
                s_material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                s_material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                s_material.SetInt("_ZWrite", 0);
                s_material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                s_material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            return s_material;
        }

        /// <summary>A round soft dot (white, alpha falling off to the rim) for the particles.</summary>
        static Texture2D SoftDot()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Spell dot", wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float a = Mathf.Clamp01(1 - Mathf.Sqrt(dx * dx + dy * dy));
                tex.SetPixel(x, y, new Color(1, 1, 1, a * a));
            }
            tex.Apply();
            return tex;
        }

        /// <summary>Dims a burst's light and removes the burst.</summary>
        sealed class FadeOut : MonoBehaviour
        {
            Light _light;
            float _seconds, _start, _intensity;

            public void Init(Light light, float seconds)
            {
                _light = light;
                _seconds = seconds;
                _start = Time.time;
                _intensity = light.intensity;
            }

            void Update()
            {
                float t = (Time.time - _start) / _seconds;
                if (_light != null) _light.intensity = _intensity * Mathf.Clamp01(1 - t);
                if (t >= 1.5f) Destroy(gameObject);
            }
        }
    }
}
