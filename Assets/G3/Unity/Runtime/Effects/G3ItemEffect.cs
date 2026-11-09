using G3.Effects;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// The effect and light of an item entity (gCEffect_PS + eCPointLight_PS of the item template, baked into
    /// <see cref="G3EffectLibrary.items"/>; Docs/formats-effects.md §8): the burning torch's flame and light, the flame,
    /// ice and demon swords, the staff and sceptre glow, the burning fire arrow. G3 shows them whenever the item exists as
    /// an entity, so the prop views call <see cref="Sync"/> for hand props, carried weapons, nocked and flying arrows and
    /// items lying in the world. The effect runs while the prop is active and is removed at
    /// once, particles and all, when the prop is hidden, destroyed or shows another item (fix-124: no slow fade of the
    /// torch's glow when it is put away; G3EffectPlayer.Kill).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Item Effect")]
    public sealed class G3ItemEffect : MonoBehaviour
    {
        [Tooltip("Item lights cast shadows only when G3 says so and this is on (moving shadowed point lights are costly) [interp]")]
        public static bool AllowShadows = false;

        public string template;
        Transform _anchor;
        Light _light;
        G3ItemEffectData _data;
        int _handle;
        float _age;

        /// <summary>
        /// Shows the effects of <paramref name="itemTemplate"/> on <paramref name="prop"/> (modelled in the item frame);
        /// null or an item without effects removes them. Cheap when nothing changes; safe without an effect library.
        /// </summary>
        public static void Sync(GameObject prop, string itemTemplate)
        {
            if (prop == null) return;
            var have = prop.GetComponent<G3ItemEffect>();
            var data = string.IsNullOrEmpty(itemTemplate) ? null : G3EffectSystem.Library?.Item(itemTemplate);
            if (data == null || string.IsNullOrEmpty(data.effect) && !data.light)
            {
                if (have != null) have.Set(null, null);
                return;
            }
            if (have == null) have = prop.AddComponent<G3ItemEffect>();
            have.Set(itemTemplate, data);
        }

        /// <summary>The blast of an arrow or bolt that hit (gCProjectile_PS EffectTargetHit / EffectPointImpact); false when it has none.</summary>
        public static bool PlayImpact(string ammoTemplate, Vector3 point, bool hitTarget) => PlayImpact(ammoTemplate, point, hitTarget, out _, out _);

        /// <summary>
        /// As <see cref="PlayImpact(string, Vector3, bool)"/>: <paramref name="effect"/> is the effect played (null: none),
        /// <paramref name="parts"/> the visual parts it started (0: no visuals, e.g. no effect system or over the budget).
        /// EffectTargetHit on a character, else EffectPointImpact (gCProjectile_PS::OnTouch, fix-176).
        /// </summary>
        public static bool PlayImpact(string ammoTemplate, Vector3 point, bool hitTarget, out string effect, out int parts)
        {
            var data = string.IsNullOrEmpty(ammoTemplate) ? null : G3EffectSystem.Library?.Item(ammoTemplate);
            effect = data == null ? null : hitTarget && !string.IsNullOrEmpty(data.hitEffect) ? data.hitEffect : data.impactEffect;
            parts = 0;
            if (string.IsNullOrEmpty(effect)) { effect = null; return false; }
            int handle = G3EffectPlayer.Play(effect, point); // sound + visuals
            parts = G3EffectPlayer.PartsOf(handle);
            return true;
        }

        /// <summary>
        /// The projectile touched something (gCProjectile_PS::OnTouch, fix-176): its effect stops emitting
        /// (StopAllRuntimeEffects, the flame of a fire arrow goes out, its particles live out) and its light is gone.
        /// </summary>
        public static void Extinguish(GameObject prop)
        {
            var have = prop != null ? prop.GetComponent<G3ItemEffect>() : null;
            if (have == null) return;
            if (have._handle != 0) G3EffectPlayer.Stop(have._handle);
            have._handle = 0;
            if (have._light != null) have._light.gameObject.SetActive(false);
            have._data = null; // no restart, no light flicker
        }

        void Set(string itemTemplate, G3ItemEffectData data)
        {
            if (template == itemTemplate && _data == data) return;
            StopEffect();
            template = itemTemplate;
            _data = data;
            if (_light != null) _light.gameObject.SetActive(false);
            if (data == null) return;
            if (data.light) EnsureLight(data);
            if (isActiveAndEnabled) StartEffect();
        }

        void EnsureLight(G3ItemEffectData d)
        {
            if (_light == null)
            {
                var go = new GameObject("Item Light");
                go.transform.SetParent(transform, false);
                _light = go.AddComponent<Light>();
                _light.type = LightType.Point;
            }
            _light.transform.localPosition = d.lightOffset;
            _light.color = d.lightColor;
            _light.range = d.lightRange;
            _light.intensity = d.lightIntensity;
            _light.shadows = d.lightShadows && AllowShadows ? LightShadows.Soft : LightShadows.None;
            _light.gameObject.SetActive(true);
        }

        void OnEnable()
        {
            if (_data == null) return;
            if (_light != null) _light.gameObject.SetActive(_data.light);
            StartEffect();
        }

        void OnDisable() => StopEffect();
        void OnDestroy() => StopEffect();

        void StartEffect()
        {
            if (_handle != 0 || _data == null || string.IsNullOrEmpty(_data.effect)) return;
            if (_anchor == null)
            {
                _anchor = new GameObject("Item Effect").transform;
                _anchor.SetParent(transform, false);
            }
            _anchor.localPosition = _data.offset;
            _anchor.localRotation = Quaternion.identity;
            _handle = G3EffectPlayer.Start(_data.effect, _anchor, withSound: true);
        }

        void StopEffect()
        {
            if (_handle != 0) G3EffectPlayer.Kill(_handle); // fix-124: gone with the item entity, no fade-out
            _handle = 0;
        }

        void Update()
        {
            if (_data == null) return;
            _age += Time.deltaTime;
            if (_light != null && _light.gameObject.activeSelf)
                _light.intensity = _data.lightIntensity *
                                   ItemEffectRules.Factor((ItemEffectRules.LightEffect)_data.lightEffect, _age, _data.lightPhase, _data.lightPeriod);
            // An effect refused at start (beyond the spawn distance, part cap) is retried when it comes near.
            if (Time.frameCount % 30 != 0 || string.IsNullOrEmpty(_data.effect)) return;
            if (_handle != 0 && !G3EffectPlayer.IsLive(_handle)) _handle = 0;
            if (_handle == 0) StartEffect();
        }
    }
}
