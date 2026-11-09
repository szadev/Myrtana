namespace Myrtana.Rules
{
    /// <summary>What the hero's focus is when the quick-loot chord is pressed (fix-226).</summary>
    public enum ChordFocus
    {
        /// <summary>No focus.</summary>
        None,
        /// <summary>A body, robbable sleeper or chest: the chord empties it (fix-205).</summary>
        Lootable,
        /// <summary>
        /// fix-234: a world item the hero can pick up (a weapon dropped in the fight, anything lying on the floor). With a
        /// drawn stance (no item focus in G3) this is a world item lying in front more directly than the best body.
        /// </summary>
        Item,
        /// <summary>Anything else: a living NPC, a plant, a door, a station.</summary>
        Other,
    }

    /// <summary>A body near the hero that the chord could loot instead of the focus (fix-226).</summary>
    public enum ChordBody
    {
        /// <summary>No downed or dead NPC in reach in front of the hero.</summary>
        None,
        /// <summary>A body with loot (the focus would take it).</summary>
        WithLoot,
        /// <summary>A body with nothing the hero can take (looted out, or only trophies of perks he lacks).</summary>
        Empty,
    }

    /// <summary>What the chord press does.</summary>
    public enum ChordAction
    {
        /// <summary>Not a chord: the press does what it always does (use the focus, or the drawn stance's attack).</summary>
        Default,
        /// <summary>Quick loot the focus (fix-205).</summary>
        LootFocus,
        /// <summary>Quick loot the body found in front of the hero instead of the focus (fix-226).</summary>
        LootBody,
        /// <summary>Pick up the focused world item, as a plain use does (fix-234).</summary>
        TakeItem,
    }

    /// <summary>
    /// fix-226 [owner] QoL, not G3: where the quick-loot chord (Ctrl / right button held + "Aktion 1") takes from. fix-205 only
    /// emptied the focus, so on a dead animal it often did nothing: the focus went to a plant, an item or an object lying by
    /// the corpse (any of them closer to the hero's facing wins it), or the weapon of the fight that killed it was still drawn
    /// (G3 PS_Melee/Ranged/Magic: Use1 attacks and only fight targets are focused). Now:
    /// <list type="bullet">
    /// <item>empty hands: a lootable focus is emptied as before; otherwise a body with loot in reach in front of the hero
    /// is (its loot is what the loot page would show: carried items, the trophies the hero's perks gave, late trophies);
    /// with no focus at all an empty body is "looted" too, so the hero says "NothingToGet" as Use on a body without loot
    /// does; else the press is a plain use;</item>
    /// <item>a drawn stance: only the modifier (Ctrl) chord, never the right button (the parade), and only onto a body with
    /// loot in front; otherwise the attack, shot or cast goes as in G3.</item>
    /// </list>
    /// fix-234 [owner] "the chord must not get in the way of picking up a weapon from the floor": a focused world item always
    /// wins. With empty hands the chord takes it as a plain use does (never the body beside it); with a drawn stance an item
    /// lying in front more directly than the body keeps the press for the stance (G3 takes no items with a weapon drawn:
    /// fix-90, only fight targets are focused), so the body fallback never swallows a click aimed at the item.
    /// Like the loot page, looting a body is no crime and has no hero motion (G3 has none; CP's quick loot neither).
    /// </summary>
    public static class QuickLootChordTarget
    {
        /// <param name="chord">The chord is held with the press (option on, modifier or right button held).</param>
        /// <param name="modifierHeld">The quick-loot modifier (Ctrl) is held.</param>
        /// <param name="stanceDrawn">A weapon, bow, spell or the fists are drawn.</param>
        /// <param name="focus">
        /// The interaction focus; with a drawn stance <see cref="ChordFocus.Item"/> when a world item lies in front more
        /// directly than the body, else <see cref="ChordFocus.None"/>.
        /// </param>
        /// <param name="body">The best body in front of the hero.</param>
        public static ChordAction Decide(bool chord, bool modifierHeld, bool stanceDrawn, ChordFocus focus, ChordBody body)
        {
            if (!chord) return ChordAction.Default;
            if (focus == ChordFocus.Item) return stanceDrawn ? ChordAction.Default : ChordAction.TakeItem;
            if (stanceDrawn) return modifierHeld && body == ChordBody.WithLoot ? ChordAction.LootBody : ChordAction.Default;
            if (focus == ChordFocus.Lootable) return ChordAction.LootFocus;
            if (body == ChordBody.WithLoot) return ChordAction.LootBody;
            if (focus == ChordFocus.None && body == ChordBody.Empty) return ChordAction.LootBody;
            return ChordAction.Default;
        }

        /// <summary>
        /// fix-238 [owner] "while the right button is held every left click takes all from what it is aimed at": the left
        /// click of a right-button hold is swallowed only when it would take from the very target that hold's own right-button
        /// quick loot (CP 0x90) just emptied (<paramref name="rightButtonLooted"/>, null when it looted nothing), so the chord
        /// does not repeat "NothingToGet" on it. Any other body, chest or item (<paramref name="chordTarget"/>; null = a floor
        /// item or nothing, never swallowed) is looted, so one hold empties several bodies. The Ctrl chord is never swallowed.
        /// </summary>
        public static bool SwallowsLeftClick(bool rightButtonHeld, string rightButtonLooted, string chordTarget) =>
            rightButtonHeld && rightButtonLooted != null && chordTarget != null && chordTarget == rightButtonLooted;

        /// <summary>
        /// The Ctrl press of a possible chord holds the sneak toggle back (fix-205 <c>ChordedToggle</c>): something to loot is
        /// in focus, or, focus or not, a body with loot is in front (also with a drawn stance, as the chord then loots it);
        /// fix-234: with empty hands a focused world item too (the chord takes it), with a drawn stance an item in front
        /// does not (the press stays the attack: sneak on the press as in G3).
        /// </summary>
        public static bool DefersSneak(bool stanceDrawn, ChordFocus focus, ChordBody body)
        {
            if (focus == ChordFocus.Item) return !stanceDrawn;
            return body == ChordBody.WithLoot || !stanceDrawn && focus == ChordFocus.Lootable;
        }
    }
}
