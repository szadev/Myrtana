using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    // Own file named after the class: Unity serializes a ScriptableObject only through a MonoScript whose file name
    // matches the class (in G3WorldEffects.cs the baked asset had m_Script 0 and loaded as null: no world effects).
    /// <summary>
    /// Effects placed in the world (baked by <c>G3/Effects/Import Effects</c>): gCEffect_PS entities (wall torches, candles,
    /// camp fires, glow-worms, flies, waterfalls) by effect name, and entities that carry an eCParticle_PS themselves
    /// (coal glow, smoke, splashes, dust) by template name. Metres, Unity axes. Docs/formats-effects.md §7.
    /// </summary>
    public sealed class G3WorldEffectsAsset : ScriptableObject
    {
        public const string ResourcePath = "G3WorldEffects";
        public List<G3WorldEffectPlacement> placements = new List<G3WorldEffectPlacement>();
    }

    [Serializable]
    public struct G3WorldEffectPlacement
    {
        public Vector3 position;
        public Quaternion rotation;
        [Tooltip("g3.efm effect name, or the VFX template name when template is set")] public string name;
        public bool template;
        [Tooltip("Shown within this distance (m)")] public float radius;
        [Tooltip("Story-switchable world sector (G3StorySectors); empty = always")] public string sector;
        [Tooltip("The sector is on at the start of a new game")] public bool enabledAtStart;
    }
}
