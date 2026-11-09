using G3.Unity;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// G3 sets the bloom highlight threshold from the weather (1 − GlowIntensity; Docs/formats-weather.md): follows the
    /// active <see cref="G3Environment"/>'s current GlowIntensity into G3's bloom (<see cref="G3ImageProcessing.BloomThreshold"/>,
    /// <see cref="PostLook.BloomThreshold"/>), sunny 0.4 (threshold 0.6) without an environment.
    /// </summary>
    public sealed class PostWeatherBloom : MonoBehaviour
    {
        G3Environment _environment;
        float _nextSearch;

        void Update()
        {
            if (_environment == null && Time.unscaledTime >= _nextSearch)
            {
                _environment = FindAnyObjectByType<G3Environment>();
                _nextSearch = Time.unscaledTime + 2f;
            }
            float glow = _environment != null && _environment.weather != null
                ? _environment.Current.glowIntensity
                : PostLook.DefaultGlowIntensity;
            G3ImageProcessing.BloomThreshold = PostLook.BloomThreshold(glow);
        }
    }
}
