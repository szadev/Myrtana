using System;

namespace G3.Audio
{
    /// <summary>When a change may happen (gEMusicTriggerTime subset).</summary>
    public enum MusicTriggerTime { Immediately, NextExit }

    /// <summary>
    /// How the sequencer moves to a new target (G3's gSMusicTrigger fields that matter for playback): where the new
    /// piece starts, whether it waits for an exit link of the playing piece, whether a g3.mul transition bridges the
    /// change (else a short crossfade) and whether further triggers are refused until the piece has played once.
    /// </summary>
    public readonly struct MusicTrigger
    {
        public readonly MusicEntrance Entrance;
        public readonly MusicTriggerTime Time;
        public readonly bool UseTransition;
        public readonly bool DisableTriggers;

        public MusicTrigger(MusicEntrance entrance, MusicTriggerTime time, bool useTransition, bool disableTriggers = false)
        {
            Entrance = entrance;
            Time = time;
            UseTransition = useTransition;
            DisableTriggers = disableTriggers;
        }

        public override string ToString() =>
            $"{Entrance}/{Time}/{(UseTransition ? "transition" : "crossfade")}{(DisableTriggers ? "/locked" : "")}";
    }

    /// <summary>
    /// The trigger parameters G3 uses per music situation (the Music::Trigger* wrappers of Script.dll, CP 1.75) and
    /// for location / day-time changes (the g3.mul <c>Loc_*</c> presets). Docs/audio-runtime.md "Music rules".
    /// </summary>
    public static class MusicTriggers
    {
        /// <summary>TriggerExplore: a random entrance of the explore piece at the next exit, matching transition.</summary>
        public static readonly MusicTrigger Explore = new MusicTrigger(MusicEntrance.RandomEntrance, MusicTriggerTime.NextExit, true);
        /// <summary>TriggerFight: from the beginning, at once, no transition.</summary>
        public static readonly MusicTrigger Fight = new MusicTrigger(MusicEntrance.Begin, MusicTriggerTime.Immediately, false);
        /// <summary>TriggerShowdown / TriggerRevolution: from the beginning, at once, matching transition.</summary>
        public static readonly MusicTrigger Urgent = new MusicTrigger(MusicEntrance.Begin, MusicTriggerTime.Immediately, true);
        /// <summary>TriggerVictory: at the next exit, and no other trigger is accepted until the piece has played.</summary>
        public static readonly MusicTrigger Victory = new MusicTrigger(MusicEntrance.Begin, MusicTriggerTime.NextExit, true, true);
        /// <summary>TriggerSituation (story PlayMusic, dead, soul travel): from the beginning at the next exit.</summary>
        public static readonly MusicTrigger Situation = new MusicTrigger(MusicEntrance.Begin, MusicTriggerTime.NextExit, true);
        /// <summary>Zone / day-time change: the loop entrance at the next exit (g3.mul Loc_* presets) [inferred].</summary>
        public static readonly MusicTrigger Location = new MusicTrigger(MusicEntrance.LoopEntrance, MusicTriggerTime.NextExit, true);

        public static MusicTrigger ForSituation(string situation)
        {
            if (MusicSituations.IsExplore(situation)) return Explore;
            if (Eq(situation, MusicSituations.Fight)) return Fight;
            if (Eq(situation, MusicSituations.Showdown) || Eq(situation, MusicSituations.Revolution)) return Urgent;
            if (Eq(situation, MusicSituations.RevolutionWon)) return Victory;
            return Situation;
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
