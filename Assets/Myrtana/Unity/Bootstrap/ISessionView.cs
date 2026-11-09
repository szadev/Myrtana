using Myrtana.Session;

namespace Myrtana.Unity
{
    /// <summary>
    /// A presentation component that shows or drives a <see cref="GameSession"/>. Loading a save replaces the session,
    /// so views are bound again: <see cref="Bind"/> must drop everything taken from the previous session.
    /// </summary>
    public interface ISessionView
    {
        void Bind(GameSession session);
    }
}
