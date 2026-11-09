using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Effects
{
    /// <summary>One key of a G3 scale curve (eCParticle_PS post data): time 0..1 over the particle's life, up to 4 values.</summary>
    public struct ScaleKey
    {
        public float Time, A, B, C, D;
        public ScaleKey(float t, float a, float b = 0, float c = 0, float d = 0) { Time = t; A = a; B = b; C = c; D = d; }
        public override string ToString() => $"{Time:0.###}:({A:0.###},{B:0.###},{C:0.###},{D:0.###})";
    }

    /// <summary>bCRange1 / bCRange3 axis: a random value between Min and Max (either order).</summary>
    public struct Range1
    {
        public float Min, Max;
        public Range1(float a, float b) { Min = a; Max = b; }
        public float Mid => (Min + Max) * 0.5f;
        public override string ToString() => $"[{Min:0.###}..{Max:0.###}]";
    }

    /// <summary>
    /// bCRange3 property value: the <b>min vector then the max vector</b> (x0 y0 z0 x1 y1 z1, 24 bytes), as the engine's
    /// bCRange3::SetMinMax(bCVector, bCVector). Axis i ranges between min[i] and max[i] (fire-fx: the data confirms it, e.g. the
    /// torch flame's StartVelocity −1,−5,−1 / 1,100,1 = up to 1 m/s upwards; StartBoxLocation −1600,0,−1600 / 1600,0,1600 = a
    /// flat box). Docs/formats-effects.md §2.
    /// </summary>
    public static class Range3
    {
        public static void Read(byte[] v, Range1[] into)
        {
            if (v == null || v.Length < 24) return;
            for (int i = 0; i < 3; i++) into[i] = new Range1(BitConverter.ToSingle(v, i * 4), BitConverter.ToSingle(v, 12 + i * 4));
        }
    }

    /// <summary>eELocationShape</summary>
    public enum LocationShape { Box = 0, Sphere = 1 }
    /// <summary>eECoordinateSystem: Independent = particles stay in the world, Relative = they move with the emitter.</summary>
    public enum ParticleSpace { Independent = 0, Relative = 1 }
    /// <summary>eEVelocityDirectionFrom (UE2 PTVD_*): with a direction mode StartVelocity.X is the speed.</summary>
    public enum VelocityDirectionFrom { None = 0, StartPositionAndOwner = 1, OwnerAndStartPosition = 2 }
    /// <summary>eEFacingDirection</summary>
    public enum FacingDirection { FacingCamera = 0, AlongMovementFacingCamera = 1, SpecifiedNormal = 2, AlongMovementFacingNormal = 3, PerpendicularToMovement = 4 }
    /// <summary>eERotationFrom</summary>
    public enum RotationFrom { None = 0, Entity = 1, Offset = 2, Normal = 3 }
    /// <summary>eETextureDrawStyle (UE2 EParticleDrawStyle)</summary>
    public enum DrawStyle { Regular = 0, AlphaBlend = 1, Modulated = 2, Translucent = 3, AlphaModulate = 4, Darken = 5, Brighten = 6, Invisible = 7 }
    /// <summary>eELightingStyle</summary>
    public enum LightingStyle { Disabled = 0, Flat = 1, Particle = 2, System = 3 }

    /// <summary>
    /// eCParticle_PS (Engine.dll), a port of the Unreal Engine 2 sprite emitter. Docs/formats-effects.md §2–3.
    /// Distances in centimetres, times in seconds, angles in revolutions (1 = 360°). Property block + post data:
    /// five scale curves (SubdivisionScale float, RevolutionScale vec3, VelocityScale vec3, ColorScale rgba, SizeScale vec3)
    /// each as {u32 version 1, u32 byteSize, [u32 count, keys]} and a trailer {u32 version, u32 n, ...} that is skipped.
    /// </summary>
    public sealed class ParticleEmitterDef
    {
        public string Name;
        public bool Enabled = true;
        public int MaxParticles;
        public float ParticlesPerSecond;
        public bool RespawnDeadParticles, AutomaticSpawning, AutoReset, AutoKill;
        public Range1 Lifetime, InitialAge, InitialDelay;
        public float RelativeWarmupTime;

        public LocationShape StartShape;
        public Range1[] StartBox = new Range1[3];
        public Range1 StartSphereRadius;
        public float[] StartOffset = new float[3];
        public ParticleSpace Space;

        public Range1[] StartVelocity = new Range1[3];
        public Range1[] VelocityLoss = new Range1[3];
        public float[] Acceleration = new float[3];
        public float[] MaxAbsoluteVelocity = { 10000, 10000, 10000 };
        public bool UseVelocityScale;
        public float VelocityScaleRepeats;
        public VelocityDirectionFrom DirectionFrom;

        public bool SpinParticles;
        public Range1[] StartSpin = new Range1[3];
        public Range1[] SpinsPerSecond = new Range1[3];
        /// <summary>Probability (0..1) that a particle spins clockwise (UE2 SpinCCWorCW).</summary>
        public float SpinDirection = 0.5f;
        public FacingDirection Facing;
        public float[] ProjectionNormal = { 0, 1, 0 };
        public RotationFrom UseRotationFrom;

        public bool UseRevolution;
        public Range1[] RevolutionsPerSecond = new Range1[3];
        public Range1[] RevolutionCenterOffset = new Range1[3];
        public bool UseRevolutionScale;
        public float RevolutionScaleRepeats;

        public bool UniformSize;
        public Range1[] StartSize = new Range1[3];
        public bool UseSizeScale;
        public float SizeScaleRepeats;
        public bool StretchToEmitter;

        public string Material;           // "" or e.g. G3_Special_HeatHaze_01.xshmat (distortion)
        public string Texture;            // e.g. VFX_Env_Torch_01.tga
        public DrawStyle DrawStyle;
        public int NumU, NumV, SubdivisionStart, SubdivisionEnd;
        public bool RandomSubdivision, BlendBetweenSubdivisions, SubdivisionRowMajor, UseSubdivisionScale;
        public float SubdivisionScaleRepeats;

        public bool FadeIn, FadeOut;
        public float FadeInEndTime, FadeOutStartTime;
        /// <summary>r, g, b, a fade factors (1 = the channel fades fully).</summary>
        public float[] FadeInFactor = { 1, 1, 1, 1 }, FadeOutFactor = { 1, 1, 1, 1 };
        public bool UseColorScale;
        public float ColorScaleRepeats;
        public bool DisableFogging, AlphaTest, DepthTest = true, DepthWrite;
        public int AlphaReference = 127;
        public LightingStyle Lighting;

        public bool UseCollision;
        /// <summary>Velocity multiplier per axis on a bounce (1 = no loss).</summary>
        public Range1[] DampingFactor = new Range1[3];
        /// <summary>With UseMaxCollisions a particle stays where its MaxCollisions-th hit put it (see EmitterRules.Collision).</summary>
        public bool UseMaxCollisions;
        public Range1 MaxCollisions;
        public bool CollideWithStatic = true, CollideWithDynamic;

        /// <summary>Key value: subdivision index.</summary>
        public readonly List<ScaleKey> SubdivisionScale = new List<ScaleKey>();
        /// <summary>Key values: x, y, z multipliers of RevolutionsPerSecond.</summary>
        public readonly List<ScaleKey> RevolutionScale = new List<ScaleKey>();
        /// <summary>Key values: x, y, z velocity multipliers.</summary>
        public readonly List<ScaleKey> VelocityScale = new List<ScaleKey>();
        /// <summary>Key values: r, g, b, a.</summary>
        public readonly List<ScaleKey> ColorScale = new List<ScaleKey>();
        /// <summary>Key values: x, y, z size multipliers.</summary>
        public readonly List<ScaleKey> SizeScale = new List<ScaleKey>();

        public bool IsHeatHaze => Material != null && Material.IndexOf("HeatHaze", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Reads an eCParticle_PS property object; <paramref name="postStart"/> = stream offset after its class version.</summary>
        public static ParticleEmitterDef Read(PropertyObject ps, GenomeReader g, long postStart)
        {
            var d = new ParticleEmitterDef();
            d.Enabled = Bool(ps, "Enabled", true);
            d.MaxParticles = Int(ps, "MaxNumParticles");
            d.ParticlesPerSecond = Float(ps, "ParticlesPerSecond");
            d.RespawnDeadParticles = Bool(ps, "RespawnDeadParticles");
            d.AutomaticSpawning = Bool(ps, "AutomaticSpawning");
            d.AutoReset = Bool(ps, "AutoReset");
            d.AutoKill = Bool(ps, "AutoKill");
            d.Lifetime = R1(ps, "Lifetime");
            d.InitialAge = R1(ps, "InitialAge");
            d.InitialDelay = R1(ps, "InitialDelay");
            d.RelativeWarmupTime = Float(ps, "RelativeWarmupTime");
            d.StartShape = (LocationShape)Enum(ps, "StartLocationShape");
            R3(ps, "StartBoxLocation", d.StartBox);
            d.StartSphereRadius = R1(ps, "StartSphereRadius");
            V3(ps, "StartLocationOffset", d.StartOffset);
            d.Space = (ParticleSpace)Enum(ps, "CoordinateSystem");
            R3(ps, "StartVelocity", d.StartVelocity);
            R3(ps, "VelocityLoss", d.VelocityLoss);
            V3(ps, "Acceleration", d.Acceleration);
            V3(ps, "MaxAbsoluteVelocity", d.MaxAbsoluteVelocity);
            d.UseVelocityScale = Bool(ps, "UseVelocityScale");
            d.VelocityScaleRepeats = Float(ps, "VelocityScaleRepeats");
            d.DirectionFrom = (VelocityDirectionFrom)Enum(ps, "VelocityDirectionFrom");
            d.SpinParticles = Bool(ps, "SpinParticles");
            R3(ps, "StartSpin", d.StartSpin);
            R3(ps, "SpinsPerSecond", d.SpinsPerSecond);
            d.SpinDirection = Float(ps, "SpinDirection", 0.5f);
            d.Facing = (FacingDirection)Enum(ps, "FacingDirection");
            V3(ps, "ProjectionNormal", d.ProjectionNormal);
            d.UseRotationFrom = (RotationFrom)Enum(ps, "UseRotationFrom");
            d.UseRevolution = Bool(ps, "UseRevolution");
            R3(ps, "RevolutionsPerSecond", d.RevolutionsPerSecond);
            R3(ps, "RevolutionCenterOffset", d.RevolutionCenterOffset);
            d.UseRevolutionScale = Bool(ps, "UseRevolutionScale");
            d.RevolutionScaleRepeats = Float(ps, "RevolutionScaleRepeats");
            d.UniformSize = Bool(ps, "UniformSize");
            R3(ps, "StartSize", d.StartSize);
            d.UseSizeScale = Bool(ps, "UseSizeScale");
            d.SizeScaleRepeats = Float(ps, "SizeScaleRepeats");
            d.StretchToEmitter = Bool(ps, "StretchToEmitter");
            d.Material = Str(g, ps, "Material");
            d.Texture = Str(g, ps, "Texture");
            d.DrawStyle = (DrawStyle)Enum(ps, "DrawStyle");
            d.NumU = Int(ps, "NumUSubdivisions");
            d.NumV = Int(ps, "NumVSubdivisions");
            d.SubdivisionStart = Int(ps, "SubdivisionStart");
            d.SubdivisionEnd = Int(ps, "SubdivisionEnd");
            d.RandomSubdivision = Bool(ps, "RandomSubdivision");
            d.BlendBetweenSubdivisions = Bool(ps, "BlendBetweenSubdivisions");
            d.SubdivisionRowMajor = Bool(ps, "SubdivisionRowMajor");
            d.UseSubdivisionScale = Bool(ps, "UseSubdivisionScale");
            d.SubdivisionScaleRepeats = Float(ps, "SubdivisionScaleRepeats");
            d.FadeIn = Bool(ps, "FadeIn");
            d.FadeOut = Bool(ps, "FadeOut");
            d.FadeInEndTime = Float(ps, "FadeInEndTime");
            d.FadeOutStartTime = Float(ps, "FadeOutStartTime");
            AlphaColor(ps, "FaceInFactor", d.FadeInFactor);
            AlphaColor(ps, "FaceOutFactor", d.FadeOutFactor);
            d.UseColorScale = Bool(ps, "UseColorScale");
            d.ColorScaleRepeats = Float(ps, "ColorScaleRepeats");
            d.DisableFogging = Bool(ps, "DisableFogging");
            d.AlphaTest = Bool(ps, "AlphaTest");
            var ar = ps.Get("AlphaReference");
            if (ar != null && ar.Value.Length > 0) d.AlphaReference = ar.Value[0];
            d.DepthTest = Bool(ps, "DepthTest", true);
            d.DepthWrite = Bool(ps, "DepthWrite");
            d.Lighting = (LightingStyle)Enum(ps, "LightingStyle");
            d.UseCollision = Bool(ps, "UseCollision");
            R3(ps, "DampingFactor", d.DampingFactor);
            d.UseMaxCollisions = Bool(ps, "UseMaxCollisions");
            d.MaxCollisions = R1(ps, "MaxCollisions");
            d.CollideWithStatic = Bool(ps, "CollideWithStatic", true);
            d.CollideWithDynamic = Bool(ps, "CollideWithDynamic");

            var r = g.R;
            long saved = g.Position;
            g.Position = postStart;
            ReadScale(r, 1, d.SubdivisionScale);
            ReadScale(r, 3, d.RevolutionScale);
            ReadScale(r, 3, d.VelocityScale);
            ReadScale(r, 4, d.ColorScale);
            ReadScale(r, 3, d.SizeScale);
            if (g.Position > ps.End) throw new InvalidDataException("eCParticle_PS: scales run past the object end");
            g.Position = saved;
            return d;
        }

        /// <summary>eCFloatScale / eCVectorScale / eCColorScale: u32 version (1), u32 byteSize, [u32 count, count × (f32 time, values)].</summary>
        static void ReadScale(BinaryReader r, int values, List<ScaleKey> into)
        {
            uint ver = r.ReadUInt32();
            if (ver != 1) throw new InvalidDataException($"eCParticle_PS: scale version {ver}");
            uint size = r.ReadUInt32();
            if (size == 0) return;
            int count = r.ReadInt32();
            if (count < 0 || count * (1 + values) * 4 != size) throw new InvalidDataException($"eCParticle_PS: scale size {size} for {count} keys");
            for (int i = 0; i < count; i++)
            {
                var k = new ScaleKey { Time = r.ReadSingle(), A = r.ReadSingle() };
                if (values > 1) k.B = r.ReadSingle();
                if (values > 2) k.C = r.ReadSingle();
                if (values > 3) k.D = r.ReadSingle();
                into.Add(k);
            }
        }

        static bool Bool(PropertyObject ps, string n, bool def = false) { var p = ps.Get(n); return p == null || p.Value.Length == 0 ? def : p.Value[0] != 0; }
        static float Float(PropertyObject ps, string n, float def = 0) { var p = ps.Get(n); return p == null || p.Value.Length < 4 ? def : BitConverter.ToSingle(p.Value, 0); }
        static int Int(PropertyObject ps, string n) { var p = ps.Get(n); return p == null || p.Value.Length < 4 ? 0 : BitConverter.ToInt32(p.Value, 0); }
        static int Enum(PropertyObject ps, string n) { var p = ps.Get(n); return p == null ? 0 : p.AsEnum(); }
        static string Str(GenomeReader g, PropertyObject ps, string n) { var p = ps.Get(n); return p == null ? "" : g.StrFrom(p.Value); }
        static Range1 R1(PropertyObject ps, string n)
        {
            var p = ps.Get(n);
            return p == null || p.Value.Length < 8 ? default : new Range1(BitConverter.ToSingle(p.Value, 0), BitConverter.ToSingle(p.Value, 4));
        }
        static void R3(PropertyObject ps, string n, Range1[] into) => Range3.Read(ps.Get(n)?.Value, into);
        static void V3(PropertyObject ps, string n, float[] into)
        {
            var p = ps.Get(n);
            if (p == null || p.Value.Length < 12) return;
            for (int i = 0; i < 3; i++) into[i] = BitConverter.ToSingle(p.Value, i * 4);
        }
        /// <summary>bCFloatAlphaColor property value: 4 unused bytes, then r, g, b, a.</summary>
        static void AlphaColor(PropertyObject ps, string n, float[] into)
        {
            var p = ps.Get(n);
            if (p == null || p.Value.Length < 20) return;
            for (int i = 0; i < 4; i++) into[i] = BitConverter.ToSingle(p.Value, 4 + i * 4);
        }
    }

    /// <summary>eCPointLight_PS of an effect template. Colours 0..1 (gamma), Range cm.</summary>
    public sealed class EffectLightDef
    {
        public float R = 1, G = 1, B = 1;
        public float Range = 1000, Intensity = 1;
        public float[] Offset = new float[3];
        /// <summary>eEDynamicLightEffect [inferred order: 0 Steady, 1 Flicker, 2 Pulse, 3 Blink, 4 Strobe].</summary>
        public int Effect;
        public float EffectPhase, EffectPeriod = 1;
        public bool CastShadows, Enabled = true;

        public static EffectLightDef Read(PropertyObject ps)
        {
            var l = new EffectLightDef();
            var c = ps.Get("Color");
            if (c != null && c.Value.Length >= 16) { l.R = BitConverter.ToSingle(c.Value, 4); l.G = BitConverter.ToSingle(c.Value, 8); l.B = BitConverter.ToSingle(c.Value, 12); }
            l.Range = ps.Get("Range")?.AsFloat() ?? 1000;
            l.Intensity = ps.Get("Intensity")?.AsFloat() ?? 1;
            var o = ps.Get("PositionOffset") ?? ps.Get("Offset");
            if (o != null && o.Value.Length >= 12) for (int i = 0; i < 3; i++) l.Offset[i] = BitConverter.ToSingle(o.Value, i * 4);
            l.Effect = ps.Get("Effect")?.AsEnum() ?? 0;
            l.EffectPhase = ps.Get("EffectPhase")?.AsFloat() ?? 0;
            l.EffectPeriod = ps.Get("EffectPeriod")?.AsFloat() ?? 1;
            l.CastShadows = ps.Get("CastShadows")?.AsBool() ?? false;
            l.Enabled = ps.Get("Enabled")?.AsBool() ?? true;
            return l;
        }
    }

    /// <summary>
    /// What an effect template (VFX_*.tple) shows: in the retail data every such template has exactly one real entity
    /// (header 1 under the helper header 0) carrying at most one eCParticle_PS and/or eCPointLight_PS. Docs/formats-effects.md §4.
    /// </summary>
    public sealed class VfxTemplateDef
    {
        public string Name;
        public Guid Guid;
        public ParticleEmitterDef Emitter;
        public EffectLightDef Light;
        /// <summary>Sound of an eCAudioEmitter_PS (played by the audio side: G3AudioWorldBaker / G3EffectSounds).</summary>
        public string Sound;

        public bool IsEmpty => Emitter == null && Light == null;

        public static VfxTemplateDef From(Templates.TemplateFile f)
        {
            var d = new VfxTemplateDef { Name = f.Name, Guid = f.Guid };
            foreach (var h in f.Headers)
                for (int i = 0; i < h.PropertySets.Count; i++)
                {
                    var ps = h.PropertySets[i];
                    switch (ps.ClassName)
                    {
                        case "eCParticle_PS":
                            if (d.Emitter == null) { d.Emitter = ParticleEmitterDef.Read(ps, h.Source, h.PostDataStart[i]); d.Emitter.Name = f.Name; }
                            break;
                        case "eCPointLight_PS":
                            if (d.Light == null) d.Light = EffectLightDef.Read(ps);
                            break;
                        case "eCAudioEmitter_PS":
                            if (d.Sound == null) d.Sound = Items.Props.Str(h.Source, ps, "Sound");
                            break;
                    }
                }
            return d;
        }
    }
}
