using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Audio
{
    /// <summary>eEAmbientClimate (also eCWeatherEnvironment climate).</summary>
    public enum AmbientClimate { Mediterranean = 0, Arctic = 1, Desert = 2, Cave = 3 }

    /// <summary>eEAmbientEnvironment (eCSpeedTree_PS.AmbientEnvironment, gCAmbientSound.Environment).</summary>
    public enum AmbientEnvironment { Forest = 0, Meadow = 1, Plain = 2, Mountain = 3, Coast = 4, Town = 5 }

    /// <summary>eEAmbientDayTime. gCClock_PS: 6–8 h morning, 8–20 day, 20–22 evening, 22–6 night (Docs/formats-weather.md §3).</summary>
    public enum AmbientDayTime { Morning = 0, Day = 1, Evening = 2, Night = 3 }

    /// <summary>gCAmbientSound: a one-shot sample that may spawn for (climate, environment, daytime).</summary>
    public sealed class AmbientSound
    {
        public AmbientClimate Climate;
        public AmbientEnvironment Environment;
        public AmbientDayTime DayTime;
        public string Sample;          // as stored (".wav"); shipped as .ogg under Sound.pak environment/…
        public float Volume, MinDistance, MaxDistance;   // distances in cm
        public int Weight;             // relative pick weight among the matching sounds
        public uint DurationMs;        // 0 = not measured by the editor
    }

    /// <summary>gCAmbientEnvironment: spawn parameters of one (climate, environment) cell.</summary>
    public sealed class AmbientEnvironmentParams
    {
        public AmbientClimate Climate;
        public AmbientEnvironment Environment;
        public int MinNumSounds, MaxNumSounds;
        public float MinSpawnDelay, MaxSpawnDelay;   // seconds
        public float SpawnProbability;
    }

    /// <summary>
    /// Reader for <c>Library/g3.ams</c> (gCAmbientLibrary::Load). Docs/formats-audio.md §3.
    /// Payload: u16 version (2); sound list (u16 version 1, u32 count, count × gCAmbientSound object);
    /// u32 count (24 = 4 climates × 6 environments) × gCAmbientEnvironment object.
    /// </summary>
    public sealed class AmbientLibrary
    {
        public ushort Version;
        public readonly List<AmbientSound> Sounds = new List<AmbientSound>();
        /// <summary>Index = climate · 6 + environment [inferred from which cells are tuned: 0..2 = Mediterranean forest/meadow/plain].</summary>
        public readonly List<AmbientEnvironmentParams> Environments = new List<AmbientEnvironmentParams>();

        public static AmbientLibrary Read(byte[] data)
        {
            var g = new GenomeReader(data);
            var r = g.R;
            var lib = new AmbientLibrary { Version = r.ReadUInt16() };
            if (lib.Version != 2) throw new InvalidDataException($"g3.ams: version {lib.Version}");
            if (r.ReadUInt16() != 1) throw new InvalidDataException("g3.ams: sound list version");
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var o = g.ReadObject();
                if (o?.ClassName != "gCAmbientSound") throw new InvalidDataException($"g3.ams: unexpected {o?.ClassName}");
                lib.Sounds.Add(new AmbientSound
                {
                    Climate = (AmbientClimate)(o.Get("Climate")?.AsEnum() ?? 0),
                    Environment = (AmbientEnvironment)(o.Get("Environment")?.AsEnum() ?? 0),
                    DayTime = (AmbientDayTime)(o.Get("DayTime")?.AsEnum() ?? 0),
                    Sample = MusicLibrary.Str(g, o, "Sample"),
                    Volume = o.Get("Volume")?.AsFloat() ?? 1f,
                    MinDistance = o.Get("MinDistance")?.AsFloat() ?? 0f,
                    MaxDistance = o.Get("MaxDistance")?.AsFloat() ?? 0f,
                    Weight = o.Get("Weight")?.AsInt() ?? 1,
                    DurationMs = (uint)(o.Get("Duration")?.AsInt() ?? 0),
                });
                g.Position = o.End;
            }
            int envs = r.ReadInt32();
            for (int i = 0; i < envs; i++)
            {
                var o = g.ReadObject();
                if (o?.ClassName != "gCAmbientEnvironment") throw new InvalidDataException($"g3.ams: unexpected {o?.ClassName}");
                lib.Environments.Add(new AmbientEnvironmentParams
                {
                    Climate = (AmbientClimate)(i / 6), Environment = (AmbientEnvironment)(i % 6),
                    MinNumSounds = o.Get("MinNumSounds")?.AsInt() ?? 0, MaxNumSounds = o.Get("MaxNumSounds")?.AsInt() ?? 0,
                    MinSpawnDelay = o.Get("MinSpawnDelay")?.AsFloat() ?? 0, MaxSpawnDelay = o.Get("MaxSpawnDelay")?.AsFloat() ?? 0,
                    SpawnProbability = o.Get("SpawnProbability")?.AsFloat() ?? 0,
                });
                g.Position = o.End;
            }
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"g3.ams: {g.PayloadEnd - g.Position} bytes left over");
            return lib;
        }

        public AmbientEnvironmentParams GetEnvironment(AmbientClimate c, AmbientEnvironment e)
        {
            int i = (int)c * 6 + (int)e;
            return i < Environments.Count ? Environments[i] : null;
        }

        /// <summary>Candidates for gCAmbientSoundList::FindSound(climate, environment, daytime); pick by Weight.</summary>
        public List<AmbientSound> FindSounds(AmbientClimate c, AmbientEnvironment e, AmbientDayTime t)
        {
            var res = new List<AmbientSound>();
            foreach (var s in Sounds)
                if (s.Climate == c && s.Environment == e && s.DayTime == t) res.Add(s);
            return res;
        }
    }
}
