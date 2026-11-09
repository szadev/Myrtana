using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The imported g3.efm visuals: effect name → the templates it spawns (gCEffectCommandSpawnEntity) as pooled
    /// <see cref="G3Vfx"/> prefabs. Created by the editor menu <c>G3/Effects/Import Effects</c> at
    /// <see cref="ResourcePath"/> (Resources, so the runtime finds it without scene wiring). Sounds stay with
    /// <see cref="G3EffectSounds"/>. Docs/formats-effects.md §6.
    /// </summary>
    public sealed class G3EffectLibrary : ScriptableObject
    {
        public const string ResourcePath = "G3EffectLibrary";

        public List<G3EffectEntry> effects = new List<G3EffectEntry>();
        [Tooltip("Every imported template prefab by template name (world-placed particles, tools).")]
        public List<G3Vfx> templates = new List<G3Vfx>();
        [Tooltip("Item templates with their own effect, light or projectile effects (torches, magic weapons, fire/explosive arrows).")]
        public List<G3ItemEffectData> items = new List<G3ItemEffectData>();

        Dictionary<string, G3EffectEntry> _byName;
        Dictionary<string, G3Vfx> _templates;
        Dictionary<string, G3ItemEffectData> _items;

        /// <summary>The effect data of an item template (gCEffect_PS, eCPointLight_PS, gCProjectile_PS), or null.</summary>
        public G3ItemEffectData Item(string template)
        {
            if (string.IsNullOrEmpty(template)) return null;
            if (_items == null)
            {
                _items = new Dictionary<string, G3ItemEffectData>(StringComparer.OrdinalIgnoreCase);
                foreach (var i in items) if (i != null && !string.IsNullOrEmpty(i.template)) _items[i.template] = i;
            }
            return _items.TryGetValue(template, out var r) ? r : null;
        }

        public G3EffectEntry Get(string effect)
        {
            if (string.IsNullOrEmpty(effect)) return null;
            if (_byName == null)
            {
                _byName = new Dictionary<string, G3EffectEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in effects) if (e != null && !string.IsNullOrEmpty(e.name)) _byName[e.name] = e;
            }
            return _byName.TryGetValue(effect, out var r) ? r : null;
        }

        public G3Vfx Template(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_templates == null)
            {
                _templates = new Dictionary<string, G3Vfx>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in templates) if (t != null) _templates[t.templateName] = t;
            }
            return _templates.TryGetValue(name, out var r) ? r : null;
        }

        void OnEnable() { _byName = null; _templates = null; _items = null; }
    }

    [Serializable]
    public sealed class G3EffectEntry
    {
        public string name;
        public List<G3EffectSpawnData> spawns = new List<G3EffectSpawnData>();
    }

    /// <summary>One gCEffectCommandSpawnEntity (metres, seconds).</summary>
    [Serializable]
    public sealed class G3EffectSpawnData
    {
        public G3Vfx prefab;
        [Tooltip("gEEffectLink: 0 independent (stays where spawned), 1 target entity (follows), 2 target bone")]
        public int link;
        public string bone;
        [Tooltip("Dies with the effect (looping parts stop when the effect is stopped).")] public bool autoKill;
        public Vector3 offsetMin, offsetMax;
        public float timeOffset;
    }

    /// <summary>
    /// Effects of an item template (metres, item frame = the hand slot's frame for held items; Docs/formats-effects.md §8):
    /// gCEffect_PS (effect + offset), eCPointLight_PS (torches), gCProjectile_PS (EffectPointImpact / EffectTargetHit:
    /// the explosive arrow's blast).
    /// </summary>
    [Serializable]
    public sealed class G3ItemEffectData
    {
        public string template;
        public string effect;
        public Vector3 offset;
        public bool light;
        public Color lightColor = Color.white;
        public float lightRange = 10, lightIntensity = 1;
        public Vector3 lightOffset;
        [Tooltip("eEDynamicLightEffect [inferred order]: 0 steady, 1 flicker, 2 pulse, 3 blink, 4 strobe")] public int lightEffect;
        public float lightPeriod = 1, lightPhase;
        public bool lightShadows;
        public string impactEffect, hitEffect;
    }
}
