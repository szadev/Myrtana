using G3.Effects;

namespace G3.Unity
{
    /// <summary>The G3 effect names of the burning and frozen states for game code (<see cref="StatusAuraRules"/>, Docs/formats-effects.md §8).</summary>
    public static class G3StatusEffects
    {
        public static string Burning => StatusAuraRules.BurningEffect;

        public static string Frozen(bool humanoid, bool ambientCreature, bool large) =>
            StatusAuraRules.FrozenEffect(StatusAuraRules.BodyOf(humanoid, ambientCreature, large));

        public static string Thaw(bool humanoid, bool ambientCreature, bool large) =>
            StatusAuraRules.ThawBurst(StatusAuraRules.BodyOf(humanoid, ambientCreature, large));
    }
}
