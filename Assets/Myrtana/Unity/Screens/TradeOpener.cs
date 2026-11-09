using System;
using Myrtana.Session;
using Myrtana.Story;

namespace Myrtana.Unity
{
    /// <summary>
    /// Opens the trade page when a trade topic asks for it (G3 DoTrade → PlayerOpenTrade). fix-140: the conversation is
    /// held while the page is open (<see cref="DialogService.HoldForPage"/>), as G3 parks the trader in IS_Idle_Trade
    /// until the page closes: the trader keeps the talk state — no routine, facing the hero — and the topic's script
    /// (its End) goes on only when the page closes (<see cref="GameScreens"/> resumes it).
    /// </summary>
    public sealed class TradeOpener : ISessionView
    {
        readonly GameScreens _screens;
        readonly TradePage _trade;
        IDisposable _requested;

        public TradeOpener(GameScreens screens, TradePage trade)
        {
            _screens = screens;
            _trade = trade;
        }

        public void Bind(GameSession session)
        {
            _requested?.Dispose();
            _requested = session.Events.Subscribe<TradeRequested>(e =>
            {
                if (!session.Dialog.HoldForPage()) return; // a talk being finished silently (the hero was taken away)
                _trade.With(session, e.Merchant);
                _screens.OpenForDialog(_trade);
            });
        }
    }
}
