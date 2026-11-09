using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Audio
{
    /// <summary>One weighted sample of a gCEffectCommandPlaySound.</summary>
    public struct EffectSample
    {
        public string Sample;          // as stored (.wav or .ogg); resolve by base name
        public float Probability;      // relative weight (all 1.0 in the shipped data)
    }

    /// <summary>gCEffectCommandPlaySound: pick one sample at TimeOffset (seconds) and play it 3D.</summary>
    public sealed class EffectSound
    {
        public float Volume = 1, MinDistance, MaxDistance, TimeOffset;   // cm, s
        public readonly List<EffectSample> Samples = new List<EffectSample>();
    }

    /// <summary>
    /// gCEffectCommandSpawnEntity: spawn a template (VFX_*) when the effect starts. Link (gEEffectLink): 0 Independent
    /// (left in the world), 1 TargetEntity (follows the entity the effect plays on), 2 TargetBone (follows BoneName).
    /// AutoKill != 0: the spawned entity dies with the effect. Offset cm (random in range), TimeOffset s. Docs/formats-effects.md §1.
    /// </summary>
    public sealed class EffectSpawn
    {
        public Guid Template;
        public string BoneName;
        public int Link;
        public bool AutoKill;
        public float[] OffsetMin = new float[3], OffsetMax = new float[3];
        public float TimeOffset;
    }

    /// <summary>One named effect (gCEffectCommandSequence). Only the audio-relevant commands are decoded.</summary>
    public sealed class Effect
    {
        public string Name;
        public readonly List<EffectSound> Sounds = new List<EffectSound>();
        /// <summary>Class names of all commands, in order (gCEffectCommandSpawnEntity, …PlaySound, …MusicTrigger, …).</summary>
        public readonly List<string> Commands = new List<string>();
        /// <summary>gCEffectCommandMusicTrigger properties (location/situation/…); none in the shipped g3.efm.</summary>
        public readonly List<MusicTrigger> MusicTriggers = new List<MusicTrigger>();
        /// <summary>Template GUIDs of gCEffectCommandSpawnEntity (eCTemplateEntityProxy). Fire/ambience effects play
        /// their sound through a spawned template that carries an eCAudioEmitter_PS (e.g. VFX_Env_Fire_Campfire_02).</summary>
        public readonly List<Guid> SpawnTemplates = new List<Guid>();
        /// <summary>Every gCEffectCommandSpawnEntity with its link settings (parallel to the commands; additive, effects).</summary>
        public readonly List<EffectSpawn> Spawns = new List<EffectSpawn>();
    }

    /// <summary>
    /// Reader for <c>Library/g3.efm</c> (gCEffectMap, file name hard-coded in Game.dll). Docs/formats-audio.md §4.
    /// Payload: u16 version (2), u32 count, count × { u16 name; u16 sequenceVersion (1); u32 n; n × command object }.
    /// gCEffectCommandPlaySound (class version 2) is followed by u32 k, k × { str sample; f32 probability }.
    /// Works for GENOMFLE (g3.efm) and inline-string archives (g3_kairo.efm, an older unused copy).
    /// </summary>
    public sealed class EffectMap
    {
        public ushort Version;
        public readonly Dictionary<string, Effect> Effects = new Dictionary<string, Effect>(StringComparer.OrdinalIgnoreCase);

        public Effect Get(string name) => name != null && Effects.TryGetValue(name, out var e) ? e : null;

        public static EffectMap Read(byte[] data)
        {
            var g = new GenomeReader(data);
            var r = g.R;
            var map = new EffectMap { Version = r.ReadUInt16() };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var e = new Effect { Name = g.Str() };
                r.ReadUInt16();
                int cmds = r.ReadInt32();
                for (int c = 0; c < cmds; c++)
                {
                    var o = g.ReadObject();
                    if (o == null) continue;
                    e.Commands.Add(o.ClassName);
                    if (o.ClassName == "gCEffectCommandPlaySound")
                    {
                        var s = new EffectSound
                        {
                            Volume = o.Get("Volume")?.AsFloat() ?? 1f,
                            MinDistance = o.Get("MinDistance")?.AsFloat() ?? 0f,
                            MaxDistance = o.Get("MaxDistance")?.AsFloat() ?? 0f,
                            TimeOffset = o.Get("TimeOffset")?.AsFloat() ?? 0f,
                        };
                        int k = r.ReadInt32();
                        for (int j = 0; j < k; j++)
                            s.Samples.Add(new EffectSample { Sample = g.Str(), Probability = r.ReadSingle() });
                        if (g.Position != o.End) throw new InvalidDataException($"efm: PlaySound of {e.Name} ends at {g.Position:X}, expected {o.End:X}");
                        e.Sounds.Add(s);
                    }
                    else if (o.ClassName == "gCEffectCommandSpawnEntity")
                    {
                        // eCTemplateEntityProxy value: u16 version, u8 hasGuid, GUID (16 bytes)
                        var p = o.Get("TemplateEntity")?.Value;
                        if (p != null && p.Length >= 19 && p[2] != 0)
                        {
                            var b = new byte[16];
                            Array.Copy(p, 3, b, 0, 16);
                            e.SpawnTemplates.Add(new Guid(b));
                            var sp = new EffectSpawn
                            {
                                Template = new Guid(b), BoneName = MusicLibrary.Str(g, o, "BoneName") ?? "",
                                Link = o.Get("CoordinateSystem")?.AsEnum() ?? 0,
                                AutoKill = o.Get("AutoKill") is { } ak && ak.Value.Length > 0 && ak.Value[0] != 0,
                                TimeOffset = o.Get("TimeOffset")?.AsFloat() ?? 0,
                            };
                            var off = o.Get("Offset")?.Value;
                            if (off != null && off.Length >= 24)
                                for (int a = 0; a < 3; a++) { sp.OffsetMin[a] = BitConverter.ToSingle(off, a * 4); sp.OffsetMax[a] = BitConverter.ToSingle(off, 12 + a * 4); } // bCRange3: min vector, max vector
                            e.Spawns.Add(sp);
                        }
                    }
                    else if (o.ClassName == "gCEffectCommandMusicTrigger")
                    {
                        e.MusicTriggers.Add(new MusicTrigger
                        {
                            Location = MusicLibrary.Str(g, o, "Location"), Situation = MusicLibrary.Str(g, o, "Situation"),
                            DayTime = MusicLibrary.Str(g, o, "DayTime"), Variation = MusicLibrary.Str(g, o, "Variation"),
                            Transition = MusicLibrary.Str(g, o, "Transition"),
                            TransitionMode = (MusicTransitionMode)(o.Get("TransitionMode")?.AsEnum() ?? 0),
                            Exit = (MusicTriggerTime)(o.Get("Exit")?.AsEnum() ?? 0),
                            Entrance = (MusicFragmentPosition)(o.Get("Entrance")?.AsEnum() ?? 0),
                            IsStopTrigger = o.Get("IsStopTrigger")?.AsBool() ?? false,
                        });
                    }
                    g.Position = o.End;
                }
                map.Effects[e.Name] = e;
            }
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"efm: {g.PayloadEnd - g.Position} bytes left over");
            return map;
        }
    }

    /// <summary>
    /// Footstep effect names. Game.dll builds "EFF_Step_" + species + "_Walk_|_Run_|_Sprint_|_Land_|_Stop_" + … +
    /// material name + "_01" from the floor's eEShapeMaterial (gCCharacterMovement_PS::OnFloorMaterialChanged).
    /// The material-name table in Game.dll lists Earth, Sand, Damage, Grass, Foliage, Debris, Snow, Flesh, Glass, Clay,
    /// Leather, Stone, Water, Metal, Wood; g3.efm only has effects for earth, grass, sand, snow, stone, water, wood
    /// (human) and earth (creatures). Exact concatenation order (side token) is [inferred] from the effect names.
    /// </summary>
    public static class Footsteps
    {
        /// <summary>eEShapeMaterial → footstep material token present in g3.efm; others fall back to "earth" [inferred].</summary>
        public static string MaterialToken(int shapeMaterial)
        {
            switch (shapeMaterial)
            {
                case 1: return "wood";      // Wood
                case 3: return "water";     // Water
                case 4: return "stone";     // Stone
                case 2: return "stone";     // Metal: no metal steps in g3.efm [inferred fallback]
                case 9: return "stone";     // Glass [inferred]
                case 5: return "earth";     // Earth
                case 6: return "snow";      // Ice [inferred]
                case 11: return "snow";     // Snow
                case 15: return "grass";    // Grass
                case 13: return "grass";    // Foliage [inferred]
                case 20: return "sand";     // Sand
                default: return "earth";
            }
        }

        /// <summary>e.g. ("human", "walk", "left", 15) → "eff_step_human_walk_left_grass_01".</summary>
        public static string EffectName(string species, string gait, string side, int shapeMaterial) =>
            side == null
                ? $"eff_step_{species}_{gait}_{MaterialToken(shapeMaterial)}_01"
                : $"eff_step_{species}_{gait}_{side}_{MaterialToken(shapeMaterial)}_01";
    }
}
