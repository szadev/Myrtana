using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// The object's own part of a use motion (Docs/interactive-objects.md §8). G3 stores the motion of an animated
    /// interactive object as the <c>_I_</c> variant of the user's motion (<c>Hero_Stand_1H_None_P0_GrindStone_Loop_I_…</c>
    /// next to the hero's own <c>…_Loop_N_…</c>), keyed to the object actor's bones; whenever the user plays a phase of the
    /// use, the object plays the same phase: the grindstone's wheel and pedal turn, the chest's lid opens and closes, the
    /// barbecue's spit turns, the water pipe's hose moves, the orc boulder tilts and rises.
    /// Which use types have such parts is G3 data (every <c>Hero_*_I_*</c> motion of CP 1.75, <c>G3Cli anim list _I_</c>);
    /// the use types without (anvil, cauldron, alchemy, saw log, drum, beds, seats …) are plain meshes.
    /// </summary>
    public static class ObjectUseParts
    {
        /// <summary>
        /// The animated object actor's placement dummy, the user's exact stand point (G3 aligns the user to it, facing the
        /// object): the water pipe's lies 99.1 cm in front of the pipe (its <c>gCNavOffset_PS.OffsetPose</c> (0, 0, 99.1)),
        /// the orc boulder's on the actor origin with the stone 95 cm behind it (fix-189).
        /// </summary>
        public const string ExactDummy = "G3_Dummy_Offset_Exact_01";

        /// <summary>Variant field of the object's part; the user's own part is <see cref="UserVariant"/>.</summary>
        public const string ObjectVariant = "I", UserVariant = "N";

        // use type → hero motion prefixes whose _I_ variants animate the object (CP 1.75 motion data)
        static readonly Dictionary<string, string[]> s_prefixes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chest"] = new[] { "Hero_Kneel_None_None_P0_OpenChest_" },                 // Chest_Top: the lid (all 5 chest actors)
            ["GrindStone"] = new[] { "Hero_Stand_1H_None_P0_GrindStone_" },           // GrindStone_Wheel + GrindStone_Pedal
            ["Barbecue"] = new[] { "Hero_Stand_None_None_P0_Barbecue_" },             // BBQ_Stick: the spit
            ["Waterpipe"] = new[] { "Hero_SitGround_None_None_P0_Waterpipe_" },       // WaterPipe_Pipe_1..5: the hose
            ["OrcBoulder"] = new[]                                                      // OrcBoulder_Stone
            {
                "Hero_Stand_None_None_P0_TiltOrcBoulder_",
                "Hero_TiltOrcBoulder_None_None_P0_",
                "Hero_HoldOrcBoulder_None_None_P0_",
                "Hero_LiftOrcBoulder_None_None_P0_",
            },
        };

        /// <summary>Use types whose object has its own part of the use motions.</summary>
        public static IEnumerable<string> AnimatedUseTypes => s_prefixes.Keys;

        /// <summary>The object of this use type moves with the user's motions.</summary>
        public static bool Animated(string useType) => useType != null && s_prefixes.ContainsKey(useType);

        /// <summary>The user's motion prefixes whose <c>_I_</c> variants are the object's parts (empty when none).</summary>
        public static IReadOnlyList<string> HeroMotionPrefixes(string useType) =>
            useType != null && s_prefixes.TryGetValue(useType, out var p) ? p : Array.Empty<string>();

        /// <summary>
        /// The object's part of a user motion key (<c>Rig_Stance_L_R_Pose_Action_Phase_Variant_Dir_Index</c>, or a full motion
        /// name): the same ten fields with variant <see cref="ObjectVariant"/>; null when it is not a structured key.
        /// </summary>
        public static string PartOf(string heroKey)
        {
            if (string.IsNullOrEmpty(heroKey)) return null;
            var f = heroKey.Split('_');
            if (f.Length < 10) return null;
            f[7] = ObjectVariant;
            return string.Join("_", f, 0, 10);
        }

        /// <summary>A part (or motion key) of phase Loop or Ambient repeats (the importer's loop rule); Begin/End run once.</summary>
        public static bool Loops(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var f = key.Split('_');
            return f.Length >= 10 && (f[6] == "Loop" || f[6] == "Ambient");
        }
    }

    /// <summary>What the object does when the user's motion changes or the use ends.</summary>
    public enum ObjectPartAction
    {
        /// <summary>Nothing: the object keeps what it shows (it has no part of this motion).</summary>
        None,
        /// <summary>Crossfade to <see cref="ObjectPartCommand.Part"/> (a loop repeats, Begin/End hold their last frame).</summary>
        Play,
        /// <summary>Show the last frame of <see cref="ObjectPartCommand.Part"/> at once (an interrupted use: the lid shut, the boulder down).</summary>
        Rest,
        /// <summary>Stop where it is (a loop without a closing part: the wheel stops turning).</summary>
        Stop,
    }

    public readonly struct ObjectPartCommand
    {
        public readonly ObjectPartAction Action;
        public readonly string Part;

        public ObjectPartCommand(ObjectPartAction action, string part)
        {
            Action = action;
            Part = part;
        }

        public static readonly ObjectPartCommand None = new ObjectPartCommand(ObjectPartAction.None, null);

        public override string ToString() => Part == null ? Action.ToString() : $"{Action} {Part}";
    }

    /// <summary>Where the object's animation stands during one use.</summary>
    public enum ObjectPartMode
    {
        /// <summary>At rest in its bind pose (no part played yet).</summary>
        Still,
        /// <summary>Playing a part in step with the user.</summary>
        Playing,
        /// <summary>The use ended; the object shows the last frame of <see cref="ObjectPartState.Part"/> (or stopped).</summary>
        Resting,
    }

    /// <summary>
    /// The object's side of one use (pure; the scene view plays the commands): every user motion with an object part
    /// plays that part, motions without one leave the object as it is (kneeling down, the lock pick), and when the use
    /// ends the object rests — after the closing part (OpenChest_End: the lid is shut, GrindStone_End: the wheel runs
    /// out), or, when the use was cut short, at the closing part's last frame (<see cref="RestAt"/>), or stopped.
    /// </summary>
    public sealed class ObjectPartState
    {
        readonly Func<string, bool> _hasPart;

        /// <param name="hasPart">the object's visual has this part (an <c>_I_</c> key); objects without parts answer false</param>
        public ObjectPartState(Func<string, bool> hasPart) => _hasPart = hasPart ?? (_ => false);

        public ObjectPartMode Mode { get; private set; }

        /// <summary>The part playing (or shown at rest).</summary>
        public string Part { get; private set; }

        /// <summary>The part whose last frame the object shows when the use is cut short.</summary>
        public string RestPart { get; private set; }

        /// <summary>The object turns with the user right now (a looping part plays).</summary>
        public bool Moving => Mode == ObjectPartMode.Playing && ObjectUseParts.Loops(Part);

        /// <summary>The user's motion that closes the use from the current stage (End, or the boulder's let-go).</summary>
        public void RestAt(string heroKey)
        {
            string part = ObjectUseParts.PartOf(heroKey);
            RestPart = part != null && _hasPart(part) ? part : null;
        }

        /// <summary>The user starts a motion: the object's command.</summary>
        public ObjectPartCommand HeroPlays(string heroKey)
        {
            string part = ObjectUseParts.PartOf(heroKey);
            if (part == null || !_hasPart(part)) return ObjectPartCommand.None;
            Mode = ObjectPartMode.Playing;
            Part = part;
            return new ObjectPartCommand(ObjectPartAction.Play, part);
        }

        /// <summary>The use ended (normally or cut short): the object's last command.</summary>
        public ObjectPartCommand UseEnded()
        {
            if (Mode != ObjectPartMode.Playing) return ObjectPartCommand.None;
            Mode = ObjectPartMode.Resting;
            if (RestPart != null && !string.Equals(Part, RestPart, StringComparison.OrdinalIgnoreCase))
            {
                Part = RestPart;
                return new ObjectPartCommand(ObjectPartAction.Rest, RestPart);
            }
            if (RestPart == null && ObjectUseParts.Loops(Part)) return new ObjectPartCommand(ObjectPartAction.Stop, Part);
            return ObjectPartCommand.None; // the closing part ran and holds its last frame
        }
    }
}
