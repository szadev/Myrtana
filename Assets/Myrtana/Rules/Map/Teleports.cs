using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Teleport stones (Docs/map-teleport.md §1.4): a stone casts a spell whose effect is <c>MagicTeleport</c>; the
    /// destination is the spell's name without its first four characters (<c>Spl_Teleport_Faring</c> →
    /// <c>Teleport_Faring</c>). Nothing has to be activated: every carried stone is a known destination. A released player
    /// cast publishes <see cref="TeleportRequested"/>; a spell without a destination does nothing, as in G3.
    /// </summary>
    public sealed class Teleports : IDisposable
    {
        public const string Effect = "MagicTeleport";
        const int SpellPrefixLength = 4;

        readonly Dictionary<string, TeleportDestinationDef> _destinations;
        readonly Func<string, SpellDef> _spellOf;
        readonly IEventBus _events;
        readonly IGameLog _log;
        readonly IDisposable _subscription;

        /// <param name="spellOf">The spell an item casts, or the spell of that id (null = none).</param>
        public Teleports(IEnumerable<TeleportDestinationDef> destinations, Func<string, SpellDef> spellOf, IEventBus events, IGameLog log = null)
        {
            _destinations = new Dictionary<string, TeleportDestinationDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in destinations ?? Enumerable.Empty<TeleportDestinationDef>()) _destinations[d.Id] = d;
            _spellOf = spellOf ?? (_ => null);
            _events = events;
            _log = log;
            _subscription = events?.Subscribe<SpellCasted>(OnSpellCasted);
        }

        public IEnumerable<TeleportDestinationDef> All => _destinations.Values;

        public bool TryGet(string destination, out TeleportDestinationDef def) =>
            _destinations.TryGetValue(destination ?? string.Empty, out def);

        /// <summary>True when the item (or spell id) casts a teleport.</summary>
        public bool IsTeleport(string itemOrSpell)
        {
            var spell = _spellOf(itemOrSpell);
            return spell != null && spell.Effect == Effect;
        }

        /// <summary>The destination of a teleport stone or spell; false for other spells and missing destinations.</summary>
        public bool TryDestinationOf(string itemOrSpell, out TeleportDestinationDef destination)
        {
            destination = null;
            var spell = _spellOf(itemOrSpell);
            if (spell == null || spell.Effect != Effect || spell.Id.Length <= SpellPrefixLength) return false;
            return TryGet(spell.Id.Substring(SpellPrefixLength), out destination);
        }

        /// <summary>The destinations of the stones in <paramref name="inventory"/>, in inventory order.</summary>
        public IReadOnlyList<TeleportDestinationDef> Known(Inventory inventory)
        {
            var known = new List<TeleportDestinationDef>();
            if (inventory == null) return known;
            foreach (var stack in inventory.Stacks)
                if (stack.Amount > 0 && TryDestinationOf(stack.Item, out var d) && !known.Contains(d)) known.Add(d);
            return known;
        }

        void OnSpellCasted(SpellCasted e)
        {
            if (!e.Caster.IsPlayer || !IsTeleport(e.Item ?? e.Spell)) return;
            if (TryDestinationOf(e.Item ?? e.Spell, out var destination))
                _events.Publish(new TeleportRequested(destination, e.Item));
            else _log?.Warning($"Teleport spell {e.Spell} has no destination in the content.");
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
