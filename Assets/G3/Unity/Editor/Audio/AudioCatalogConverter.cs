using System;
using System.Collections.Generic;
using System.IO;
using G3.Audio;
using F = G3.Formats.Audio;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Converts the decoded G3 audio formats (G3.Formats.Audio) into the engine-free runtime data (G3.Audio):
    /// ms → s, cm → m, sample names → lower-case base names. Free of UnityEngine so G3Cli compiles it too
    /// (`G3Cli audio sim`).
    /// </summary>
    public static class AudioCatalogConverter
    {
        const float M = 0.01f;

        public static string ClipKey(string sample) => Path.GetFileNameWithoutExtension(sample ?? "").ToLowerInvariant();

        /// <summary>g3.mul → MusicCatalog. Fragments whose sample is not shipped (trelis_explore_night_01_new) are dropped.</summary>
        public static MusicCatalog Music(F.MusicLibrary lib, Func<string, bool> sampleExists)
        {
            var cat = new MusicCatalog();
            foreach (var f in lib.Fragments)
            {
                var clip = ClipKey(f.Sample);
                if (sampleExists != null && !sampleExists(clip)) continue;
                var d = new MusicFragmentData
                {
                    location = f.Location.ToLowerInvariant(), situation = f.Situation.ToLowerInvariant(),
                    dayTime = f.DayTime.ToLowerInvariant(), variation = f.Variation, nextVariation = f.NextVariation,
                    clip = clip, volume = f.Volume, duration = f.DurationMs / 1000f,
                };
                foreach (var l in f.Links)
                    d.links.Add(new MusicLinkData { time = l.OffsetMs / 1000f, connectIn = l.ConnectIn, connectOut = l.ConnectOut, kind = (MusicLinkKind)l.Type });
                cat.fragments.Add(d);
            }
            foreach (var t in lib.Transitions)
            {
                var clip = ClipKey(t.Sample);
                if (sampleExists != null && !sampleExists(clip)) continue;
                cat.transitions.Add(new MusicTransitionData
                {
                    id = t.Id, clip = clip, connectIn = t.ConnectIn, connectOut = t.ConnectOut,
                    fadeOutBegin = t.FadeOutBeginMs / 1000f, fadeOutEnd = t.FadeOutEndMs / 1000f,
                    fadeInBegin = t.FadeInBeginMs / 1000f, fadeInEnd = t.FadeInEndMs / 1000f,
                    offset = t.OffsetMs / 1000f, volume = t.Volume, duration = t.DurationMs / 1000f,
                });
            }
            return cat;
        }

        /// <summary>g3.ams → AmbientCatalog.</summary>
        public static AmbientCatalog Ambient(F.AmbientLibrary lib)
        {
            var cat = new AmbientCatalog();
            foreach (var s in lib.Sounds)
                cat.sounds.Add(new AmbientSoundData
                {
                    climate = (AmbientClimate)s.Climate, environment = (AmbientEnvironment)s.Environment, dayTime = (AmbientDayTime)s.DayTime,
                    clip = ClipKey(s.Sample), volume = s.Volume, minDistance = s.MinDistance * M, maxDistance = s.MaxDistance * M,
                    weight = s.Weight,
                });
            foreach (var e in lib.Environments)
                cat.cells.Add(new AmbientCellParams
                {
                    climate = (AmbientClimate)e.Climate, environment = (AmbientEnvironment)e.Environment,
                    minSounds = e.MinNumSounds, maxSounds = e.MaxNumSounds,
                    minDelay = e.MinSpawnDelay, maxDelay = e.MaxSpawnDelay, probability = e.SpawnProbability,
                });
            return cat;
        }

        /// <summary>First PlaySound command of an effect, or null.</summary>
        public static EffectSoundData Effect(F.Effect e)
        {
            if (e == null || e.Sounds.Count == 0) return null;
            var s = e.Sounds[0];
            var clips = new List<string>();
            foreach (var x in s.Samples) clips.Add(ClipKey(x.Sample));
            return new EffectSoundData
            {
                name = e.Name.ToLowerInvariant(), volume = s.Volume,
                minDistance = Math.Max(0.1f, s.MinDistance * M), maxDistance = Math.Max(1f, s.MaxDistance * M),
                clips = clips.ToArray(),
            };
        }

        /// <summary>eCWeatherZone_PS → MusicZoneData (metres, unit axes from the world matrix rows).</summary>
        public static MusicZoneData Zone(F.MusicZone z)
        {
            var w = z.World;
            var ax = new V3(w[0], w[1], w[2]);
            var ay = new V3(w[4], w[5], w[6]);
            var az = new V3(w[8], w[9], w[10]);
            return new MusicZoneData
            {
                name = z.Name, enabled = z.Enabled, shape = (ZoneShape)z.Shape,
                centre = new V3(z.X * M, z.Y * M, z.Z * M),
                axisX = ax.Normalized, axisY = ay.Normalized, axisZ = az.Normalized,
                innerRadius = z.InnerRadius * M, outerRadius = z.OuterRadius * M,
                innerExtents = new V3(z.InnerExtends[0] * M, z.InnerExtends[1] * M, z.InnerExtends[2] * M),
                outerExtents = new V3(z.OuterExtends[0] * M, z.OuterExtends[1] * M, z.OuterExtends[2] * M),
                weatherEnvironment = z.WeatherEnvironment, musicLocation = z.MusicLocation,
            };
        }

        /// <summary>eCAudioEmitter_PS → SoundEmitterData at a world position given in G3 centimetres.</summary>
        public static SoundEmitterData Emitter(F.SoundEmitter s, string name, float xCm, float yCm, float zCm)
        {
            return new SoundEmitterData
            {
                name = name, position = new V3(xCm * M, yCm * M, zCm * M), clip = ClipKey(s.Sound),
                mode = (EmitterMode)s.Mode, volume = s.Volume,
                minDistance = Math.Max(0.1f, s.MinDistance * M), maxDistance = Math.Max(1f, s.MaxDistance * M),
                repeatDelayMin = s.RepeatDelayMin, repeatDelayMax = s.RepeatDelayMax,
                repeatProbabilityMin = s.RepeatProbabilityMin, repeatProbabilityMax = s.RepeatProbabilityMax,
                maxRepeats = (int)Math.Round(s.MaxRepeatsMax), useMaxRepeats = s.UseMaxRepeats, stream = s.UseStream,
            };
        }
    }
}
