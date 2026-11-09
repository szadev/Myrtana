using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The demo build's travel kit (Docs/build.md): G3's own teleporter stones to Ardea, Faring, Nordmar (Hammer clan)
    /// and Varant (Bakaresh). They are ordinary inventory items (permanent, 0 mana, no activation; Docs/map-teleport.md
    /// §1.4), so the trip is the game's own MagicTeleport and the save keeps both the stones and where the hero stands.
    /// Given on request only (the demo hotkey); a normal new game is untouched.
    /// </summary>
    public static class DemoKit
    {
        /// <summary>Stone item ids in the order they are listed to the player.</summary>
        public static readonly IReadOnlyList<string> TeleportStones = new[]
        {
            "It_Teleport_Ardea", "It_Teleport_Faring", "It_Teleport_Hammerclan", "It_Teleport_Bakaresh",
        };

        /// <summary>
        /// Gives the player every stone of <see cref="TeleportStones"/> it does not carry yet and that
        /// <paramref name="exists"/> accepts (null: all). Returns the ids given; empty when the hero has them all.
        /// </summary>
        public static IReadOnlyList<string> Give(Inventories inventories, Func<string, bool> exists = null)
        {
            if (inventories == null) throw new ArgumentNullException(nameof(inventories));
            var given = new List<string>();
            var hero = inventories.Of(ActorId.Player);
            foreach (var stone in TeleportStones)
            {
                if (hero.Count(stone) > 0 || (exists != null && !exists(stone))) continue;
                inventories.Give(ActorId.Player, stone, 1);
                given.Add(stone);
            }
            return given;
        }
    }
}
