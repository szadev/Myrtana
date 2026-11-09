using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Audio;
using MusicLibrary = G3.Formats.Audio.MusicLibrary;
using AmbientLibrary = G3.Formats.Audio.AmbientLibrary;
using G3.Formats.Pak;
using G3.Unity.Editor;

/// <summary>
/// `G3Cli audio sim`: exercises the engine-free runtime audio logic (Assets/G3/Unity/Runtime/Audio/Logic) on the real
/// g3.mul / g3.ams / world zones without Unity: zone → music state at sample points, a scripted music session
/// (explore → fight → explore → other location → night) with coverage checks, the ambient spawner and the footstep
/// surface table. Exit code 1 if a check fails.
/// </summary>
static partial class Program
{
    static int AudioSim(string data)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) failures++; }

        // --- catalog
        HashSet<string> shipped;
        using (var fs = new G3FileSystem(data, "Music"))
            shipped = new HashSet<string>(fs.Files.Select(f => AudioCatalogConverter.ClipKey(f.Path)));
        var music = AudioCatalogConverter.Music(MusicLibrary.Read(AudioLib(data, "g3.mul")), shipped.Contains);
        Console.WriteLine($"music catalog: {music.fragments.Count} fragments, {music.transitions.Count} transitions");
        Check(music.fragments.All(f => shipped.Contains(f.clip)), "every fragment sample is shipped");

        // --- zones
        var world = LoadAudioWorld(data);
        var zones = world.Zones.Select(AudioCatalogConverter.Zone).ToList();
        var map = new MusicZoneMap(zones);
        var resolver = new MusicStateResolver(music);
        Console.WriteLine($"\nmusic zones: {map.Count} usable of {zones.Count}");
        V3 Centre(string name) => zones.First(z => z.name == name).centre;
        var points = new List<(string label, V3 p, string expectLoc)>
        {
            ("Faring square", new V3(655, 90, 586), "faring"),
            ("Faring Innos temple", new V3(616, 82, 635), "slaves"),
            ("Nordmar region centre", Centre("Nordmar_WeatherZone_01"), null),
            ("Varant region centre", Centre("Varant_WeatherZone_01"), null),
            ("Varant sand N of Braga", new V3(-887, 49, -397), "varant"),
            ("Braga edge", new V3(-940, 24, -542), "braga"),
            ("Mora Sul arena", new V3(-1015, 54, -1494), "arena"),
            ("Lago", new V3(-159, 12, -527), "braga"),
        };
        foreach (var (label, p, expect) in points)
        {
            var z = map.ZoneAt(p);
            var s = resolver.Resolve(z, null, 12);
            var n = resolver.Resolve(z, null, 23);
            var f = resolver.Resolve(z, MusicSituations.Fight, 12);
            Console.WriteLine($"  {label,-22} {p}  zone {z?.name ?? "-"} ({z?.weatherEnvironment})  → {s} | night {n} | fight {f} | climate {AmbientRules.ClimateOf(z)}");
            if (expect != null) Check(s.Location == expect, $"{label} plays {expect}");
            Check(music.FindFragments(s.Location, s.Situation, s.DayTime).Count > 0, $"{label} has a fragment");
            Check(f.Situation == MusicSituations.Fight && music.FindFragments(f.Location, f.Situation, f.DayTime).Any(x => x.situation == "fight"), $"{label} fight resolves to a fight piece");
        }

        // --- sequencer session
        Console.WriteLine("\nsequencer session (simulated DSP clock):");
        var seq = new MusicSequencer(music, new Random(1));
        seq.Scheduled += c => Console.WriteLine($"    t={c.Start,7:F2}  {(c.IsTransition ? "transition" : "fragment  ")} {c.Clip,-36} from {c.Offset,6:F2}s  len {c.Length,6:F1}");
        var script = new List<(double t, MusicState s, MusicEntrance e, double wait)>
        {
            (0, new MusicState("faring", "explore", "day"), MusicEntrance.LoopEntrance, 15),
            (40, new MusicState("myrtana", "fight", "day"), MusicEntrance.Begin, 3),
            (75, new MusicState("faring", "explore", "day"), MusicEntrance.LoopEntrance, 15),
            (130, new MusicState("varant", "explore", "day"), MusicEntrance.LoopEntrance, 15),
            (200, new MusicState("varant", "explore", "night"), MusicEntrance.LoopEntrance, 15),
            (300, new MusicState("dungeon", "explore", "day"), MusicEntrance.LoopEntrance, 15),
        };
        const double dt = 0.02, end = 900;
        int si = 0;
        double silent = 0, maxSilent = 0;
        int maxFragments = 0;
        var arrival = new List<string>();
        MusicState? pending = null;
        for (double t = 0; t < end; t += dt)
        {
            while (si < script.Count && script[si].t <= t)
            {
                seq.SetTarget(script[si].s, script[si].e, script[si].wait);
                pending = script[si].s;
                Console.WriteLine($"  t={t,7:F2}  target {script[si].s}");
                si++;
            }
            seq.Update(t);
            bool audible = false;
            int frags = 0;
            foreach (var c in seq.Cues)
            {
                if (t < c.Start || t >= c.End) continue;
                float g = c.Gain.Evaluate(t);
                if (g > 0.05f) audible = true;
                if (!c.IsTransition && g > 0.05f) frags++;
            }
            maxFragments = Math.Max(maxFragments, frags);
            silent = audible ? 0 : silent + dt;
            if (t > 1) maxSilent = Math.Max(maxSilent, silent);
            if (pending.HasValue && seq.Current?.Fragment != null && seq.Current.Fragment.Is(pending.Value.Location, pending.Value.Situation, pending.Value.DayTime))
            {
                arrival.Add($"{pending.Value} after {t - script[si - 1].t:F1}s");
                pending = null;
            }
        }
        Console.WriteLine("  arrivals: " + string.Join("; ", arrival));
        Check(arrival.Count == script.Count, "every target state was reached");
        Check(maxSilent < 0.5, $"no silence gap (longest {maxSilent:F2}s)");
        Check(maxFragments <= 2, $"at most two audible fragments at once ({maxFragments})");

        // --- fix-177: a fight that is over within one situation tick leaves no fight music behind
        var shortSeq = new MusicSequencer(music, new Random(3));
        int cancelled = 0;
        shortSeq.Cancelled += c => cancelled++;
        var shortExplore = new MusicState("faring", "explore", "day");
        shortSeq.SetTarget(shortExplore, MusicTriggers.Explore);
        double fightAfter = 0;
        for (double t = 0; t < 120; t += dt)
        {
            if (Math.Abs(t - 30) < dt / 2) shortSeq.SetTarget(new MusicState("myrtana", "fight", "day"), MusicTriggers.Fight);
            if (Math.Abs(t - 31) < dt / 2) shortSeq.SetTarget(shortExplore, MusicTriggers.Explore);
            shortSeq.Update(t);
            if (t < 31 + shortSeq.FallbackCrossfade) continue;
            foreach (var c in shortSeq.Cues)
                if (c.Fragment != null && c.Fragment.situation == "fight" && t >= c.Start && t < c.End && c.Gain.Evaluate(t) > 0.001f) fightAfter += dt;
        }
        Console.WriteLine($"\n  1 s fight at t=30: {cancelled} change(s) undone, fight audible {fightAfter:F1}s after the fade back, playing {shortSeq.Current?.Clip}");
        Check(cancelled == 1 && fightAfter == 0 && shortSeq.Current?.Fragment?.situation == "explore", "a 1 s fight leaves no fight music behind");

        // --- ambient
        var ambient = AudioCatalogConverter.Ambient(AmbientLibrary.Read(AudioLib(data, "g3.ams")));
        var spawner = new AmbientSpawner(ambient, new Random(2));
        int spawns = 0;
        for (double t = 0; t < 600; t += 0.25)
        {
            var s = spawner.Update(t, AmbientClimate.Mediterranean, AmbientEnvironment.Forest, AmbientDayTime.Day);
            if (s == null) continue;
            spawns++;
            spawner.NotifyStarted(t, 4);
        }
        Console.WriteLine($"\nambient: {ambient.sounds.Count} sounds; Mediterranean forest day → {spawns} one-shots in 10 min");
        Check(spawns > 10 && spawns < 200, "ambient spawn rate plausible");
        Check(spawner.Update(0, AmbientClimate.Arctic, AmbientEnvironment.Forest, AmbientDayTime.Day) == null, "no Arctic one-shots (none in g3.ams)");

        // --- footsteps
        Console.WriteLine("\nfootstep surfaces:");
        foreach (var (name, expect) in new[]
        {
            ("G3_Nature_Ground_Grass_01", "grass"), ("G3_Architecture_SandStone_01_A", "stone"), ("G3_Nature_Dunes_PlainSand_01", "sand"),
            ("G3_Architecture_Wood_01_A", "wood"), ("G3_Nature_Forest_Snowground_01", "snow"), ("G3_Myrtana_Dungeon_Ground_01_A_2_Grass_01", "stone"),
            ("G3_Nature_Ground_Path_01", "earth"), ("G3_Architecture_GroundPlates_01_A", "stone"),
        })
        {
            var s = FootstepRules.SurfaceFromName(name);
            Check(s == expect, $"{name} → {s}");
        }
        Check(FootstepRules.EffectName(FootstepGait.Run, true, "grass") == "eff_step_human_run_left_grass_01", "footstep effect name");

        // --- IMA-ADPCM → PCM (the importer converts these)
        Console.WriteLine("\nIMA-ADPCM conversion:");
        using (var fs = new G3FileSystem(data, "Sound"))
        {
            int converted = 0;
            foreach (var e in fs.Files.Where(f => f.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = e.Read();
                if (!G3.Formats.Audio.ImaAdpcm.IsImaAdpcmWav(bytes)) continue;
                var src = G3.Formats.Audio.AudioFileInfo.Read(bytes);
                var pcm = G3.Formats.Audio.AudioFileInfo.Read(G3.Formats.Audio.ImaAdpcm.ToPcm16(bytes));
                bool ok = pcm.Codec == "pcm" && pcm.SampleRate == src.SampleRate && Math.Abs(pcm.Samples - src.Samples) < 2048;
                if (!ok || converted == 0) Check(ok, $"{e.Path}: {src.Samples} → {pcm.Samples} samples {pcm.Codec} {pcm.SampleRate} Hz");
                converted++;
            }
            // Retail had 10 (voice/sfx_voi_fight_stumble_*); Community Patch 1.75 ships them as PCM.
            Console.WriteLine($"  {converted} IMA-ADPCM WAVs in Sound.pak, all converted");
        }
        Check(ImaRoundTripError() < 0.02, "IMA-ADPCM decoder round trip of a synthetic sine");

        Console.WriteLine(failures == 0 ? "\nall checks passed" : $"\n{failures} check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Encodes a 440 Hz sine as mono IMA-ADPCM (reference encoder), decodes it with ImaAdpcm, returns the RMS error (full scale 1).</summary>
    static double ImaRoundTripError()
    {
        int[] idxTab = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        int[] steps = { 7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
        const int rate = 22050, blockAlign = 256, perBlock = (blockAlign - 4) * 2 + 1, blocks = 20;
        var src = new short[perBlock * blocks];
        for (int i = 0; i < src.Length; i++) src[i] = (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 12000);
        var data = new List<byte>();
        for (int b = 0; b < blocks; b++)
        {
            int pred = src[b * perBlock], index = 0;
            data.AddRange(BitConverter.GetBytes((short)pred)); data.Add(0); data.Add(0);
            int Enc(int sample)
            {
                int step = steps[index], diff = sample - pred, nib = 0;
                if (diff < 0) { nib = 8; diff = -diff; }
                int vpd = step >> 3;
                if (diff >= step) { nib |= 4; diff -= step; vpd += step; }
                if (diff >= step >> 1) { nib |= 2; diff -= step >> 1; vpd += step >> 1; }
                if (diff >= step >> 2) { nib |= 1; vpd += step >> 2; }
                pred = Math.Clamp(pred + ((nib & 8) != 0 ? -vpd : vpd), short.MinValue, short.MaxValue);
                index = Math.Clamp(index + idxTab[nib], 0, 88);
                return nib;
            }
            for (int i = 1; i < perBlock; i += 2)
            {
                int lo = Enc(src[b * perBlock + i]), hi = Enc(src[b * perBlock + i + 1]);
                data.Add((byte)(lo | hi << 4));
            }
        }
        var wav = new List<byte>();
        void Str(string x) => wav.AddRange(System.Text.Encoding.ASCII.GetBytes(x));
        Str("RIFF"); wav.AddRange(BitConverter.GetBytes(4 + 28 + 8 + data.Count)); Str("WAVEfmt ");
        wav.AddRange(BitConverter.GetBytes(20)); wav.AddRange(BitConverter.GetBytes((ushort)0x11)); wav.AddRange(BitConverter.GetBytes((ushort)1));
        wav.AddRange(BitConverter.GetBytes(rate)); wav.AddRange(BitConverter.GetBytes(rate * blockAlign / perBlock));
        wav.AddRange(BitConverter.GetBytes((ushort)blockAlign)); wav.AddRange(BitConverter.GetBytes((ushort)4));
        wav.AddRange(BitConverter.GetBytes((ushort)2)); wav.AddRange(BitConverter.GetBytes((ushort)perBlock));
        Str("data"); wav.AddRange(BitConverter.GetBytes(data.Count)); wav.AddRange(data);
        var pcm = G3.Formats.Audio.ImaAdpcm.ToPcm16(wav.ToArray());
        int n = (pcm.Length - 44) / 2;
        if (n != src.Length) return 1;
        double err = 0;
        for (int i = 0; i < n; i++) { double d = (BitConverter.ToInt16(pcm, 44 + i * 2) - src[i]) / 32768.0; err += d * d; }
        return Math.Sqrt(err / n);
    }
}
