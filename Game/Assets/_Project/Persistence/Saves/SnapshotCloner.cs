using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace HeroGame.Persistence.Saves
{
    /// <summary>
    /// Deep-copies save payloads so they can be serialized on another thread while the simulation keeps mutating the
    /// originals. Each object is copied member-wise in one step (value fields for free) and only its reference-typed
    /// fields are followed; strings and types listed as immutable are shared. Payloads are trees (JSON could not
    /// serialize cycles either), so no identity map is needed. Types are analysed once and cached.
    /// </summary>
    public static class SnapshotCloner
    {
        private enum Kind { Shared, Object, Array, List, Dictionary, CopyConstructed }

        private sealed class Plan
        {
            public Kind Kind;
            public FieldInfo[] ReferenceFields = Array.Empty<FieldInfo>();
            public Type Element;
            public bool ElementShared;
            public Type Key;
            public bool KeyShared;
        }

        private static readonly ConcurrentDictionary<Type, Plan> Plans = new ConcurrentDictionary<Type, Plan>();
        private static readonly HashSet<Type> Immutable = new HashSet<Type>
        {
            typeof(string), typeof(Type),
            typeof(Core.Population.LifeEvent), // never edited after it is recorded
            typeof(Core.Powers.PowerDefinition), // fixed once generated
            typeof(Core.Building.BuildingLayout), // copy-on-write: builds replace the object
        };

        private static readonly MethodInfo MemberwiseMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Func<object, object> Memberwise = CreateMemberwise();

        private static Func<object, object> CreateMemberwise()
        {
            try { return (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>), MemberwiseMethod); }
            catch (Exception) { return o => MemberwiseMethod.Invoke(o, null); } // AOT platforms without open-instance delegates
        }

        public static T Clone<T>(T value) where T : class => (T)CloneObject(value);

        private static object CloneObject(object o)
        {
            if (o == null) return null;
            var plan = PlanFor(o.GetType());
            switch (plan.Kind)
            {
                case Kind.Shared:
                    return o;
                case Kind.Array:
                {
                    var source = (Array)o;
                    var copy = (Array)source.Clone();
                    if (!plan.ElementShared)
                        for (var i = 0; i < copy.Length; i++) copy.SetValue(CloneObject(source.GetValue(i)), i);
                    return copy;
                }
                case Kind.List:
                {
                    var source = (IList)o;
                    if (plan.ElementShared) return Activator.CreateInstance(o.GetType(), o); // List<T>(IEnumerable<T>)
                    var copy = (IList)Activator.CreateInstance(o.GetType(), source.Count);
                    foreach (var item in source) copy.Add(CloneObject(item));
                    return copy;
                }
                case Kind.Dictionary:
                {
                    var source = (IDictionary)o;
                    var copy = (IDictionary)CreateLike(o);
                    foreach (DictionaryEntry e in source)
                        copy.Add(plan.KeyShared ? e.Key : CloneObject(e.Key), plan.ElementShared ? e.Value : CloneObject(e.Value));
                    return copy;
                }
                case Kind.CopyConstructed:
                    return Activator.CreateInstance(o.GetType(), o);
                default:
                {
                    var copy = Memberwise(o);
                    foreach (var f in plan.ReferenceFields) f.SetValue(copy, CloneObject(f.GetValue(o)));
                    return copy;
                }
            }
        }

        /// <summary>A new empty dictionary of the same type, keeping its comparer.</summary>
        private static object CreateLike(object dictionary)
        {
            var type = dictionary.GetType();
            var comparer = type.GetProperty("Comparer")?.GetValue(dictionary);
            var key = type.GetGenericArguments()[0];
            var comparerType = (type.GetGenericTypeDefinition() == typeof(SortedDictionary<,>) ? typeof(IComparer<>) : typeof(IEqualityComparer<>)).MakeGenericType(key);
            var ctor = comparer != null ? type.GetConstructor(new[] { comparerType }) : null;
            return ctor != null ? ctor.Invoke(new[] { comparer }) : Activator.CreateInstance(type);
        }

        private static Plan PlanFor(Type t) => Plans.GetOrAdd(t, Analyse);

        private static bool IsShared(Type t) => PlanFor(t).Kind == Kind.Shared;

        private static Plan Analyse(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t.IsPointer || t == typeof(decimal) || Immutable.Contains(t) || typeof(Delegate).IsAssignableFrom(t))
                return new Plan { Kind = Kind.Shared };
            if (t.IsArray)
            {
                var e = t.GetElementType();
                return new Plan { Kind = Kind.Array, Element = e, ElementShared = e.IsValueType && ValueTypeIsShared(e) || !e.IsValueType && IsShared(e) && e.IsSealed };
            }
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                if (def == typeof(List<>))
                    return new Plan { Kind = Kind.List, Element = args[0], ElementShared = ElementShared(args[0]) };
                if (def == typeof(Dictionary<,>) || def == typeof(SortedDictionary<,>))
                    return new Plan { Kind = Kind.Dictionary, Key = args[0], KeyShared = ElementShared(args[0]), Element = args[1], ElementShared = ElementShared(args[1]) };
                if (def == typeof(HashSet<>) || def == typeof(SortedSet<>) || def == typeof(Queue<>))
                {
                    if (!ElementShared(args[0])) throw new NotSupportedException("SnapshotCloner: " + t + " of mutable elements");
                    return new Plan { Kind = Kind.CopyConstructed };
                }
            }
            if (t.IsValueType && ValueTypeIsShared(t)) return new Plan { Kind = Kind.Shared };
            if (typeof(IEnumerable).IsAssignableFrom(t) && t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal))
                throw new NotSupportedException("SnapshotCloner: unsupported collection " + t);

            // A plain object (or a struct holding references): copy member-wise, then follow reference fields.
            var fields = new List<FieldInfo>();
            for (var type = t; type != null && type != typeof(object) && type != typeof(ValueType); type = type.BaseType)
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = f.FieldType;
                    if (ft.IsValueType ? ValueTypeIsShared(ft) : Immutable.Contains(ft)) continue;
                    fields.Add(f);
                }
            return new Plan { Kind = Kind.Object, ReferenceFields = fields.ToArray() };
        }

        private static bool ElementShared(Type e) => e.IsValueType ? ValueTypeIsShared(e) : Immutable.Contains(e) || e.IsSealed && IsShared(e);

        private static readonly ConcurrentDictionary<Type, bool> ValueTypes = new ConcurrentDictionary<Type, bool>();

        /// <summary>A struct is shared (copied by value) when none of its fields, recursively, is a mutable reference.</summary>
        private static bool ValueTypeIsShared(Type t) => ValueTypes.GetOrAdd(t, type =>
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return true;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>)) return ValueTypeIsShared(type.GetGenericArguments()[0]);
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = f.FieldType;
                if (ft.IsValueType ? !ValueTypeIsShared(ft) : !Immutable.Contains(ft)) return false;
            }
            return true;
        });
    }
}
