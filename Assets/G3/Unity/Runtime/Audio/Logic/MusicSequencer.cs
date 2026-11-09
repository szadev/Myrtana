using System;
using System.Collections.Generic;

namespace G3.Audio
{
    /// <summary>Where the new fragment starts after a change (gEMusicFragmentPosition subset).</summary>
    public enum MusicEntrance { Begin, LoopEntrance, RandomEntrance }

    /// <summary>
    /// Engine-free music sequencer (gCMusicSequencer as far as decoded, Docs/formats-audio.md §2.5). It turns target
    /// states into a list of <see cref="MusicCue"/>s on the DSP clock:
    /// <list type="bullet">
    /// <item>loop: when a fragment runs out, its NextVariation (or itself) follows seamlessly, from its Loop link;</item>
    /// <item>change: at the next Exit link of the playing fragment a transition is started so that its Offset lands
    /// on the exit; the old fragment fades out over [FadeOutBegin, FadeOutEnd], the transition fades in over
    /// [FadeInBegin, FadeInEnd], and the new fragment starts at its entrance link when the transition ends
    /// [inferred alignment];</item>
    /// <item>no exit within <c>maxWait</c> (0 for <see cref="MusicTriggerTime.Immediately"/>): a virtual exit right now
    /// with the fragment's usual exit connector;</item>
    /// <item>a <see cref="MusicTrigger"/> without transition crossfades instead; one with DisableTriggers refuses
    /// further targets until its piece has played through once;</item>
    /// <item>cancel (fix-177): when the target changes again before a planned change is audible (its transition and
    /// new piece still scheduled) the change is dropped and the playing piece keeps playing as if nothing happened; when
    /// a crossfade change (fight) is still fading in and the target returns to the piece being left, the crossfade is
    /// reversed. So a fight that ends before its music really started leaves no fight fragment behind.</item>
    /// </list>
    /// Call <see cref="SetTarget"/> whenever the wanted state may have changed and <see cref="Update"/> every frame.
    /// </summary>
    public sealed class MusicSequencer
    {
        /// <summary>Minimum time between "now" and anything newly scheduled (the audio thread needs a little lead).</summary>
        public double MinLead = 0.15;
        /// <summary>The successor of an ending fragment is scheduled this long before the end.</summary>
        public double LoopLookahead = 2.0;
        public double StartFade = 1.5, StopFade = 3.0, FallbackCrossfade = 2.0;

        readonly MusicCatalog _catalog;
        readonly Random _rng;
        readonly Func<string, double> _clipLength;
        readonly List<MusicCue> _cues = new List<MusicCue>();
        MusicCue _current, _next;
        bool _nextIsChange;
        // The last change: the piece it leaves (fading out), its transition cue (null = crossfade) and whether the left
        // piece was playing normally before (only then can the change be undone).
        MusicCue _from, _transitionCue;
        bool _fromWasClean;
        MusicState _target;
        bool _hasTarget;
        MusicEntrance _entrance;
        double _maxWait;
        bool _useTransition = true;
        bool _locked;
        bool _oneShotDone;
        int _ids;

        /// <param name="clipLength">Real sample length in seconds by clip name (≤ 0 = unknown → g3.mul duration).</param>
        public MusicSequencer(MusicCatalog catalog, Random rng = null, Func<string, double> clipLength = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _rng = rng ?? new Random();
            _clipLength = clipLength;
        }

        /// <summary>All cues that are playing or scheduled (fragments and transitions).</summary>
        public IReadOnlyList<MusicCue> Cues => _cues;
        /// <summary>The fragment cue that is audible now (null = silence).</summary>
        public MusicCue Current => _current;
        /// <summary>The fragment cue scheduled to follow (loop successor or the target of a planned change).</summary>
        public MusicCue Next => _next;
        public MusicState Target => _target;

        /// <summary>Raised for every newly scheduled cue (diagnostics / tests).</summary>
        public event Action<MusicCue> Scheduled;
        /// <summary>Raised when a change is undone before it was really heard; the argument is the dropped new piece.</summary>
        public event Action<MusicCue> Cancelled;

        public void SetTarget(MusicState state, MusicEntrance entrance = MusicEntrance.LoopEntrance, double maxWait = 15) =>
            SetTarget(state, new MusicTrigger(entrance, MusicTriggerTime.NextExit, true), maxWait);

        /// <summary>False while a <see cref="MusicTrigger.DisableTriggers"/> target has not played through yet.</summary>
        public bool AcceptsTriggers => !_locked;

        /// <param name="maxWait">Longest wait for an exit link with <see cref="MusicTriggerTime.NextExit"/> (seconds).</param>
        /// <returns>False when the trigger was refused (locked by a previous trigger).</returns>
        public bool SetTarget(MusicState state, MusicTrigger trigger, double maxWait = 15)
        {
            if (_locked) return false;
            if (_hasTarget && state.Equals(_target)) return true;
            _target = state;
            _hasTarget = true;
            _entrance = trigger.Entrance;
            _maxWait = trigger.Time == MusicTriggerTime.Immediately ? 0 : maxWait;
            _useTransition = trigger.UseTransition;
            _locked = trigger.DisableTriggers;
            _oneShotDone = false;
            return true;
        }

        /// <summary>Fades everything out and forgets the target.</summary>
        public void Stop(double now)
        {
            Forget();
            _hasTarget = false;
            _locked = false;
            CancelNext(now);
            if (_current != null) FadeOutAndEnd(_current, now, now + StopFade);
        }

        public void Update(double now)
        {
            _cues.RemoveAll(c => c.End <= now);
            if (_next != null && now >= _next.Start) { _current = _next; _next = null; _nextIsChange = false; }
            if (_current != null && _current.End <= now) _current = null;
            if (!_hasTarget) return;

            var wanted = _catalog.FindFragments(_target.Location, _target.Situation, _target.DayTime);
            if (wanted.Count == 0) _locked = false;   // nothing to play through: do not block later triggers
            if (_current == null)
            {
                if (_next == null && wanted.Count > 0 && !_oneShotDone) StartFresh(now, Pick(wanted));
                return;
            }

            var heading = _next ?? _current;
            if (wanted.Count > 0 && !heading.Fragment.SameFamily(wanted[0]) && TryUndoChange(now, wanted[0]))
                heading = _current;
            if (wanted.Count == 0 || !heading.Fragment.SameFamily(wanted[0]))
            {
                if (_nextIsChange) return;   // a committed change is audible already; re-plan once it has started
                if (wanted.Count == 0 && _current.FadingOut) return;
                CancelNext(now);
                if (wanted.Count == 0) FadeOutAndEnd(_current, now, now + StopFade);
                else PlanChange(now, Pick(wanted));
                return;
            }

            if (_next == null && _current.SampleEnd - now <= LoopLookahead) PlanSuccessor();
        }

        // ------------------------------------------------------------------ planning

        /// <summary>
        /// Undoes the last change when it has not really been heard: (a) it is still only planned (its transition and new
        /// piece start in the future) → both are dropped and the playing piece goes on unfaded, whatever the new target;
        /// (b) it was a crossfade whose new piece is still fading in and the target is the piece being left again → the
        /// crossfade is reversed. True when <see cref="_current"/> changed or the plan was dropped.
        /// </summary>
        bool TryUndoChange(double now, MusicFragmentData wanted)
        {
            if (_from == null || !_fromWasClean || !_cues.Contains(_from) || _from.End <= now) return false;
            if (_nextIsChange && _next != null && _current == _from && _next.Start > now
                && (_transitionCue == null || _transitionCue.Start > now))
            {
                var dropped = _next;
                _cues.Remove(_next);
                if (_transitionCue != null) _cues.Remove(_transitionCue);
                _next = null;
                _nextIsChange = false;
                Restore(_from, now);
                Forget();
                Cancelled?.Invoke(dropped);
                return true;
            }
            if (_transitionCue == null && _current != null && _current != _from && !_nextIsChange
                && now < _current.Start + FallbackCrossfade && _from.Fragment.SameFamily(wanted))
            {
                var dropped = _current;
                CancelNext(now);
                FadeOutAndEnd(dropped, now, now + FallbackCrossfade);
                Restore(_from, now);
                _current = _from;
                Forget();
                Cancelled?.Invoke(dropped);
                return true;
            }
            return false;
        }

        void Restore(MusicCue c, double now)
        {
            c.Gain.RampTo(now, now + FallbackCrossfade, 1f);
            c.FadingOut = false;
            c.End = c.SampleEnd;
        }

        void Forget()
        {
            _from = null;
            _transitionCue = null;
            _fromWasClean = false;
        }

        void StartFresh(double now, MusicFragmentData f)
        {
            var cue = AddFragment(f, now + MinLead, 0);
            cue.Gain.SetRamp(cue.Start, cue.Start + StartFade, 0f, 1f);
            _current = cue;
        }

        void PlanSuccessor()
        {
            _locked = false;   // the locked target has played through once
            var f = _catalog.Successor(_current.Fragment);
            if (f == null) { _oneShotDone = true; return; }
            var loop = f.FirstLink(MusicLinkKind.Loop);
            _next = AddFragment(f, _current.SampleEnd, loop?.time ?? 0);
            _nextIsChange = false;
        }

        void PlanChange(double now, MusicFragmentData to)
        {
            var cur = _current;
            _from = cur;
            _fromWasClean = !cur.FadingOut && cur.End >= cur.SampleEnd - 1e-6;
            _transitionCue = null;
            var entryLink = _entrance == MusicEntrance.RandomEntrance ? RandomLink(to, MusicLinkKind.Entrance) : to.FirstLink(MusicLinkKind.Entrance);
            int connectIn = entryLink?.connectIn ?? 0;
            double entryOffset = _entrance == MusicEntrance.Begin ? 0 : entryLink?.time ?? 0;

            MusicTransitionData transition = null;
            double exitTime = double.NaN;
            foreach (var exit in ExitsInOrder(cur.Fragment))
            {
                double et = cur.Origin + exit.time;
                if (et < cur.Start) continue;
                if (et - now > _maxWait || et > cur.SampleEnd) break;
                var t = _useTransition ? PickTransition(exit.connectOut, connectIn) : null;
                if (et - (t?.offset ?? 0) < now + MinLead) continue;
                transition = t;
                exitTime = et;
                break;
            }
            if (double.IsNaN(exitTime))
            {
                transition = _useTransition ? PickTransition(UsualExitConnector(cur.Fragment), connectIn) : null;
                exitTime = now + MinLead + (transition?.offset ?? 0);
            }

            double newStart;
            if (transition != null)
            {
                double ts = exitTime - transition.offset;
                var tc = Add(new MusicCue(++_ids, transition, ts, LengthOf(transition.clip, transition.duration)));
                _transitionCue = tc;
                if (transition.fadeInEnd > transition.fadeInBegin)
                    tc.Gain.SetRamp(ts + transition.fadeInBegin, ts + transition.fadeInEnd, 0f, 1f);
                FadeOutAndEnd(cur, ts + transition.fadeOutBegin, ts + Math.Max(transition.fadeOutEnd, transition.fadeOutBegin + 0.05));
                newStart = tc.SampleEnd;
            }
            else
            {
                FadeOutAndEnd(cur, exitTime, exitTime + FallbackCrossfade);
                newStart = exitTime;
            }

            _next = AddFragment(to, newStart, entryOffset);
            if (transition == null) _next.Gain.SetRamp(newStart, newStart + FallbackCrossfade, 0f, 1f);
            _nextIsChange = true;
        }

        // ------------------------------------------------------------------ helpers

        MusicCue AddFragment(MusicFragmentData f, double start, double offset)
        {
            double len = LengthOf(f.clip, f.duration);
            if (offset >= len - 0.5) offset = 0;
            return Add(new MusicCue(++_ids, f, start, offset, len));
        }

        MusicCue Add(MusicCue c)
        {
            _cues.Add(c);
            Scheduled?.Invoke(c);
            return c;
        }

        void CancelNext(double now)
        {
            if (_next == null) return;
            if (_next.Start > now) _cues.Remove(_next);
            _next = null;
            _nextIsChange = false;
        }

        static void FadeOutAndEnd(MusicCue c, double t0, double t1)
        {
            c.Gain.RampTo(t0, t1, 0f);
            c.FadingOut = true;
            if (t1 < c.End) c.End = t1;
        }

        double LengthOf(string clip, float catalogDuration)
        {
            double l = _clipLength != null ? _clipLength(clip) : -1;
            return l > 0 ? l : catalogDuration;
        }

        MusicFragmentData Pick(List<MusicFragmentData> candidates)
        {
            foreach (var f in candidates)
                if (string.IsNullOrEmpty(f.variation) || MusicCatalog.SameVariation(f.variation, "0")) return f;
            return candidates[0];
        }

        MusicLinkData RandomLink(MusicFragmentData f, MusicLinkKind kind)
        {
            var links = new List<MusicLinkData>();
            foreach (var l in f.links) if (l.kind == kind) links.Add(l);
            return links.Count == 0 ? null : links[_rng.Next(links.Count)];
        }

                MusicTransitionData PickTransition(int connectOut, int connectIn)
        {
            var list = _catalog.FindTransitions(connectOut, connectIn);
            return list.Count == 0 ? null : list[_rng.Next(list.Count)];
        }

        static IEnumerable<MusicLinkData> ExitsInOrder(MusicFragmentData f)
        {
            var exits = new List<MusicLinkData>();
            foreach (var l in f.links) if (l.kind == MusicLinkKind.Exit) exits.Add(l);
            exits.Sort((a, b) => a.time.CompareTo(b.time));
            return exits;
        }

        static int UsualExitConnector(MusicFragmentData f)
        {
            var counts = new Dictionary<int, int>();
            int best = 0, bestN = 0;
            foreach (var l in f.links)
            {
                if (l.kind != MusicLinkKind.Exit) continue;
                counts.TryGetValue(l.connectOut, out int n);
                counts[l.connectOut] = ++n;
                if (n > bestN) { best = l.connectOut; bestN = n; }
            }
            return best;
        }
    }
}
