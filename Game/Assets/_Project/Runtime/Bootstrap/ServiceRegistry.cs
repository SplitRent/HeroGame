using System;
using System.Collections.Generic;

namespace HeroGame.Runtime.Bootstrap
{
    /// <summary>
    /// Minimal typed service registry (TDD §5.2). Systems receive dependencies explicitly where
    /// practical; the registry exists for the seams Unity makes awkward (scene objects created by
    /// designers, UI documents). Nothing registers itself implicitly; <see cref="GameBootstrap"/>
    /// owns registration order, so dependencies are visible in one place.
    /// </summary>
    public static class ServiceRegistry
    {
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();

        public static event Action<Type> Changed;

        public static void Register<T>(T service) where T : class
        {
            Services[typeof(T)] = service ?? throw new ArgumentNullException(nameof(service));
            Changed?.Invoke(typeof(T));
        }

        public static void Unregister<T>(T service) where T : class
        {
            if (Services.TryGetValue(typeof(T), out var current) && ReferenceEquals(current, service))
            {
                Services.Remove(typeof(T));
                Changed?.Invoke(typeof(T));
            }
        }

        public static bool TryGet<T>(out T service) where T : class
        {
            if (Services.TryGetValue(typeof(T), out var s))
            {
                service = (T)s;
                return true;
            }
            service = null;
            return false;
        }

        public static T Get<T>() where T : class
        {
            if (TryGet<T>(out var s)) return s;
            throw new InvalidOperationException("Service not registered: " + typeof(T).Name);
        }

        /// <summary>Clears everything (domain reload disabled / tests).</summary>
        public static void Reset() => Services.Clear();
    }
}
