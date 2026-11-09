using System;
using System.Collections.Generic;
using System.Numerics;

namespace Myrtana.Ui.Dialog
{
    /// <summary>How a shot is reached: a hard cut, or a glide from the previous camera (G3 "Lerp" presets).</summary>
    public enum ShotTransition { Cut, Glide }

    /// <summary>Close (face / over the shoulder), Side (two-shot from the side) or Wide (the moving "Lerp" shots).</summary>
    public enum ShotGroup { Close, Side, Wide }

    /// <summary>
    /// Where a preset puts the camera (G3 gCCameraAI_PS, Docs/dialog-presentation.md §1.3): on a sphere around the
    /// listener's head, beside the midpoint of the two heads, or moving between two listener positions over
    /// <see cref="DialogShotGeometry.DollySeconds"/>.
    /// </summary>
    public enum ShotAnchor { Listener, Side, Dolly }

    /// <summary>What the camera looks at: the middle of the two heads, or the speaker's head (Face2).</summary>
    public enum ShotAim { Midpoint, Speaker }

    /// <summary>
    /// One G3 dialog camera preset (PC_Camera gCCameraModeParams, Docs/dialog-presentation.md §1). For a
    /// <see cref="ShotAnchor.Listener"/> shot the camera stands <see cref="Range"/> metres (G3 MinRange; negative = in
    /// front) behind the listener's head, turned <see cref="Azimuth"/> degrees about the up axis away from the line
    /// listener → speaker (+ = to its right) and raised <see cref="Elevation"/> degrees. A <see cref="ShotAnchor.Dolly"/>
    /// shot moves from that position to the one of <see cref="DollyEnd"/>.
    /// </summary>
    public sealed class DialogShotPreset
    {
        public DialogShotPreset(string name, ShotGroup group, float range, float azimuth, float elevation, ShotTransition transition,
                                ShotAnchor anchor = ShotAnchor.Listener, ShotAim aim = ShotAim.Midpoint, DialogShotPreset dollyEnd = null)
        {
            Name = name; Group = group; Range = range; Azimuth = azimuth; Elevation = elevation; Transition = transition;
            Anchor = anchor; Aim = aim; DollyEnd = dollyEnd;
        }

        public string Name { get; }
        public ShotGroup Group { get; }
        /// <summary>Distance behind the listener's head, m (G3 MinRange; dialog shots do not use BestRange).</summary>
        public float Range { get; }
        public float Azimuth { get; }
        public float Elevation { get; }
        public ShotTransition Transition { get; }
        public ShotAnchor Anchor { get; }
        public ShotAim Aim { get; }
        /// <summary>End position of a dolly shot (its range, azimuth and elevation), null for the others.</summary>
        public DialogShotPreset DollyEnd { get; }

        /// <summary>
        /// Side of the line listener → speaker the camera starts on: +1 right, -1 left, 0 on the line (SideL stands
        /// left of it, SideR right).
        /// </summary>
        public int SpeakerSide => Azimuth > 0.5f ? 1 : Azimuth < -0.5f ? -1 : 0;

        public override string ToString() => Name;
    }

    /// <summary>The G3 CP 1.75 dialog presets (PC_Camera CamModeDialog*), in metres.</summary>
    public static class DialogShotPresets
    {
        /// <summary>FOV range of every dialog preset, degrees (one value is drawn per shot).</summary>
        public const float MinFov = 60, MaxFov = 65;
        /// <summary>RotOffsetX of every dialog preset: the camera pitch relative to the line to the target, degrees.</summary>
        public const float PitchOffset = -5;

        public static readonly DialogShotPreset Face = new DialogShotPreset("Face", ShotGroup.Close, -0.5f, 0, 0, ShotTransition.Cut);

        static readonly DialogShotPreset Lerp2End = new DialogShotPreset("Lerp2", ShotGroup.Wide, 2.5f, -75, 28, ShotTransition.Glide);
        static readonly DialogShotPreset Lerp4End = new DialogShotPreset("Lerp4", ShotGroup.Wide, 3.5f, 80, 10, ShotTransition.Glide);

        /// <summary>
        /// G3 runs Lerp1 and Lerp2 as the same move (Lerp1's position to Lerp2's), Lerp3 and Lerp4 likewise; both names
        /// stay so that the draw odds match G3.
        /// </summary>
        public static readonly IReadOnlyList<DialogShotPreset> All = new[]
        {
            Face,
            new DialogShotPreset("Face2L", ShotGroup.Close, -0.5f, -60, 5, ShotTransition.Cut, aim: ShotAim.Speaker),
            new DialogShotPreset("Face2R", ShotGroup.Close, -0.5f, 60, 5, ShotTransition.Cut, aim: ShotAim.Speaker),
            new DialogShotPreset("Shoulder1L", ShotGroup.Close, 1.05f, -40, 25, ShotTransition.Cut),
            new DialogShotPreset("Shoulder1R", ShotGroup.Close, 1.05f, 40, 25, ShotTransition.Cut),
            new DialogShotPreset("Shoulder2L", ShotGroup.Close, 2.5f, -60, 5, ShotTransition.Cut),
            new DialogShotPreset("Shoulder2R", ShotGroup.Close, 2.5f, 60, 5, ShotTransition.Cut),
            new DialogShotPreset("SideL", ShotGroup.Side, 0, -120, 0, ShotTransition.Cut, ShotAnchor.Side),
            new DialogShotPreset("SideR", ShotGroup.Side, 0, 120, 0, ShotTransition.Cut, ShotAnchor.Side),
            new DialogShotPreset("Lerp1", ShotGroup.Wide, 2.5f, 75, 28, ShotTransition.Glide, ShotAnchor.Dolly, dollyEnd: Lerp2End),
            new DialogShotPreset("Lerp2", ShotGroup.Wide, 2.5f, 75, 28, ShotTransition.Glide, ShotAnchor.Dolly, dollyEnd: Lerp2End),
            new DialogShotPreset("Lerp3", ShotGroup.Wide, 3.5f, -80, 10, ShotTransition.Glide, ShotAnchor.Dolly, dollyEnd: Lerp4End),
            new DialogShotPreset("Lerp4", ShotGroup.Wide, 3.5f, -80, 10, ShotTransition.Glide, ShotAnchor.Dolly, dollyEnd: Lerp4End),
        };
    }

    /// <summary>Camera placement of a preset (pure math; Unity axes: y up, yaw clockwise seen from above).</summary>
    public static class DialogShotGeometry
    {
        /// <summary>A Side shot stands this many times the head-to-head distance beside the midpoint (G3: 1.3).</summary>
        public const float SideDistanceFactor = 1.3f;
        /// <summary>Duration of a dolly (Lerp) move, s (G3: 18, eased).</summary>
        public const float DollySeconds = 18;

        /// <summary>
        /// Camera position of <paramref name="preset"/> for the heads of speaker and listener; <paramref name="shotAge"/>
        /// (s since the shot began) moves a dolly shot.
        /// </summary>
        public static Vector3 CameraPosition(Vector3 speaker, Vector3 listener, DialogShotPreset preset, float shotAge = 0)
        {
            var f = Horizontal(speaker - listener);
            var right = new Vector3(f.Z, 0, -f.X); // f rotated 90 degrees clockwise seen from above
            switch (preset.Anchor)
            {
                case ShotAnchor.Side:
                {
                    var mid = (speaker + listener) * 0.5f;
                    float d = Vector3.Distance(speaker, listener);
                    return mid + right * (preset.SpeakerSide * d * SideDistanceFactor);
                }
                case ShotAnchor.Dolly when preset.DollyEnd != null:
                {
                    var a = BehindListener(listener, f, right, preset);
                    var b = BehindListener(listener, f, right, preset.DollyEnd);
                    float t = Math.Max(0, Math.Min(1, shotAge / DollySeconds));
                    t = t * t * (3 - 2 * t);
                    return Vector3.Lerp(a, b, t);
                }
                default:
                    return BehindListener(listener, f, right, preset);
            }
        }

        /// <summary>The point the camera looks at (before the pitch offset).</summary>
        public static Vector3 LookTarget(Vector3 speaker, Vector3 listener, DialogShotPreset preset) =>
            preset.Aim == ShotAim.Speaker ? speaker : (speaker + listener) * 0.5f;

        /// <summary>
        /// Distance camera → speaker's head the preset gives (no collision). Listener-anchored shots add the head-to-head
        /// distance: at 2 m apart Face 1.5, Shoulder1 2.8, Shoulder2 3.9, Side 2.8, the dollies 3.4–4.2 m; at 2.6 m apart
        /// Face 2.1, Shoulder2 4.4, Side 3.6, Lerp1 4.0, Lerp3/4 end 4.7 m.
        /// </summary>
        public static float SpeakerDistance(Vector3 speaker, Vector3 listener, DialogShotPreset preset, float shotAge = 0) =>
            Vector3.Distance(CameraPosition(speaker, listener, preset, shotAge), speaker);

        /// <summary>
        /// Range behind the listener, turned by the azimuth (+ = right of the line listener → speaker) and raised by the
        /// elevation; a negative range stands in front of the listener, towards the speaker.
        /// </summary>
        static Vector3 BehindListener(Vector3 listener, Vector3 f, Vector3 right, DialogShotPreset p)
        {
            double az = p.Azimuth * Math.PI / 180, el = p.Elevation * Math.PI / 180;
            var horizontal = -f * (float)Math.Cos(az) + right * (float)Math.Sin(az);
            var dir = horizontal * (float)Math.Cos(el) + Vector3.UnitY * (float)Math.Sin(el);
            return listener + dir * p.Range;
        }

        static Vector3 Horizontal(Vector3 v)
        {
            v = new Vector3(v.X, 0, v.Z);
            return v.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(v);
        }

        /// <summary>
        /// Side of the line of action (hero → partner) a camera stands on: +1 / -1, 0 on the line. Shots that keep this
        /// sign keep everyone's screen direction (the 180° rule).
        /// </summary>
        public static int WorldSide(Vector3 hero, Vector3 partner, Vector3 camera)
        {
            var axis = partner - hero;
            var rel = camera - hero;
            float cross = axis.Z * rel.X - axis.X * rel.Z; // > 0: right of the hero → partner axis
            return cross > 1e-4f ? 1 : cross < -1e-4f ? -1 : 0;
        }

        /// <summary>
        /// World side (see <see cref="WorldSide"/>) a preset starts on: its side of the line listener → speaker, which
        /// is the hero → partner line when the partner speaks and the reverse line when the hero speaks.
        /// </summary>
        public static int WorldSideOf(DialogShotPreset preset, bool speakerIsHero) => preset.SpeakerSide * (speakerIsHero ? -1 : 1);
    }

    /// <summary>
    /// Picks the camera preset for every line of a conversation like G3 CP 1.75 gCCameraAI_PS SetDialogRoles /
    /// StartDialogCamera / FindPreset (Docs/dialog-presentation.md §1.2). Not a free random draw but a small edit
    /// pattern:
    /// <list type="bullet">
    /// <item>first shot: over the shoulder — Shoulder2 (70 %), Shoulder1 (22.5 %), Side (7.5 %, partners &lt; 20 m);</item>
    /// <item>later lines: Shoulder2 by default; 30 % of the time Side (a quarter of those) or Shoulder1 instead, unless the
    /// shot before the current one was a Shoulder2; after a Side shot or when Shoulder2 is blocked, the listener's face shot
    /// (Face, or Face2 after a Shoulder2) when the partners stand more than 2 m apart;</item>
    /// <item>a speaker who keeps talking (3rd line in a row and on): the shot stays, except that after a shoulder shot and with
    /// the partners more than 2 m apart it cuts to the face shot (30 %) or starts a dolly (35 %);</item>
    /// <item>a running dolly (Lerp) younger than <see cref="KeepWideSeconds"/> stays while the same character talks; one older
    /// than <see cref="SideFlipSeconds"/> moves the line-of-action side to where it ends.</item>
    /// </list>
    /// L/R variants are chosen to stay on the current side of the line of action (the 180° rule); presets the scene
    /// rejects are skipped (second pass: any preset), and when nothing fits the current shot stays.
    /// </summary>
    public sealed class DialogShotDirector
    {
        public const float KeepWideSeconds = 18, SideFlipSeconds = 9;
        /// <summary>G3 FindPreset rolls: leave the default Shoulder2 (0.3), of that a Side shot (0.25); a monologue cuts to the face (0.3) or else starts a dolly (0.5).</summary>
        public const double OtherThanShoulder2Chance = 0.3, SideChance = 0.25, MonologueFaceChance = 0.3, MonologueDollyChance = 0.5;
        /// <summary>Partners farther apart than this (m, root to root) get face shots and dollies (G3: 200 cm).</summary>
        public const float FarPartnerDistance = 2;
        /// <summary>A first Side shot needs the partners closer than this, m (G3: 2000 cm).</summary>
        public const float SidePartnerDistance = 20;

        readonly Random _random;
        readonly IReadOnlyList<DialogShotPreset> _presets;
        int _worldSide, _sameSpeakerRun;
        bool _currentSpeakerIsHero;

        public DialogShotDirector(Random random, IReadOnlyList<DialogShotPreset> presets = null)
        {
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _presets = presets ?? DialogShotPresets.All;
        }

        public DialogShotPreset Current { get; private set; }
        /// <summary>The shot before <see cref="Current"/> (G3 compares new picks against it, not against the current one).</summary>
        public DialogShotPreset Previous { get; private set; }
        public float CurrentAge { get; private set; }
        /// <summary>Line-of-action side of the shots so far (+1/-1), 0 before the first sided shot.</summary>
        public int WorldSide => _worldSide;
        /// <summary>Lines in a row by the current speaker after his first one (G3 counter: 0 when the speaker changed).</summary>
        public int SameSpeakerRun => _sameSpeakerRun;
        public float Fov { get; private set; } = DialogShotPresets.MinFov;

        /// <summary>Starts a conversation: no current shot, either side.</summary>
        public void Reset()
        {
            Current = Previous = null;
            CurrentAge = 0;
            _worldSide = 0;
            _sameSpeakerRun = 0;
        }

        public void Advance(float seconds) => CurrentAge += Math.Max(0, seconds);

        /// <summary>
        /// The shot for a new line. <paramref name="sameSpeaker"/>: the previous line had the same speaker.
        /// <paramref name="isUsable"/> (may be null) rejects presets the scene cannot show. <paramref name="partnerDistance"/>:
        /// distance between the two characters, m. <paramref name="bigCreature"/>: one of them is huge (G3: bounding box
        /// over 343 m³) — no Shoulder1 shots. Returns false when the current shot is kept.
        /// </summary>
        public bool NextShot(bool speakerIsHero, bool sameSpeaker, Func<DialogShotPreset, bool> isUsable,
                             float partnerDistance = 0, bool bigCreature = false)
        {
            bool first = Current == null;
            _sameSpeakerRun = sameSpeaker && !first ? _sameSpeakerRun + 1 : 0;
            if (sameSpeaker && !first && Current.Group == ShotGroup.Wide && CurrentAge < KeepWideSeconds) return false;
            // A dolly that has run past the line puts the camera on the side it moves to (G3 StartDialogCamera).
            if (!first && Current.Anchor == ShotAnchor.Dolly && Current.DollyEnd != null && CurrentAge > SideFlipSeconds)
                _worldSide = DialogShotGeometry.WorldSideOf(Current.DollyEnd, _currentSpeakerIsHero);

            var ctx = new Context(speakerIsHero, partnerDistance, bigCreature);
            var pick = (first ? FirstShot(ctx, isUsable) : FollowUpShot(ctx, isUsable, strict: true))
                       ?? (first ? FirstShot(ctx, null) : FollowUpShot(ctx, null, strict: false));
            if (pick == null)
            {
                if (!first) return false; // G3 keeps the shot when FindPreset finds nothing
                pick = DialogShotPresets.Face;
            }
            if (!first) Previous = Current;
            int side = DialogShotGeometry.WorldSideOf(pick, speakerIsHero);
            if (side != 0) _worldSide = side;
            Current = pick;
            _currentSpeakerIsHero = speakerIsHero;
            CurrentAge = 0;
            Fov = DialogShotPresets.MinFov + (float)_random.NextDouble() * (DialogShotPresets.MaxFov - DialogShotPresets.MinFov);
            return true;
        }

        readonly struct Context
        {
            public Context(bool hero, float distance, bool big) { Hero = hero; Distance = distance; Big = big; }
            public readonly bool Hero;
            public readonly float Distance;
            public readonly bool Big;
            public bool Far => Distance > FarPartnerDistance;
        }

        DialogShotPreset FirstShot(Context c, Func<DialogShotPreset, bool> ok)
        {
            if (Roll() < OtherThanShoulder2Chance)
            {
                DialogShotPreset p = null;
                if (Roll() < SideChance && c.Distance < SidePartnerDistance) p = Usable(ok, "SideR") ?? Usable(ok, "SideL");
                if (p != null) return p;
                if (!c.Big && (p = Usable(ok, "Shoulder1R") ?? Usable(ok, "Shoulder1L")) != null) return p;
            }
            return Usable(ok, "Shoulder2R") ?? Usable(ok, "Shoulder2L");
        }

        DialogShotPreset FollowUpShot(Context c, Func<DialogShotPreset, bool> ok, bool strict)
        {
            string s = SideSuffix(c.Hero);
            if (_sameSpeakerRun > 1 && !c.Big)
            {
                // The same speaker goes on: keep the shot, or after a shoulder shot cut to the face / start a dolly.
                if (!Is(Previous, "Shoulder")) return null;
                if (Roll() < MonologueFaceChance)
                    return c.Far ? Usable(ok, Is(Previous, "Shoulder2") ? "Face2" + s : "Face") : null;
                return Roll() < MonologueDollyChance && c.Far ? Usable(ok, Dolly(c.Hero)) : null;
            }

            DialogShotPreset pick = null;
            if (Roll() < OtherThanShoulder2Chance && !Is(Previous, "Shoulder2"))
            {
                if (Roll() < SideChance) pick = Usable(ok, "Side" + s);
                if (pick == null && !c.Big) pick = Usable(ok, "Shoulder1" + s);
            }
            if (pick != null) return pick;
            if (!Is(Previous, "Side") && (pick = Usable(ok, "Shoulder2" + s)) != null) return pick;
            if (c.Far && !Is(Previous, "Face"))
                if ((pick = Usable(ok, Is(Previous, "Shoulder2") ? "Face2" + s : "Face")) != null) return pick;
            if (!Is(Previous, "Shoulder1")) return Usable(ok, "Shoulder2" + s);
            return strict ? null : Find("Shoulder1" + s);
        }

        /// <summary>"R" or "L": the variant that stands on the current side of the line of action (R before any shot).</summary>
        string SideSuffix(bool speakerIsHero) => _worldSide == 0 || _worldSide == (speakerIsHero ? -1 : 1) ? "R" : "L";

        /// <summary>The dolly that starts on the current side (and crosses the line); G3 picks only Lerp1 or Lerp3.</summary>
        string Dolly(bool speakerIsHero)
        {
            var lerp1 = Find("Lerp1");
            return lerp1 == null || _worldSide == 0 || DialogShotGeometry.WorldSideOf(lerp1, speakerIsHero) == _worldSide ? "Lerp1" : "Lerp3";
        }

        static bool Is(DialogShotPreset p, string prefix) => p != null && p.Name.StartsWith(prefix, StringComparison.Ordinal);

        DialogShotPreset Find(string name)
        {
            foreach (var p in _presets) if (p.Name == name) return p;
            return null;
        }

        DialogShotPreset Usable(Func<DialogShotPreset, bool> ok, string name)
        {
            var p = Find(name);
            return p != null && (ok == null || ok(p)) ? p : null;
        }

        double Roll() => _random.NextDouble();
    }
}
