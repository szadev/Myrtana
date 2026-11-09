using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>Why the hero cannot drop a stack from the inventory page.</summary>
    public enum DropRefusal { None, NotCarried, QuestItem, InHand }

    /// <summary>
    /// The hero drops items from the inventory page [owner, fix-85]: G3 CP 1.75 has no drop on the inventory screen
    /// (DropStack is only used by the AI and scripts), so this is the owner's addition. Quest items (ItemFlags.Mission)
    /// stay (the page plays G3's refusal sound), so do a burning torch or a spell held in the hand; a worn item is taken
    /// off first when no copy of it stays carried. The items become a dropped world item (<see cref="WorldItems.Drop"/>)
    /// of the hero (never theft to pick up), saved with the other world items and picked up like them.
    /// </summary>
    public static class HeroItemDrop
    {
        /// <summary>Whether the hero may drop <paramref name="item"/> now.</summary>
        public static DropRefusal Check(GameSession session, string item)
        {
            if (string.IsNullOrEmpty(item) || session.Items.Inventories.Of(ActorId.Player).Count(item) <= 0) return DropRefusal.NotCarried;
            if (session.Content.Database.TryGet(item, out ItemDef def) && def.IsMission) return DropRefusal.QuestItem;
            if (session.Items.Hands.Holds(item)) return DropRefusal.InHand;
            return DropRefusal.None;
        }

        /// <summary>The most the hero can drop of <paramref name="item"/> (0 when refused).</summary>
        public static int MaxAmount(GameSession session, string item) =>
            Check(session, item) == DropRefusal.None ? session.Items.Inventories.Of(ActorId.Player).Count(item) : 0;

        /// <summary>
        /// Drops up to <paramref name="amount"/> of <paramref name="item"/> at <paramref name="position"/> (x, y, z) facing
        /// <paramref name="yaw"/>; null and the reason when refused.
        /// </summary>
        public static DroppedItem Drop(GameSession session, string item, int amount, float[] position, float yaw, out DropRefusal refusal)
        {
            refusal = Check(session, item);
            if (refusal != DropRefusal.None || amount <= 0) return null;
            var inventory = session.Items.Inventories.Of(ActorId.Player);
            int carried = inventory.Count(item);
            if (amount > carried) amount = carried;
            int quality = inventory.QualityOf(item);
            if (amount >= carried && session.Items.Equipment.IsWorn(item)) session.Items.Equipment.UnequipItem(item);
            int removed = session.Items.Inventories.Consume(ActorId.Player, item, amount);
            if (removed <= 0) return null;
            return session.Items.World.Drop(ActorId.Player, item, removed, quality, position, yaw);
        }
    }
}
