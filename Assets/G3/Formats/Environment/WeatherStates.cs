using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Environment
{
    /// <summary>
    /// Reader for <c>_intern/WeatherStates.g3ws</c> (eCWeatherAdmin::LoadWeatherStates).
    /// Layout and field order are documented in Docs/formats-weather.md. Curve order inside each state was
    /// verified against Engine.dll (eCWeatherStates::Read reads the members in exactly this order).
    /// </summary>
    public sealed class WeatherStatesFile
    {
        public ushort Version;
        public readonly List<WeatherState> States = new List<WeatherState>();
        public readonly List<WeatherEnvironment> Environments = new List<WeatherEnvironment>();

        public WeatherState GetState(string name)
        {
            foreach (var s in States) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public WeatherEnvironment GetEnvironment(string name)
        {
            foreach (var e in Environments) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static WeatherStatesFile Read(byte[] data)
        {
            var g = new GenomeReader(data);
            var r = g.R;
            var f = new WeatherStatesFile();
            if (r.ReadUInt32() != 0x53574547) throw new InvalidDataException("WeatherStates: missing GEWS magic");
            f.Version = r.ReadUInt16();
            int stateCount = r.ReadInt32();
            for (int i = 0; i < stateCount; i++)
            {
                var o = g.ReadObject();
                if (o == null || o.ClassName != "eCWeatherStates")
                    throw new InvalidDataException($"WeatherStates: expected eCWeatherStates, got {o?.ClassName}");
                var s = new WeatherState { ClassVersion = o.ClassVersion, Name = g.Str() };
                s.ReadPrecipitation(o, g);
                for (int c = 0; c < WeatherState.CurveCount; c++)
                    s.Curves[c] = ReadScale(r, WeatherState.CurveIsColor[c]);
                if (g.Position != o.End)
                    throw new InvalidDataException($"WeatherStates: state '{s.Name}' ended at {g.Position:X}, expected {o.End:X}");
                f.States.Add(s);
            }

            if (g.Position < g.PayloadEnd)
            {
                int envCount = r.ReadInt32();
                for (int i = 0; i < envCount; i++)
                {
                    var e = new WeatherEnvironment { Version = r.ReadUInt16(), Name = g.Str() };
                    int n = g.ArrayCount();
                    for (int k = 0; k < n; k++)
                        e.Probabilities.Add(new WeatherProbability
                        {
                            StateName = g.Str(),
                            Probability = r.ReadSingle(),
                            MinDuration = r.ReadSingle(),
                            MaxDuration = r.ReadSingle(),
                        });
                    e.MusicLocation = g.Str();
                    e.Climate = (AmbientClimate)r.ReadInt32();
                    f.Environments.Add(e);
                }
            }
            if (g.Position != g.PayloadEnd)
                throw new InvalidDataException($"WeatherStates: payload ended at {g.Position:X}, expected {g.PayloadEnd:X}");
            return f;
        }

        /// <summary>eCColorScale / eCFloatScale::Read: u32 version (1), u32 byteSize, u32 count, count × (f32 time, value).</summary>
        static Scale ReadScale(BinaryReader r, bool color)
        {
            uint ver = r.ReadUInt32();
            uint size = r.ReadUInt32();
            long start = r.BaseStream.Position;
            var s = new Scale { IsColor = color };
            if (ver != 1) { r.BaseStream.Position += size; return s; }
            if (size == 0) return s;
            int n = r.ReadInt32();
            int stride = color ? 5 : 2;
            if (size != n * stride * 4) throw new InvalidDataException($"WeatherStates: scale size {size} does not match {n} keys");
            s.Keys = new float[n * stride];
            for (int i = 0; i < s.Keys.Length; i++) s.Keys[i] = r.ReadSingle();
            // the count u32 is not included in 'size' (g3dit writes entries*20)
            r.BaseStream.Position = start + 4 + size;
            return s;
        }
    }

    public enum AmbientClimate { Mediterranean = 0, Arctic = 1, Desert = 2, Cave = 3 }

    /// <summary>Index of each curve inside <see cref="WeatherState.Curves"/>, in file order.</summary>
    public enum WeatherCurve
    {
        LightDiffuseColor = 0,
        LightSpecularColor = 1,
        LightIntensity = 2,
        AmbientBackLightColor = 3,
        AmbientGeneralColor = 4,
        AmbientIntensity = 5,
        FogColor = 6,
        FogStart = 7,
        SkyColor = 8,
        HazeColor = 9,
        FogEnd = 10,
        CloudThickness = 11,
        CloudColor = 12,
        GlowIntensity = 13,
        ShadowDensity = 14,
        Temperature = 15,
    }

    public sealed class WeatherState
    {
        public const int CurveCount = 16;
        public static readonly bool[] CurveIsColor =
        {
            true, true, false,          // LightDiffuse, LightSpecular, LightIntensity
            true, true, false,          // AmbientBackLight, AmbientGeneral, AmbientIntensity
            true, false,                // FogColor, FogStart
            true, true, false,          // SkyColor, HazeColor, FogEnd
            false, true, false,         // CloudThickness, CloudColor, GlowIntensity
            false, false,               // ShadowDensity, Temperature
        };

        public string Name;
        public ushort ClassVersion;
        public readonly Scale[] Curves = new Scale[CurveCount];

        // eCWeatherStates properties (precipitation / audio)
        public int ParticlesPerSecond;
        public float SpeedMin, SpeedMax, SizeX, SizeY;
        public float DirectionScale;
        public int CurrentTextureTile;
        public float SpawnPointDizzer, JitterPower, JitterSpeed, SplashDuration;
        public int SplashTextureTile;
        public float SplashSizeScaleX, SplashSizeScaleY;
        public string LoopSample;
        public float LoopVolume;

        public Scale this[WeatherCurve c] => Curves[(int)c];

        internal void ReadPrecipitation(PropertyObject o, GenomeReader g)
        {
            Property p;
            if ((p = o.Get("ParticlesPerSecond")) != null) ParticlesPerSecond = p.AsInt();
            if ((p = o.Get("SpeedMinMax")) != null) { SpeedMin = F(p, 0); SpeedMax = F(p, 1); }
            if ((p = o.Get("Size")) != null) { SizeX = F(p, 0); SizeY = F(p, 1); }
            if ((p = o.Get("DirectionScale")) != null) DirectionScale = p.AsFloat();
            if ((p = o.Get("CurrentTextureTile")) != null) CurrentTextureTile = p.AsInt();
            if ((p = o.Get("SpawnPointDizzer")) != null) SpawnPointDizzer = p.AsFloat();
            if ((p = o.Get("JitterPower")) != null) JitterPower = p.AsFloat();
            if ((p = o.Get("JitterSpeed")) != null) JitterSpeed = p.AsFloat();
            if ((p = o.Get("SplashDuration")) != null) SplashDuration = p.AsFloat();
            if ((p = o.Get("SplashTextureTile")) != null) SplashTextureTile = p.AsInt();
            if ((p = o.Get("SplashSizeScale")) != null) { SplashSizeScaleX = F(p, 0); SplashSizeScaleY = F(p, 1); }
            if ((p = o.Get("LoopSample")) != null) LoopSample = CString(g.StrFrom(p.Value));
            if ((p = o.Get("LoopVolume")) != null) LoopVolume = p.AsFloat();
        }

        static float F(Property p, int i) => BitConverter.ToSingle(p.Value, i * 4);

        /// <summary>The CP 1.75 file stores LoopSample in a 260-byte buffer: the name ends at the first NUL, the rest is garbage.</summary>
        static string CString(string s)
        {
            int z = s.IndexOf((char)0);
            return z >= 0 ? s.Substring(0, z) : s;
        }
    }

    /// <summary>
    /// Piecewise-linear key curve over the day (time 0..1, 0 = midnight). Keys flattened: color = (t,r,g,b,a), float = (t,v).
    /// Outside the key range the value wraps around midnight (last key → first key), which matches the data
    /// (all daily curves start and end on the same value).
    /// </summary>
    public sealed class Scale
    {
        public bool IsColor;
        public float[] Keys = Array.Empty<float>();

        public int Stride => IsColor ? 5 : 2;
        public int Count => Keys.Length / Stride;
        public float TimeAt(int i) => Keys[i * Stride];

        public float EvaluateFloat(float t) { var v = new float[1]; Evaluate(t, v); return v[0]; }

        public (float r, float g, float b, float a) EvaluateColor(float t)
        {
            var v = new float[4];
            Evaluate(t, v);
            return (v[0], v[1], v[2], v[3]);
        }

        /// <summary>Writes Stride-1 components into <paramref name="result"/>.</summary>
        public void Evaluate(float t, float[] result)
        {
            int st = Stride, dim = st - 1, n = Count;
            if (n == 0) { for (int k = 0; k < dim; k++) result[k] = 0; return; }
            t -= (float)Math.Floor(t);
            if (n == 1) { for (int k = 0; k < dim; k++) result[k] = Keys[1 + k]; return; }
            int a, b; float x;
            float t0 = Keys[0], tn = Keys[(n - 1) * st];
            if (t < t0 || t >= tn)
            {
                a = n - 1; b = 0;
                float span = (1f - tn) + t0;
                float d = t >= tn ? t - tn : t + 1f - tn;
                x = span > 1e-6f ? d / span : 0f;
            }
            else
            {
                a = 0;
                while (a < n - 2 && Keys[(a + 1) * st] <= t) a++;
                b = a + 1;
                float ta = Keys[a * st], tb = Keys[b * st];
                x = tb > ta ? (t - ta) / (tb - ta) : 0f;
            }
            for (int k = 0; k < dim; k++)
            {
                float va = Keys[a * st + 1 + k], vb = Keys[b * st + 1 + k];
                result[k] = va + (vb - va) * x;
            }
        }
    }

    public sealed class WeatherEnvironment
    {
        public ushort Version;
        public string Name;
        public readonly List<WeatherProbability> Probabilities = new List<WeatherProbability>();
        public string MusicLocation;
        public AmbientClimate Climate;
    }

    /// <summary>eCWeatherEnvironment::eSProbability. Durations are fractions of a game day (1/24 = one hour) — inferred from values.</summary>
    public sealed class WeatherProbability
    {
        public string StateName;
        public float Probability;
        public float MinDuration;
        public float MaxDuration;
    }
}
