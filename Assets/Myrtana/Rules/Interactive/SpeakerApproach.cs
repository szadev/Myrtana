using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// [owner] fix-206: an invisible talking object (a <see cref="InteractiveKind.Speaker"/> without a visual: the six
    /// <c>Living_Ancestor_0n_Tester</c> rune pedestals in the Nordmar ancestor tombs, the temple door testers) speaks its
    /// News entry as soon as the hero comes up to it ("А, туда что-то вставляется!").
    /// <para>G3 (CP 1.75) has no approach trigger: the tester is a gCInteraction_PS (UseType Shrine, FocusPriority 1) with a
    /// gCDialog_PS; the hero has to focus "Пьедестал с рунами" and press Use (PS_Interact_Shrine kneels, then the dialog's
    /// News entry plays). With nothing to see there (no mesh; G3 even hangs the focus name 4 m under it, FocusWorldOffset
    /// (0, −400, 0)) players walked past it; the owner's first "trigger" was the left button (Use1 with no weapon drawn)
    /// pressed to attack the skeletons that guard the pedestal.</para>
    /// Delivering the stone stays a use of the pedestal (its Choice entry, kneeling first as in G3).
    /// </summary>
    public sealed class SpeakerApproach
    {
        /// <summary>The hero's object reach in the plane (m; PlayerInteraction.objectReach, G3 FocusCheck measures flat).</summary>
        public const float ReachM = 2.5f;
        /// <summary>At most this far above or below the object's pivot (m): not from the floor above or below.</summary>
        public const float MaxHeightM = 2f;
        /// <summary>The hero has to move this much farther than <see cref="ReachM"/> away before it can speak again (m).</summary>
        public const float RearmMarginM = 1.5f;

        readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The objects that announce themselves: a talking object with no mesh of its own.</summary>
        public static bool Announces(InteractiveObjectDef def) =>
            def != null && def.Kind == InteractiveKind.Speaker && string.IsNullOrEmpty(def.Visual) && !string.IsNullOrEmpty(def.Name);

        /// <param name="flatDistance">Hero ↔ object pivot in the plane (m).</param>
        /// <param name="heightOffset">Hero's feet minus the object's pivot (m).</param>
        public static bool InReach(float flatDistance, float heightOffset) =>
            flatDistance <= ReachM && Math.Abs(heightOffset) <= MaxHeightM;

        static bool OutOfRearmRange(float flatDistance, float heightOffset) =>
            flatDistance > ReachM + RearmMarginM || Math.Abs(heightOffset) > MaxHeightM + RearmMarginM;

        /// <summary>
        /// Once per visit: true when the hero is in reach of object <paramref name="id"/>, it may speak now
        /// (<paramref name="canAnnounce"/>: it has an available News entry, free line of sight, the hero is free) and it has
        /// not spoken since the hero last left it. A failed try (no News yet, weapon drawn) does not use up the visit.
        /// </summary>
        public bool ShouldAnnounce(string id, float flatDistance, float heightOffset, bool canAnnounce)
        {
            if (id == null) return false;
            if (OutOfRearmRange(flatDistance, heightOffset))
            {
                _announced.Remove(id);
                return false;
            }
            if (!canAnnounce || !InReach(flatDistance, heightOffset) || _announced.Contains(id)) return false;
            _announced.Add(id);
            return true;
        }

        /// <summary>The hero left the object's surroundings without the pass seeing it (its view was hidden).</summary>
        public void Forget(string id)
        {
            if (id != null) _announced.Remove(id);
        }
    }
}
