using System;

namespace Myrtana.Core
{
    /// <summary>Marker for events published on the <see cref="IEventBus"/> (small immutable structs).</summary>
    public interface IGameEvent { }

    /// <summary>
    /// Synchronous, typed in-process event bus between modules. Events published while a handler runs are queued
    /// and delivered after it, so the delivery order is deterministic.
    /// </summary>
    public interface IEventBus
    {
        void Publish<T>(T e) where T : struct, IGameEvent;
        IDisposable Subscribe<T>(Action<T> handler) where T : struct, IGameEvent;
    }
}
