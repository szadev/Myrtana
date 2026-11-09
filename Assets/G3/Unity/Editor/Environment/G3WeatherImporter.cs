using System.IO;
using G3.Formats.Environment;
using G3.Formats.Pak;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Converts WeatherStates.g3ws into Assets/G3/Environment/G3Weather.asset. CP 1.75 ships its own copy in Library.pak
    /// (loop sounds, the Cave environment, retuned rain); the vanilla _intern copy is the fallback. Docs/formats-weather.md §4.
    /// </summary>
    public static class G3WeatherImporter
    {
        public const string AssetFolder = "Assets/G3/Environment";
        public const string AssetPath = AssetFolder + "/G3Weather.asset";

        [MenuItem("G3/Environment/Import Weather")]
        public static void ImportMenu()
        {
            var a = Import();
            Debug.Log($"G3 weather: {a.states.Count} states, {a.environments.Count} environments → {AssetPath}");
            Selection.activeObject = a;
        }

        /// <summary>Imports (or re-imports in place) the weather asset; returns it.</summary>
        public static G3WeatherAsset Import(string dataDir = null)
        {
            dataDir ??= G3WorldImporter.DataDir;
            byte[] bytes = null;
            foreach (var volume in new[] { "Library", "_intern" })
            {
                try
                {
                    using (var fs = new G3FileSystem(dataDir, volume))
                    {
                        var e = fs.GetByName("WeatherStates.g3ws");
                        if (e != null) { bytes = e.Read(); break; }
                    }
                }
                catch (IOException) { }
            }
            if (bytes == null) throw new FileNotFoundException("WeatherStates.g3ws not found in Library or _intern of " + dataDir);
            var file = WeatherStatesFile.Read(bytes);

            if (!AssetDatabase.IsValidFolder(AssetFolder))
                AssetDatabase.CreateFolder("Assets/G3", "Environment");
            var asset = AssetDatabase.LoadAssetAtPath<G3WeatherAsset>(AssetPath);
            bool create = asset == null;
            if (create) asset = ScriptableObject.CreateInstance<G3WeatherAsset>();

            var clips = new System.Collections.Generic.Dictionary<string, AudioClip>();
            foreach (var old in asset.states) if (old?.loopClip != null) clips[old.name] = old.loopClip; // set by the effects import
            asset.states.Clear();
            foreach (var s in file.States)
            {
                var d = new G3WeatherState
                {
                    name = s.Name,
                    lightDiffuseColor = C(s[WeatherCurve.LightDiffuseColor]),
                    lightSpecularColor = C(s[WeatherCurve.LightSpecularColor]),
                    lightIntensity = F(s[WeatherCurve.LightIntensity]),
                    ambientBackLightColor = C(s[WeatherCurve.AmbientBackLightColor]),
                    ambientGeneralColor = C(s[WeatherCurve.AmbientGeneralColor]),
                    ambientIntensity = F(s[WeatherCurve.AmbientIntensity]),
                    fogColor = C(s[WeatherCurve.FogColor]),
                    fogStart = F(s[WeatherCurve.FogStart]),
                    skyColor = C(s[WeatherCurve.SkyColor]),
                    hazeColor = C(s[WeatherCurve.HazeColor]),
                    fogEnd = F(s[WeatherCurve.FogEnd]),
                    cloudThickness = F(s[WeatherCurve.CloudThickness]),
                    cloudColor = C(s[WeatherCurve.CloudColor]),
                    glowIntensity = F(s[WeatherCurve.GlowIntensity]),
                    shadowDensity = F(s[WeatherCurve.ShadowDensity]),
                    temperature = F(s[WeatherCurve.Temperature]),
                    particlesPerSecond = s.ParticlesPerSecond,
                    speedMinMax = new Vector2(s.SpeedMin, s.SpeedMax),
                    size = new Vector2(s.SizeX, s.SizeY),
                    directionScale = s.DirectionScale,
                    textureTile = s.CurrentTextureTile,
                    spawnPointDizzer = s.SpawnPointDizzer,
                    jitterPower = s.JitterPower,
                    jitterSpeed = s.JitterSpeed,
                    splashDuration = s.SplashDuration,
                    splashTextureTile = s.SplashTextureTile,
                    splashSizeScale = new Vector2(s.SplashSizeScaleX, s.SplashSizeScaleY),
                    loopSample = s.LoopSample,
                    loopVolume = s.LoopVolume,
                };
                if (clips.TryGetValue(s.Name, out var clip)) d.loopClip = clip;
                asset.states.Add(d);
            }

            asset.environments.Clear();
            foreach (var e in file.Environments)
            {
                var d = new G3WeatherEnvironment { name = e.Name, musicLocation = e.MusicLocation, climate = (int)e.Climate };
                foreach (var p in e.Probabilities)
                    d.probabilities.Add(new G3WeatherProbability
                    {
                        state = p.StateName, probability = p.Probability,
                        minDuration = p.MinDuration, maxDuration = p.MaxDuration,
                    });
                asset.environments.Add(d);
            }

            if (create) AssetDatabase.CreateAsset(asset, AssetPath);
            else EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return asset;
        }

        static G3FloatCurve F(Scale s)
        {
            int n = s.Count;
            var c = new G3FloatCurve { times = new float[n], values = new float[n] };
            for (int i = 0; i < n; i++) { c.times[i] = s.Keys[i * 2]; c.values[i] = s.Keys[i * 2 + 1]; }
            return c;
        }

        static G3ColorCurve C(Scale s)
        {
            int n = s.Count;
            var c = new G3ColorCurve { times = new float[n], values = new Color[n] };
            for (int i = 0; i < n; i++)
            {
                int o = i * 5;
                c.times[i] = s.Keys[o];
                c.values[i] = new Color(s.Keys[o + 1], s.Keys[o + 2], s.Keys[o + 3], s.Keys[o + 4]);
            }
            return c;
        }
    }
}
