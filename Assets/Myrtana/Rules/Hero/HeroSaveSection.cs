using System;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Save section "rules.hero": level, XP, learning points, permanent attributes, learned skills and the NPCs that
    /// already paid kill XP. Equipment bonuses are not saved; they are recomputed from the equipment.
    /// </summary>
    public sealed class HeroSaveSection : ISaveParticipant
    {
        readonly Hero _hero;
        readonly KillXpAwarder _killXp;

        public HeroSaveSection(Hero hero, KillXpAwarder killXp)
        {
            _hero = hero;
            _killXp = killXp;
        }

        public string Section => "rules.hero";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            w.WriteInt32(_hero.Level);
            w.WriteUInt64((ulong)_hero.Xp);
            w.WriteInt32(_hero.LearningPoints);
            var ids = _hero.Stats.Ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
            w.WriteInt32(ids.Count);
            foreach (var id in ids)
            {
                var v = _hero.Stats.Permanent(id);
                w.WriteString(id);
                w.WriteInt32(v.Value);
                w.WriteInt32(v.Maximum);
            }
            w.WriteSortedStrings(_hero.Skills);
            w.WriteSortedStrings(_killXp.Rewarded.Select(a => a.Name));
        }

        public void Read(ISaveReader r, int version)
        {
            _hero.Level = r.ReadInt32();
            _hero.Xp = (long)r.ReadUInt64();
            _hero.LearningPoints = r.ReadInt32();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                int value = r.ReadInt32(), max = r.ReadInt32();
                if (id == ActorVitals.HitPointsId) RepairDevGodHitPoints(ref value, ref max);
                _hero.Stats.Set(id, new StatValue(value, max));
            }
            foreach (var skill in r.ReadStrings()) _hero.Learn(skill);
            foreach (var victim in r.ReadStrings()) _killXp.MarkRewarded(new ActorId(victim));
            _hero.NotifyChanged();
        }

        /// <summary>
        /// fix-132: the dev console's god and tp (builds up to 18f) wrote 99999/99999 into the hero's HP attribute and never
        /// put it back, and saves kept it. No rule raises the HP maximum that far, so such a maximum is that god's: it goes
        /// back to the new-game maximum (the session's hero before the load, i.e. the content's HeroStart HP) and the
        /// current HP is clamped to it. HP gains made before the god are lost with it (the god had overwritten them).
        /// </summary>
        void RepairDevGodHitPoints(ref int value, ref int max)
        {
            if (max < ActorVitals.InvulnerableHitPoints) return;
            int start = _hero.Stats.Permanent(ActorVitals.HitPointsId).Maximum;
            if (start <= 0 || start >= ActorVitals.InvulnerableHitPoints) return;
            max = start;
            value = Math.Max(1, Math.Min(value, max));
        }
    }
}
