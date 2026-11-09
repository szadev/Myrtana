using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>
    /// Maps a key that data uses to name behaviour (a command type, a condition kind, a routine name) to a small
    /// handler object. Registering a key twice is a programming error.
    /// </summary>
    public sealed class HandlerRegistry<TKey, THandler>
    {
        readonly Dictionary<TKey, THandler> _handlers;

        public HandlerRegistry(IEqualityComparer<TKey> comparer = null) =>
            _handlers = new Dictionary<TKey, THandler>(comparer ?? EqualityComparer<TKey>.Default);

        public IEnumerable<TKey> Keys => _handlers.Keys;

        public void Register(TKey key, THandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_handlers.ContainsKey(key)) throw new InvalidOperationException($"A handler for '{key}' is already registered.");
            _handlers.Add(key, handler);
        }

        public bool TryGet(TKey key, out THandler handler) => _handlers.TryGetValue(key, out handler);
    }
}
