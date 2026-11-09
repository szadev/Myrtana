using Myrtana.Rules;
using Myrtana.Session;

namespace Myrtana.Unity
{
    /// <summary>
    /// Holds the session's <see cref="CraftingServices"/> for the station and document pages. The session owns them and
    /// the bookstand memory (saved as section "world.bookstands").
    /// </summary>
    public sealed class CraftingHost : ISessionView
    {
        public CraftingServices Services { get; private set; }

        public void Bind(GameSession session) => Services = session?.Crafting;
    }
}
