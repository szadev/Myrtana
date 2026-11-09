using System;
using System.Collections.Generic;
using G3.Formats.Genome;
using G3.Formats.World;

namespace G3.Formats.Audio
{
    /// <summary>eEWeatherZoneShape</summary>
    public enum WeatherZoneShape { Circle2D = 0, Rect2D = 1, Sphere3D = 2, Box3D = 3 }

    /// <summary>eEAudioEmitterMode</summary>
    public enum AudioEmitterMode { Once = 0, Loop = 1, Repeat = 2 }

    /// <summary>
    /// eCWeatherZone_PS: the only world entity that names a music location (MusicLocation) — G3's "music zone".
    /// Also selects the weather environment. Positions/extents in cm, G3 axes (Y up).
    /// </summary>
    public sealed class MusicZone
    {
        public string Name, SourcePath;
        public bool Enabled;
        public float[] World = new float[16];
        public WeatherZoneShape Shape;
        public float InnerRadius, OuterRadius;
        public float[] InnerExtends = new float[3], OuterExtends = new float[3];   // half extents [inferred]
        public string WeatherEnvironment, MusicLocation;
        public float X => World[12];
        public float Y => World[13];
        public float Z => World[14];

        /// <summary>
        /// eCWeatherZone_PS::GetBlendFactor [inferred]: 1 inside the inner shape, 0 outside the outer one, linear in
        /// between; boxes/rects per local axis, combined with min.
        /// </summary>
        public float BlendFactor(float px, float py, float pz)
        {
            float dx = px - X, dy = py - Y, dz = pz - Z;
            switch (Shape)
            {
                case WeatherZoneShape.Circle2D: return Ramp((float)Math.Sqrt(dx * dx + dz * dz), InnerRadius, OuterRadius);
                case WeatherZoneShape.Sphere3D: return Ramp((float)Math.Sqrt(dx * dx + dy * dy + dz * dz), InnerRadius, OuterRadius);
                default:
                    float f = 1;
                    for (int a = 0; a < 3; a++)
                    {
                        if (Shape == WeatherZoneShape.Rect2D && a == 1) continue;
                        float ax = World[a * 4], ay = World[a * 4 + 1], az = World[a * 4 + 2];
                        float len = (float)Math.Sqrt(ax * ax + ay * ay + az * az);
                        float l = len > 1e-6f ? Math.Abs((dx * ax + dy * ay + dz * az) / len) : 0;
                        f = Math.Min(f, Ramp(l, InnerExtends[a], OuterExtends[a]));
                    }
                    return f;
            }
        }

        static float Ramp(float d, float inner, float outer)
        {
            if (d <= inner) return 1;
            if (d >= outer) return 0;
            return (outer - d) / (outer - inner);
        }
    }

    /// <summary>eCAudioEmitter_PS: a placed 3D sound (campfire crackle, waterfall, ambience bed).</summary>
    public sealed class SoundEmitter
    {
        public string Name, SourcePath, Sound;
        public float X, Y, Z;
        public bool Enabled, AutoReset, AutoKill, UseStream, UseMaxRepeats;
        public float Volume, MinDistance, MaxDistance;   // cm
        public AudioEmitterMode Mode;
        public float RepeatDelayMin, RepeatDelayMax;     // SecondsBetweenRepeats (bCRange1)
        public float RepeatProbabilityMin, RepeatProbabilityMax;
        public float MaxRepeatsMin, MaxRepeatsMax;
    }

    /// <summary>gCEffect_PS: an entity that plays a g3.efm effect (torches, fires…); sounds come from the effect.</summary>
    public sealed class EffectSource
    {
        public string Name, SourcePath, Effect;
        public float X, Y, Z;
        public float OffX, OffY, OffZ, Probability;
        public bool Static, IsItem, IsNpc;
    }

    /// <summary>Audio-relevant data gathered from world entities (archives read with keepRaw: true).</summary>
    public sealed class AudioWorld
    {
        public readonly List<MusicZone> Zones = new List<MusicZone>();
        public readonly List<SoundEmitter> Emitters = new List<SoundEmitter>();
        public readonly List<EffectSource> Effects = new List<EffectSource>();
        /// <summary>SpeedTree count per eCSpeedTree_PS.AmbientEnvironment (gCAmbientLibrary ambient objects).</summary>
        public readonly int[] AmbientTreeCounts = new int[6];
        /// <summary>eCParticle_PS with SpawningSound/CollisionSound != 0.</summary>
        public int ParticlesWithSound;

        public void Add(WorldEntity e, string sourcePath)
        {
            if (e.RawPropertySets == null) return;
            var g = e.Source;
            foreach (var ps in e.RawPropertySets)
            {
                switch (ps.ClassName)
                {
                    case "eCWeatherZone_PS":
                    {
                        var z = new MusicZone
                        {
                            Name = e.Name, SourcePath = sourcePath, Enabled = e.Enabled,
                            Shape = (WeatherZoneShape)(ps.Get("Shape")?.AsEnum() ?? 0),
                            InnerRadius = ps.Get("InnerRadius")?.AsFloat() ?? 0, OuterRadius = ps.Get("OuterRadius")?.AsFloat() ?? 0,
                            WeatherEnvironment = Str(g, ps, "WeatherEnvironment"), MusicLocation = Str(g, ps, "MusicLocation"),
                        };
                        Array.Copy(e.World, z.World, 16);
                        Vec(ps, "InnerExtends", z.InnerExtends);
                        Vec(ps, "OuterExtends", z.OuterExtends);
                        Zones.Add(z);
                        break;
                    }
                    case "eCAudioEmitter_PS":
                    {
                        var s = ReadEmitter(ps, g);
                        s.Name = e.Name; s.SourcePath = sourcePath; s.X = e.X; s.Y = e.Y; s.Z = e.Z;
                        Emitters.Add(s);
                        break;
                    }
                    case "gCEffect_PS":
                    {
                        var name = Str(g, ps, "Effect");
                        if (string.IsNullOrEmpty(name)) break;
                        var f = new EffectSource
                        {
                            Name = e.Name, SourcePath = sourcePath, Effect = name, X = e.X, Y = e.Y, Z = e.Z,
                            Probability = ps.Get("Probability")?.AsFloat() ?? 1, Static = ps.Get("Static")?.AsBool() ?? false,
                            IsItem = e.IsItem, IsNpc = e.IsNpc,
                        };
                        var off = new float[3];
                        Vec(ps, "Offset", off);
                        f.OffX = off[0]; f.OffY = off[1]; f.OffZ = off[2];
                        Effects.Add(f);
                        break;
                    }
                    case "eCSpeedTree_PS":
                    {
                        int env = ps.Get("AmbientEnvironment")?.AsEnum() ?? 0;
                        if ((uint)env < 6) AmbientTreeCounts[env]++;
                        break;
                    }
                    case "eCParticle_PS":
                        if ((ps.Get("SpawningSound")?.AsEnum() ?? 0) != 0 || (ps.Get("CollisionSound")?.AsEnum() ?? 0) != 0)
                            ParticlesWithSound++;
                        break;
                }
            }
        }

        /// <summary>eCAudioEmitter_PS properties (world entity or template). Position fields are left 0.</summary>
        public static SoundEmitter ReadEmitter(PropertyObject ps, GenomeReader g)
        {
            var s = new SoundEmitter
            {
                Sound = Str(g, ps, "Sound"),
                Enabled = ps.Get("Enabled")?.AsBool() ?? true, AutoReset = ps.Get("AutoReset")?.AsBool() ?? false,
                AutoKill = ps.Get("AutoKill")?.AsBool() ?? false, UseStream = ps.Get("UseStream")?.AsBool() ?? false,
                UseMaxRepeats = ps.Get("UseMaxRepeats")?.AsBool() ?? false,
                Volume = ps.Get("Volume")?.AsFloat() ?? 1, MinDistance = ps.Get("MinDistance")?.AsFloat() ?? 0,
                MaxDistance = ps.Get("MaxDistance")?.AsFloat() ?? 0,
                Mode = (AudioEmitterMode)(ps.Get("SpawningMode")?.AsEnum() ?? 0),
            };
            Range(ps, "SecondsBetweenRepeats", out s.RepeatDelayMin, out s.RepeatDelayMax);
            Range(ps, "RepeatProbability", out s.RepeatProbabilityMin, out s.RepeatProbabilityMax);
            Range(ps, "MaxNumRepeats", out s.MaxRepeatsMin, out s.MaxRepeatsMax);
            return s;
        }

        /// <summary>
        /// Music location at a point: the most specific zone the point is in — the smallest enabled zone with a
        /// MusicLocation whose blend factor is at least 0.5, ties → the stronger factor; none that far in → the strongest
        /// zone [inferred; same rule as G3.Audio.MusicZoneMap, which adds hysteresis]. null = none (region default).
        /// y = NaN ignores height (each zone is tested at its own centre height).
        /// </summary>
        public MusicZone MusicZoneAt(float x, float y, float z)
        {
            MusicZone best = null, strongest = null;
            float bestF = 0, bestSize = float.MaxValue, strongF = 0, strongSize = float.MaxValue;
            foreach (var zn in Zones)
            {
                if (!zn.Enabled || string.IsNullOrEmpty(zn.MusicLocation)) continue;
                float f = zn.BlendFactor(x, float.IsNaN(y) ? zn.Y : y, z);
                if (f <= 0) continue;
                float size = zn.Shape == WeatherZoneShape.Circle2D || zn.Shape == WeatherZoneShape.Sphere3D
                    ? zn.OuterRadius : Math.Max(zn.OuterExtends[0], zn.OuterExtends[2]);
                if (f > strongF || (f == strongF && size < strongSize)) { strongest = zn; strongF = f; strongSize = size; }
                if (f >= 0.5f && (size < bestSize || (size == bestSize && f > bestF))) { best = zn; bestF = f; bestSize = size; }
            }
            return best ?? strongest;
        }

        static string Str(GenomeReader g, PropertyObject o, string name)
        {
            var p = o.Get(name);
            return p == null || g == null ? "" : g.StrFrom(p.Value);
        }

        static void Vec(PropertyObject o, string name, float[] into)
        {
            var p = o.Get(name);
            if (p == null || p.Value.Length < 12) return;
            for (int i = 0; i < 3; i++) into[i] = BitConverter.ToSingle(p.Value, i * 4);
        }

        static void Range(PropertyObject o, string name, out float min, out float max)
        {
            var p = o.Get(name);
            min = max = 0;
            if (p == null || p.Value.Length < 8) return;
            min = BitConverter.ToSingle(p.Value, 0);
            max = BitConverter.ToSingle(p.Value, 4);
        }
    }
}
