using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    public sealed class EventBus : IEventBus
    {
        readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();
        readonly Queue<Action> _pending = new Queue<Action>();
        bool _dispatching;

        public void Publish<T>(T e) where T : struct, IGameEvent
        {
            _pending.Enqueue(() => Dispatch(e));
            if (_dispatching) return;
            _dispatching = true;
            try
            {
                while (_pending.Count > 0) _pending.Dequeue()();
            }
            finally
            {
                _dispatching = false;
                _pending.Clear();
            }
        }

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_handlers.TryGetValue(typeof(T), out var list)) _handlers[typeof(T)] = list = new List<Delegate>();
            list.Add(handler);
            return new Subscription(() => list.Remove(handler));
        }

        void Dispatch<T>(T e) where T : struct, IGameEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var handler in list.ToArray()) ((Action<T>)handler)(e);
        }

        sealed class Subscription : IDisposable
        {
            Action _unsubscribe;

            public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

            public void Dispose()
            {
                _unsubscribe?.Invoke();
                _unsubscribe = null;
            }
        }
    }
}
