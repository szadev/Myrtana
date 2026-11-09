using Myrtana.Session;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>The character screen (key C): level and experience, attributes, reputation (<see cref="CharacterSheet"/>).</summary>
    public sealed class CharacterScreen : GameScreen
    {
        readonly Label _title, _hint;
        readonly VisualElement _experience, _attributes, _reputation;

        public CharacterScreen(VisualElement hudRoot) : base(hudRoot, "CharacterScreen")
        {
            _title = Root.Q<Label>("screen-title");
            _hint = Root.Q<Label>("screen-hint");
            _experience = Root.Q("experience");
            _attributes = Root.Q("attributes");
            _reputation = Root.Q("reputation");
        }

        public override void Render()
        {
            var sheet = CharacterSheet.Of(Session);
            _title.text = sheet.Title;
            _hint.text = "C / Esc";
            _experience.Clear();
            _attributes.Clear();
            _reputation.Clear();
            AddSection(_experience, sheet.ExperienceTitle, sheet.Experience);
            AddSection(_attributes, sheet.AttributesTitle, sheet.Attributes);
            if (sheet.Reputation.Count > 0) AddSection(_reputation, sheet.ReputationTitle, sheet.Reputation);
        }
    }
}
