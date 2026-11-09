using Myrtana.Session;

namespace Myrtana.Unity
{
    /// <summary>Shows the session's <see cref="GameNotices"/> (quests, experience, level up, items, messages) as HUD messages.</summary>
    public sealed class NoticeFeed : ISessionView
    {
        readonly HudMessages _messages;
        readonly System.Func<bool> _muteItems;
        GameNotices _notices;

        /// <param name="muteItems">True while item moves should not be announced (trading).</param>
        public NoticeFeed(HudMessages messages, System.Func<bool> muteItems)
        {
            _messages = messages;
            _muteItems = muteItems;
        }

        public void Bind(GameSession session)
        {
            _notices?.Dispose();
            _notices = new GameNotices(session) { MuteItems = _muteItems };
            _notices.Posted += (text, kind) => _messages.Show(text, kind);
        }
    }
}
