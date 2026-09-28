using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.Presentation
{
    /// <summary>
    /// Talks to the render pipeline's volume system (HDRP / SRP Core) by reflection, so runtime scripts compile and
    /// run without a hard package dependency (the headless compile check has no HDRP, and the game must still start
    /// on the built-in pipeline). Every call degrades to a no-op when a type or member is missing.
    /// </summary>
    public static class PipelineReflection
    {
        public const string Hd = "UnityEngine.Rendering.HighDefinition.";
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();

        public static Type FindType(string fullName)
        {
            if (Types.TryGetValue(fullName, out var cached)) return cached;
            Type found = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = assembly.GetType(fullName, false);
                if (found != null) break;
            }
            Types[fullName] = found;
            return found;
        }

        /// <summary>A new runtime VolumeProfile, or null without SRP Core.</summary>
        public static ScriptableObject NewProfile()
        {
            var type = FindType("UnityEngine.Rendering.VolumeProfile");
            return type != null ? ScriptableObject.CreateInstance(type) : null;
        }

        /// <summary>Adds a global Volume with <paramref name="profile"/> to <paramref name="go"/>.</summary>
        public static Component AddGlobalVolume(GameObject go, ScriptableObject profile, float priority)
        {
            var type = FindType("UnityEngine.Rendering.Volume");
            if (type == null || profile == null) return null;
            var volume = go.AddComponent(type);
            type.GetField("isGlobal")?.SetValue(volume, true);
            type.GetField("priority")?.SetValue(volume, priority);
            // The runtime profile is ours: assign it as the shared profile (no per-volume copy).
            type.GetField("sharedProfile")?.SetValue(volume, profile);
            return volume;
        }

        /// <summary>The profile's override of <paramref name="typeName"/>, added (with nothing overridden) if missing.</summary>
        public static ScriptableObject Override(ScriptableObject profile, string typeName)
        {
            if (profile == null) return null;
            var type = FindType(typeName);
            if (type == null) return null;
            var components = profile.GetType().GetField("components")?.GetValue(profile) as System.Collections.IList;
            if (components != null)
                foreach (var c in components)
                    if (c != null && c.GetType() == type) return (ScriptableObject)c;
            var add = profile.GetType().GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            return add?.Invoke(profile, new object[] { type, false }) as ScriptableObject;
        }

        /// <summary>Turns a volume override on or off as a whole.</summary>
        public static void SetActive(ScriptableObject component, bool active)
        {
            if (component == null) return;
            var property = component.GetType().GetProperty("active");
            if (property != null && property.CanWrite) property.SetValue(component, active);
            else component.GetType().GetField("active")?.SetValue(component, active);
        }

        /// <summary>Overrides one parameter (enum values may be given by name).</summary>
        public static void Set(object component, string field, object value)
        {
            if (component == null) return;
            try
            {
                var parameter = component.GetType().GetField(field)?.GetValue(component);
                var property = parameter?.GetType().GetProperty("value");
                if (property == null) return;
                property.SetValue(parameter, Convert(value, property.PropertyType));
                parameter.GetType().GetProperty("overrideState")?.SetValue(parameter, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Graphics] " + component.GetType().Name + "." + field + ": " + e.Message);
            }
        }

        /// <summary>Sets a plain field or property on a component (camera/light additional data).</summary>
        public static bool SetMember(object target, string name, object value)
        {
            if (target == null) return false;
            try
            {
                var t = target.GetType();
                var field = t.GetField(name);
                if (field != null)
                {
                    field.SetValue(target, Convert(value, field.FieldType));
                    return true;
                }
                var property = t.GetProperty(name);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(target, Convert(value, property.PropertyType));
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Graphics] " + target.GetType().Name + "." + name + ": " + e.Message);
            }
            return false;
        }

        public static void Invoke(object target, string method, params object[] args)
        {
            if (target == null) return;
            foreach (var m in target.GetType().GetMethods())
            {
                if (m.Name != method || m.GetParameters().Length != args.Length) continue;
                try
                {
                    var ps = m.GetParameters();
                    var converted = new object[args.Length];
                    for (var i = 0; i < args.Length; i++) converted[i] = Convert(args[i], ps[i].ParameterType);
                    m.Invoke(target, converted);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Graphics] " + target.GetType().Name + "." + method + ": " + e.Message);
                }
                return;
            }
        }

        private static object Convert(object value, Type target)
        {
            if (value == null || target.IsInstanceOfType(value)) return value;
            if (target.IsEnum) return value is string name ? Enum.Parse(target, name) : Enum.ToObject(target, value);
            return System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
