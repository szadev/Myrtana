using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Serialized copy of Gothic 3's _intern/WeatherStates.g3ws (see Docs/formats-weather.md), created by
    /// the editor menu "G3/Environment/Import Weather". Runtime never needs the pak.
    /// Curve times are 0..1 over the day (0 = midnight); colours are stored exactly as in the file (gamma space).
    /// </summary>
    [CreateAssetMenu(menuName = "G3/Weather Asset", fileName = "G3Weather")]
    public sealed class G3WeatherAsset : ScriptableObject
    {
        public List<G3WeatherState> states = new List<G3WeatherState>();
        public List<G3WeatherEnvironment> environments = new List<G3WeatherEnvironment>();
        [Tooltip("RainAndSnow.tga particles (G3/Particle), set by G3/Effects/Import Effects")] public Material precipitationMaterial;

        public G3WeatherState GetState(string name)
        {
            foreach (var s in states)
                if (string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public G3WeatherEnvironment GetEnvironment(string name)
        {
            foreach (var e in environments)
                if (string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Resolves a state name or an environment name (→ its most probable state).</summary>
        public G3WeatherState Resolve(string name)
        {
            var s = GetState(name);
            if (s != null) return s;
            var env = GetEnvironment(name);
            if (env != null)
            {
                G3WeatherProbability best = null;
                foreach (var p in env.probabilities)
                    if (best == null || p.probability > best.probability) best = p;
                if (best != null) s = GetState(best.state);
            }
            return s ?? (states.Count > 0 ? states[0] : null);
        }
    }

    [Serializable]
    public sealed class G3WeatherState
    {
        public string name;
        public G3ColorCurve lightDiffuseColor = new G3ColorCurve();
        public G3ColorCurve lightSpecularColor = new G3ColorCurve();
        public G3FloatCurve lightIntensity = new G3FloatCurve();
        public G3ColorCurve ambientBackLightColor = new G3ColorCurve();
        public G3ColorCurve ambientGeneralColor = new G3ColorCurve();
        public G3FloatCurve ambientIntensity = new G3FloatCurve();
        public G3ColorCurve fogColor = new G3ColorCurve();
        /// <summary>centimetres</summary>
        public G3FloatCurve fogStart = new G3FloatCurve();
        public G3ColorCurve skyColor = new G3ColorCurve();
        public G3ColorCurve hazeColor = new G3ColorCurve();
        /// <summary>centimetres</summary>
        public G3FloatCurve fogEnd = new G3FloatCurve();
        public G3FloatCurve cloudThickness = new G3FloatCurve();
        public G3ColorCurve cloudColor = new G3ColorCurve();
        public G3FloatCurve glowIntensity = new G3FloatCurve();
        public G3FloatCurve shadowDensity = new G3FloatCurve();
        /// <summary>degrees Celsius (gameplay)</summary>
        public G3FloatCurve temperature = new G3FloatCurve();

        [Header("Precipitation")]
        public int particlesPerSecond;
        public Vector2 speedMinMax;
        public Vector2 size;
        public float directionScale;
        /// <summary>Tile of RainAndSnow.tga (2 × 2): 2 rain, 1 snow [verified data]; splash tile and size scale for drops hitting the ground.</summary>
        public int textureTile;
        public float spawnPointDizzer, jitterPower, jitterSpeed, splashDuration;
        public int splashTextureTile;
        public Vector2 splashSizeScale = Vector2.one;
        public string loopSample;
        public float loopVolume;
        [Tooltip("loopSample imported by G3/Effects/Import Effects (CP 1.75: rain_medium_01, wind_highplaces_01, wind_varant_01)")]
        public AudioClip loopClip;

        public G3WeatherSample Sample(float t01)
        {
            return new G3WeatherSample
            {
                lightDiffuse = lightDiffuseColor.Evaluate(t01),
                lightSpecular = lightSpecularColor.Evaluate(t01),
                lightIntensity = lightIntensity.Evaluate(t01),
                ambientBack = ambientBackLightColor.Evaluate(t01),
                ambientGeneral = ambientGeneralColor.Evaluate(t01),
                ambientIntensity = ambientIntensity.Evaluate(t01),
                fogColor = fogColor.Evaluate(t01),
                fogStart = fogStart.Evaluate(t01),
                fogEnd = fogEnd.Evaluate(t01),
                skyColor = skyColor.Evaluate(t01),
                hazeColor = hazeColor.Evaluate(t01),
                cloudThickness = cloudThickness.Evaluate(t01),
                cloudColor = cloudColor.Evaluate(t01),
                glowIntensity = glowIntensity.Evaluate(t01),
                shadowDensity = shadowDensity.Evaluate(t01),
                temperature = temperature.Evaluate(t01),
            };
        }
    }

    /// <summary>All weather values at one moment (gamma-space colours, G3 units).</summary>
    public struct G3WeatherSample
    {
        public Color lightDiffuse, lightSpecular, ambientBack, ambientGeneral, fogColor, skyColor, hazeColor, cloudColor;
        public float lightIntensity, ambientIntensity, fogStart, fogEnd, cloudThickness, glowIntensity, shadowDensity, temperature;

        public static G3WeatherSample Lerp(in G3WeatherSample a, in G3WeatherSample b, float t)
        {
            return new G3WeatherSample
            {
                lightDiffuse = Color.Lerp(a.lightDiffuse, b.lightDiffuse, t),
                lightSpecular = Color.Lerp(a.lightSpecular, b.lightSpecular, t),
                ambientBack = Color.Lerp(a.ambientBack, b.ambientBack, t),
                ambientGeneral = Color.Lerp(a.ambientGeneral, b.ambientGeneral, t),
                fogColor = Color.Lerp(a.fogColor, b.fogColor, t),
                skyColor = Color.Lerp(a.skyColor, b.skyColor, t),
                hazeColor = Color.Lerp(a.hazeColor, b.hazeColor, t),
                cloudColor = Color.Lerp(a.cloudColor, b.cloudColor, t),
                lightIntensity = Mathf.Lerp(a.lightIntensity, b.lightIntensity, t),
                ambientIntensity = Mathf.Lerp(a.ambientIntensity, b.ambientIntensity, t),
                fogStart = Mathf.Lerp(a.fogStart, b.fogStart, t),
                fogEnd = Mathf.Lerp(a.fogEnd, b.fogEnd, t),
                cloudThickness = Mathf.Lerp(a.cloudThickness, b.cloudThickness, t),
                glowIntensity = Mathf.Lerp(a.glowIntensity, b.glowIntensity, t),
                shadowDensity = Mathf.Lerp(a.shadowDensity, b.shadowDensity, t),
                temperature = Mathf.Lerp(a.temperature, b.temperature, t),
            };
        }
    }

    /// <summary>Piecewise-linear daily curve; wraps around midnight (last key → first key).</summary>
    [Serializable]
    public sealed class G3FloatCurve
    {
        public float[] times = Array.Empty<float>();
        public float[] values = Array.Empty<float>();

        public float Evaluate(float t)
        {
            int n = times?.Length ?? 0;
            if (n == 0) return 0f;
            if (n == 1) return values[0];
            G3CurveMath.Locate(times, t, out int a, out int b, out float x);
            return Mathf.Lerp(values[a], values[b], x);
        }
    }

    [Serializable]
    public sealed class G3ColorCurve
    {
        public float[] times = Array.Empty<float>();
        public Color[] values = Array.Empty<Color>();

        public Color Evaluate(float t)
        {
            int n = times?.Length ?? 0;
            if (n == 0) return Color.black;
            if (n == 1) return values[0];
            G3CurveMath.Locate(times, t, out int a, out int b, out float x);
            return Color.Lerp(values[a], values[b], x);
        }
    }

    static class G3CurveMath
    {
        public static void Locate(float[] times, float t, out int a, out int b, out float x)
        {
            int n = times.Length;
            t -= Mathf.Floor(t);
            float t0 = times[0], tn = times[n - 1];
            if (t < t0 || t >= tn)
            {
                a = n - 1; b = 0;
                float span = (1f - tn) + t0;
                float d = t >= tn ? t - tn : t + 1f - tn;
                x = span > 1e-6f ? d / span : 0f;
                return;
            }
            a = 0;
            while (a < n - 2 && times[a + 1] <= t) a++;
            b = a + 1;
            float ta = times[a], tb = times[b];
            x = tb > ta ? (t - ta) / (tb - ta) : 0f;
        }
    }

    [Serializable]
    public sealed class G3WeatherEnvironment
    {
        public string name;
        public string musicLocation;
        /// <summary>0 Mediterranean, 1 Arctic, 2 Desert, 3 Cave</summary>
        public int climate;
        public List<G3WeatherProbability> probabilities = new List<G3WeatherProbability>();
    }

    [Serializable]
    public sealed class G3WeatherProbability
    {
        public string state;
        public float probability;
        /// <summary>fraction of a day</summary>
        public float minDuration, maxDuration;
    }
}
