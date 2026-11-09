#if !UNITY_5_3_OR_NEWER || UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session.Dev
{
    /// <summary>
    /// Dev-only NPC helpers for scenarios: spawning a new actor from a template NPC (like a summon, but with a chosen
    /// attitude and no summoner) and removing it again.
    /// </summary>
    public static class DevNpcs
    {
        public const string Mark = "~dev";

        /// <summary>Template by exact id, by the name before '#' ("Bloodfly"), or by species; first in id order.</summary>
        public static NpcDef FindTemplate(IEnumerable<NpcDef> definitions, string template)
        {
            if (string.IsNullOrEmpty(template)) return null;
            var defs = definitions.Where(d => d != null && !d.Id.IsPlayer && d.Id.Name.IndexOf(Mark, StringComparison.Ordinal) < 0)
                                  .OrderBy(d => d.Id.Name, StringComparer.Ordinal).ToList();
            return defs.FirstOrDefault(d => string.Equals(d.Id.Name, template, StringComparison.OrdinalIgnoreCase))
                   ?? defs.FirstOrDefault(d => d.Id.Name.IndexOf('#') > 0 &&
                                               string.Equals(d.Id.Name.Substring(0, d.Id.Name.IndexOf('#')), template, StringComparison.OrdinalIgnoreCase))
                   ?? defs.FirstOrDefault(d => string.Equals(d.Combat?.Species ?? d.Species, template, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>"Bloodfly~dev01", the first free one.</summary>
        public static ActorId NewId(string template, Func<ActorId, bool> exists)
        {
            string stem = template;
            int hash = stem.IndexOf('#');
            if (hash > 0) stem = stem.Substring(0, hash);
            for (int i = 1; ; i++)
            {
                var id = new ActorId($"{stem}{Mark}{i:00}");
                if (exists == null || !exists(id)) return id;
            }
        }

        public static bool IsDevSpawn(ActorId id) => id.Name != null && id.Name.IndexOf(Mark, StringComparison.Ordinal) >= 0;

        /// <summary>
        /// A new actor with the template's looks, species, level and weaponry; no routine, enclave or boss/guard role.
        /// <paramref name="attitude"/> is stored as its attitude to the hero (null = the rules decide, e.g. wolves hunt).
        /// </summary>
        public static NpcDef CreatureFrom(NpcDef template, ActorId id, WorldPose pose, Attitude? attitude)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var c = template.Combat;
            var combat = c == null ? null : new NpcCombatDef(c.Level, c.LevelMax, c.Species, c.Humanoid, boss: false, immortal: false,
                friendType: c.FriendType, mage: c.Mage, ambientCreature: c.AmbientCreature, evil: c.Evil, animal: c.Animal,
                gameAnimal: c.GameAnimal, role: NpcRole.Personal, @class: c.Class, hasEnclave: false, faction: c.Faction,
                attitudeToPlayer: attitude, equipment: c.Equipment, enclave: null);
            return new NpcDef(id, template.DisplayName, template.VoiceSet, template.Species, template.Visual, pose, routine: null, combat: combat);
        }

        /// <summary>"hostile|neutral|friendly|..." → attitude; null for empty/"default".</summary>
        public static Attitude? ParseAttitude(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
            return Enum.TryParse<Attitude>(text, true, out var a) ? a : (Attitude?)null;
        }
    }
}
#endif
