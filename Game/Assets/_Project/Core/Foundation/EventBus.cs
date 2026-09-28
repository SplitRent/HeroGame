using System;
using System.Collections.Generic;

namespace HeroGame.Core.Foundation
{
    /// <summary>
    /// Typed publish/subscribe hub used for decoupled communication between systems
    /// (TDD §5.3). Handlers run synchronously on the simulation thread.
    /// <see cref="Enqueue{T}"/> defers delivery to the next <see cref="Flush"/> so systems
    /// can raise events mid-iteration without re-entrancy problems.
    /// </summary>
    public sealed class EventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();
        private readonly Queue<Action> _deferred = new Queue<Action>();

        public IDisposable Subscribe<T>(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = new List<Delegate>();
                _handlers.Add(typeof(T), list);
            }
            list.Add(handler);
            return new Subscription(() => list.Remove(handler));
        }

        public void Publish<T>(T evt)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
            // Copy so handlers may unsubscribe while being invoked.
            var snapshot = list.ToArray();
            foreach (var handler in snapshot) ((Action<T>)handler)(evt);
        }

        public void Enqueue<T>(T evt)
        {
            _deferred.Enqueue(() => Publish(evt));
        }

        /// <summary>Delivers deferred events. Events enqueued during flushing are delivered in the same call.</summary>
        public int Flush(int maxEvents = 100000)
        {
            var delivered = 0;
            while (_deferred.Count > 0 && delivered < maxEvents)
            {
                _deferred.Dequeue()();
                delivered++;
            }
            return delivered;
        }

        private sealed class Subscription : IDisposable
        {
            private Action _dispose;
            public Subscription(Action dispose) { _dispose = dispose; }

            public void Dispose()
            {
                _dispose?.Invoke();
                _dispose = null;
            }
        }
    }
}
