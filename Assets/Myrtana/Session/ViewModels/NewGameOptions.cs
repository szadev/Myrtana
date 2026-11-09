using System;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The CP rule choices of the menus (Docs/game-start.md): Alternative Balancing on the title screen (fixed for a game),
    /// difficulty and Alternative A.I. on the options' Difficulty page (<c>IDD_MENU_DIFFICULTY_2</c>). A session takes
    /// its <see cref="RuleOptions"/> when it is created, so changes apply to the next new or loaded game. Kept outside
    /// save games; the host stores them on <see cref="Changed"/>. Defaults: the owner's choice AB + AAI on, medium.
    /// </summary>
    public sealed class NewGameOptions
    {
        bool _alternativeBalancing = true, _alternativeAI = true;
        Difficulty _difficulty = Difficulty.Medium;

        public event Action Changed;

        public bool AlternativeBalancing
        {
            get => _alternativeBalancing;
            set { if (_alternativeBalancing != value) { _alternativeBalancing = value; Changed?.Invoke(); } }
        }

        public bool AlternativeAI
        {
            get => _alternativeAI;
            set { if (_alternativeAI != value) { _alternativeAI = value; Changed?.Invoke(); } }
        }

        public Difficulty Difficulty
        {
            get => _difficulty;
            set { if (_difficulty != value) { _difficulty = value; Changed?.Invoke(); } }
        }

        /// <summary>The rules of a new session: these choices over <paramref name="basis"/>'s other options.</summary>
        public RuleOptions ToRules(RuleOptions basis = null)
        {
            basis ??= RuleOptions.Default;
            return new RuleOptions(_alternativeBalancing, _alternativeAI, basis.FixPassiveExtendedAi, basis.XpModifierPercent, _difficulty);
        }
    }
}
