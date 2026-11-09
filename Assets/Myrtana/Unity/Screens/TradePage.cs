using Myrtana.Core;
using Myrtana.Session;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>A trade page (plain or G3 look) that <see cref="TradeOpener"/> opens after a trade topic.</summary>
    public abstract class TradePage : GameScreen
    {
        protected TradePage(VisualElement hudRoot, string template) : base(hudRoot, template) { }

        protected TradePage(VisualElement parent, VisualElement page) : base(parent, page) { }

        /// <summary>G3 page 5: no HUD and no page sidebar.</summary>
        public override Myrtana.Ui.Skin.HudPage HudPage => Myrtana.Ui.Skin.HudPage.Exclusive;

        /// <summary>Prepares the merchant's stock; call before opening.</summary>
        public abstract void With(GameSession session, ActorId merchant);
    }
}
