using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>Named story flags ("Gnar_NotEnter"), set and cleared by dialog commands and tested by conditions.</summary>
    public interface IGameEventFlags
    {
        bool IsSet(string flag);
        void Set(string flag);
        void Clear(string flag);
    }

    public sealed class GameEventFlags : IGameEventFlags
    {
        readonly HashSet<string> _set = new HashSet<string>(StringComparer.Ordinal);

        public IEnumerable<string> All => _set;

        public bool IsSet(string flag) => _set.Contains(flag);
        public void Set(string flag) => _set.Add(flag);
        public void Clear(string flag) => _set.Remove(flag);
    }
}
