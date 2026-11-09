using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The last delivery of every spell (fix-110): its route, whether it took effect and whether a G3 effect of it exists to
    /// show. Read by the scenario probes (<c>spellfx:</c>, <c>spellvis:</c>) and logged for the owner's Player.log.
    /// </summary>
    public static class SpellDeliveryLog
    {
        public readonly struct Entry
        {
            public Entry(ActorId caster, SpellRoute route, bool applied, bool visual, float time)
            {
                Caster = caster;
                Route = route;
                Applied = applied;
                Visual = visual;
                Time = time;
            }

            public ActorId Caster { get; }
            public SpellRoute Route { get; }
            public bool Applied { get; }
            /// <summary>An imported G3 effect of the spell exists (else the placeholder glow stands in).</summary>
            public bool Visual { get; }
            public float Time { get; }
        }

        static readonly Dictionary<string, Entry> s_last = new Dictionary<string, Entry>();

        public static void Record(ActorId caster, SpellDef spell, SpellRoute route, bool applied)
        {
            if (spell == null) return;
            bool visual = false;
            foreach (var effect in SpellRoutes.Visuals(spell))
                if (G3EffectPlayer.Has(effect)) { visual = true; break; }
            s_last[spell.Id] = new Entry(caster, route, applied, visual, UnityEngine.Time.time);
            if (caster.IsPlayer) Debug.Log($"[Myrtana] spell {spell.Id}: {route}, {(applied ? "took effect" : "no effect")}, visual {(visual ? "G3" : "placeholder")}");
        }

        public static bool TryGet(string spell, out Entry entry) => s_last.TryGetValue(spell ?? "", out entry);

        public static void Clear() => s_last.Clear();
    }
}
