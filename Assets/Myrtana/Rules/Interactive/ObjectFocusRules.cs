namespace Myrtana.Rules
{
    /// <summary>
    /// Which interactive objects the hero's focus selects (port option "Focus decorative objects", fix-79; Docs/
    /// controls-options.md section 7). G3 focuses every object with a gCInteraction_PS (FocusPriority 1 for all of them);
    /// with the option off the purely ambient uses drop out of the focus:
    /// <list type="bullet">
    /// <item><see cref="InteractiveKind.Seat"/>: benches, stools, thrones (sitting only; the small sitting regeneration
    /// is the same as sitting on the ground);</item>
    /// <item><see cref="InteractiveKind.Activity"/>: water pipes, logs to saw, orc drums (PS_Interact_Loop, changes nothing).</item>
    /// </list>
    /// Kept: chests, doors, beds (sleep), crafting and reading stations (anvil, grindstone, cauldron, alchemy, campfire,
    /// barbecue, bookstands and stone plates), talking objects (shrines, altars, furnaces, gates), orc boulders, ore veins,
    /// water barrels and fountains (+10 HP), treasure spots, switches — and any object with a script hook (UseFunc).
    /// </summary>
    public static class ObjectFocusRules
    {
        /// <summary>An object whose use gives no gameplay benefit (sitting, smoking, sawing, drumming).</summary>
        public static bool IsDecorative(InteractiveObjectDef def) =>
            def != null && string.IsNullOrEmpty(def.UseFunc) && IsDecorative(def.Kind);

        public static bool IsDecorative(InteractiveKind kind) => kind == InteractiveKind.Seat || kind == InteractiveKind.Activity;

        /// <param name="focusDecorative">The option: true = original (every object), false = no decorative objects.</param>
        public static bool Focusable(InteractiveObjectDef def, bool focusDecorative) => focusDecorative || !IsDecorative(def);
    }
}
