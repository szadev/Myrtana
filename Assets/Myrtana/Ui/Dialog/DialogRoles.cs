using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Dialog
{
    /// <summary>What a conversation participant's body does at a line boundary (Docs/dialog-presentation.md §3).</summary>
    public enum PerformerCue
    {
        /// <summary>Speaks the line: talk gesture and lip sync.</summary>
        Talk,
        /// <summary>Listens: the stance's <c>Listen_Loop</c> where it exists (else idle), mouth closed.</summary>
        Listen,
        /// <summary>Back to the animator's own motion (a looping talk gesture is stopped).</summary>
        Idle,
        /// <summary>A one-shot talk gesture plays to its end, then idle; the mouth eases closed.</summary>
        PlayOut,
    }

    /// <summary>
    /// fix-200 (owner: "the hero keeps his talk animation while the NPC speaks"): who acts what, per line. G3
    /// <c>_AI_Say_Ex</c> plays the Say gesture and the facial animation on the line's speaker only, and SetDialogRoles
    /// makes everyone else the listener. Here: the speaker talks, every other participant listens; when a line ends the
    /// speaker's looping gesture (<c>Say_Ambient</c> is a loop) stops, a one-shot plays out. The camera's listener is
    /// the hero for any NPC line (the partner or a third NPC joining in) and the partner for the hero's lines.
    /// Ids are opaque strings (actor names), compared ordinally.
    /// </summary>
    public static class DialogRoles
    {
        /// <summary>The listener the dialog camera frames with <paramref name="speaker"/>.</summary>
        public static string ListenerOf(string speaker, string hero, string partner) =>
            string.Equals(speaker, hero, StringComparison.Ordinal) ? partner : hero;

        /// <summary>A line by <paramref name="speaker"/> starts: <paramref name="performer"/> talks or listens.</summary>
        public static PerformerCue AtLineStart(string performer, string speaker) =>
            string.Equals(performer, speaker, StringComparison.Ordinal) ? PerformerCue.Talk : PerformerCue.Listen;

        /// <summary>
        /// A line ends: its speaker stops a looping gesture (<see cref="PerformerCue.Idle"/>) or lets a one-shot play out;
        /// the others keep listening.
        /// </summary>
        public static PerformerCue AtLineEnd(bool spoke, bool gestureLoops) =>
            !spoke ? PerformerCue.Listen : gestureLoops ? PerformerCue.Idle : PerformerCue.PlayOut;

        /// <summary>Cues of every participant for a line by <paramref name="speaker"/>, in participant order.</summary>
        public static IReadOnlyList<KeyValuePair<string, PerformerCue>> LineStart(IEnumerable<string> participants, string speaker)
        {
            var cues = new List<KeyValuePair<string, PerformerCue>>();
            foreach (var p in participants) cues.Add(new KeyValuePair<string, PerformerCue>(p, AtLineStart(p, speaker)));
            return cues;
        }

        /// <summary>Exactly one participant talks in any line (the speaker), provided it takes part.</summary>
        public static int Talkers(IEnumerable<KeyValuePair<string, PerformerCue>> cues)
        {
            int n = 0;
            foreach (var c in cues) if (c.Value == PerformerCue.Talk) n++;
            return n;
        }
    }
}
