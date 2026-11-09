using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>The G3 tutorial tips (string ids <c>TUT_*</c>), each shown once per game.</summary>
    public enum TutorialTip
    {
        /// <summary>A new game, after the intro video (gCSession::Start).</summary>
        Start,
        /// <summary>The quest Ardea_Revolution succeeded (Ardea freed).</summary>
        AfterFight,
        /// <summary>The hero has talked to Diego, Milten and Gorn.</summary>
        AfterFriends,
        /// <summary>The hero has talked to Lester.</summary>
        AfterLester,
        /// <summary>First melee weapon drawn (PS_Melee).</summary>
        Fight,
        /// <summary>First bow or crossbow drawn (PS_Ranged).</summary>
        Bow,
        /// <summary>First spell readied (PS_Magic).</summary>
        Spell,
        Inventory,
        Spellbook,
        Maps,
        Charscreen,
        MissionLog,
        Trade,
    }

    /// <summary>
    /// Which tutorial tips the player has seen and whether he wants them at all (G3 <c>gCPlayerMemory_PS</c>
    /// TutorialFlags and <c>m_bHideTips</c>, saved with the game). <see cref="Request"/> queues a tip the first time its
    /// occasion comes; the view shows the queue one box at a time (<c>IDD_VIEW_TUTORIAL_2</c>: "Tip", the text, the
    /// "Do not show tips" check box and OK). While the box is checked, <c>gCGUIManager::OpenTutorial</c> shows nothing;
    /// the occasion is still used up.
    /// </summary>
    public sealed class TutorialTips : ISaveParticipant
    {
        public const string SectionName = "hud.tutorials";
        /// <summary>The companions whose talks make <see cref="TutorialTip.AfterFriends"/>.</summary>
        public static readonly IReadOnlyList<ActorId> Friends = new[] { new ActorId("Diego"), new ActorId("Milten"), new ActorId("Gorn") };
        public static readonly ActorId Lester = new ActorId("Lester");
        /// <summary>The quest whose success makes <see cref="TutorialTip.AfterFight"/>.</summary>
        public const string ArdeaQuest = "Ardea_Revolution";

        readonly HashSet<TutorialTip> _seen = new HashSet<TutorialTip>();
        readonly Queue<TutorialTip> _pending = new Queue<TutorialTip>();

        /// <summary>"Do not show tips" is checked.</summary>
        public bool HideTips { get; set; }

        /// <summary>The tip to show next, or null.</summary>
        public TutorialTip? Next => _pending.Count > 0 ? _pending.Peek() : (TutorialTip?)null;

        public bool HasSeen(TutorialTip tip) => _seen.Contains(tip);

        /// <summary>The occasion of <paramref name="tip"/> came: queued the first time (unless tips are hidden).</summary>
        public bool Request(TutorialTip tip)
        {
            if (!_seen.Add(tip) || HideTips) return false;
            _pending.Enqueue(tip);
            return true;
        }

        /// <summary>The shown box was closed (OK, another command, or the check box).</summary>
        public void Dismiss()
        {
            if (_pending.Count > 0) _pending.Dequeue();
            if (HideTips) _pending.Clear();
        }

        /// <summary>A new game: nothing seen, tips on again only if they were not switched off in this game.</summary>
        public void Reset()
        {
            _seen.Clear();
            _pending.Clear();
            HideTips = false;
        }

        public static TextId TextOf(TutorialTip tip) => new TextId("TUT_" + tip);

        string ISaveParticipant.Section => SectionName;
        int ISaveParticipant.Version => 1;

        void ISaveParticipant.Write(ISaveWriter w)
        {
            w.WriteBool(HideTips);
            var seen = new List<string>();
            foreach (var tip in _seen) seen.Add(tip.ToString());
            w.WriteSortedStrings(seen);
        }

        void ISaveParticipant.Read(ISaveReader r, int version)
        {
            Reset();
            HideTips = r.ReadBool();
            foreach (var name in r.ReadStrings())
                if (Enum.TryParse(name, out TutorialTip tip)) _seen.Add(tip);
        }
    }
}
