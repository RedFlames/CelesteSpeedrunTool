using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Celeste.Mod.SpeedrunTool.Extensions;

internal static class ReflectionExtensions {
    private const BindingFlags StaticInstanceAnyVisibility =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // ReSharper disable UnusedMember.Local
    private record struct MemberKey(Type Type, string Name) {
        public readonly Type Type = Type;
        public readonly string Name = Name;
    }

    private record struct MethodKey(Type Type, string Name, long Types) {
        public readonly Type Type = Type;
        public readonly string Name = Name;
        public readonly long Types = Types;
    }

    private record struct DelegateKey(Type Type, string Name, Type ReturnType) {
        public readonly Type Type = Type;
        public readonly string Name = Name;
        public readonly Type ReturnType = ReturnType;
    }
    // ReSharper restore UnusedMember.Local

    public delegate T GetDelegate<out T>(object instance);

    private static readonly ConcurrentDictionary<MemberKey, FieldInfo> CachedFieldInfos = new();
    private static readonly ConcurrentDictionary<MemberKey, PropertyInfo> CachedPropertyInfos = new();
    private static readonly ConcurrentDictionary<MethodKey, MethodInfo> CachedMethodInfos = new();
    private static readonly ConcurrentDictionary<MethodKey, FastReflectionDelegate> CachedMethodDelegates = new();
    private static readonly ConcurrentDictionary<DelegateKey, Delegate> CachedFieldGetDelegates = new();

    private static readonly object[] NoArg = { };
    private static readonly object[] NullArg = {null};
    private static readonly Type[] EmptyTypes = { };

    private static readonly HashSet<Type> SimpleTypes = new() {
        typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(string), typeof(bool),
        typeof(DateTime), typeof(TimeSpan), typeof(DateTimeOffset), typeof(Vector2), typeof(Vector3),
        typeof(VertexPositionColor), typeof(Color)
    };

    public static bool IsSimple(this Type type, Func<Type, bool> extraTypes = null) {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>)) {
            // nullable type, check if the nested type is simple.
            return IsSimple(type.GetGenericArguments()[0], extraTypes);
        }

        return SimpleTypes.Contains(type) || type.IsEnum || extraTypes?.Invoke(type) == true;
    }

    public static bool IsSimpleClass(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return IsSimple(type, extraGenericTypes)
               || IsSimpleArray(type, extraGenericTypes)
               || IsSimpleList(type, extraGenericTypes)
               || IsSimpleStack(type, extraGenericTypes)
               || IsSimpleHashSet(type, extraGenericTypes)
               || IsSimpleDictionary(type, extraGenericTypes);
    }

    public static bool IsSimpleArray(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return type.IsSingleRankArray() && type.GetElementType().IsSimple(extraGenericTypes);
    }

    public static bool IsSimpleList(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return type.IsList(out Type genericType) && genericType.IsSimple(extraGenericTypes);
    }

    public static bool IsSimpleStack(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return type.IsStack(out Type genericType) && genericType.IsSimple(extraGenericTypes);
    }

    public static bool IsSimpleHashSet(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return type.IsHashSet(out Type genericType) && genericType.IsSimple(extraGenericTypes);
    }

    public static bool IsSimpleDictionary(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return type.IsDictionary(out Type keyType, out Type valueType) && keyType.IsSimple(extraGenericTypes) &&
               valueType.IsSimple(extraGenericTypes);
    }

    public static bool IsSimpleWeakReference(this Type type, Func<Type, bool> extraGenericTypes = null) {
        return type.IsWeakReference(out Type genericType) && genericType.IsSimple(extraGenericTypes);
    }

    public static bool IsSingleRankArray(this Type type) {
        return type.IsArray && type.GetArrayRank() == 1;
    }

    public static bool IsList(this Type type, out Type genericType) {
        bool result = type.IsGenericType && type.GetGenericTypeDefinition().IsAssignableFrom(typeof(List<>))
                                         && type.GenericTypeArguments.Length == 1;

        genericType = result ? type.GenericTypeArguments[0] : null;

        return result;
    }

    public static bool IsStack(this Type type, out Type genericType) {
        bool result = type.IsGenericType && type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Stack<>))
                                         && type.GenericTypeArguments.Length == 1;

        genericType = result ? type.GenericTypeArguments[0] : null;

        return result;
    }

    public static bool IsHashSet(this Type type, out Type genericType) {
        genericType = null;

        if (!type.IsGenericType || type.IsGenericTypeDefinition || !type.IsClass) {
            return false;
        }

        Type[] genericTypeArguments = type.GetGenericArguments();

        if (genericTypeArguments.Length != 1) {
            return false;
        }

        if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(HashSet<>))) {
            genericType = genericTypeArguments[0];
            return true;
        }

        return false;
    }

    public static bool IsDictionary(this Type type, out Type keyType, out Type valueType) {
        bool result = type.IsGenericType && type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Dictionary<,>))
                                         && type.GenericTypeArguments.Length == 2;

        keyType = result ? type.GenericTypeArguments[0] : null;
        valueType = result ? type.GenericTypeArguments[1] : null;

        return result;
    }

    public static bool IsIDictionary(this Type type, out Type keyType, out Type valueType) {
        keyType = null;
        valueType = null;

        if (!type.IsGenericType || type.IsGenericTypeDefinition || !type.IsClass) {
            return false;
        }

        Type[] genericTypeArguments = type.GetGenericArguments();

        if (genericTypeArguments.Length != 2) {
            return false;
        } else {
            foreach (Type @interface in type.GetInterfaces()) {
                if (@interface.IsGenericType && @interface.GetGenericTypeDefinition() == typeof(IDictionary<,>)) {
                    keyType = genericTypeArguments[0];
                    valueType = genericTypeArguments[1];
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsWeakReference(this Type type, out Type genericType) {
        bool result = type.IsGenericType && type.GetGenericTypeDefinition().IsAssignableFrom(typeof(WeakReference<>))
                                         && type.GenericTypeArguments.Length == 1;

        genericType = result ? type.GenericTypeArguments[0] : null;

        return result;
    }

    public static bool IsCompilerGenerated(this object obj) {
        return IsCompilerGenerated(obj.GetType());
    }

    public static bool IsCompilerGenerated(this Type type) {
        return type.Name.StartsWith("<");
    }

    public static bool IsProperty(this MemberInfo memberInfo) {
        return (memberInfo.MemberType & MemberTypes.Property) != 0;
    }

    public static bool IsField(this MemberInfo memberInfo) {
        return (memberInfo.MemberType & MemberTypes.Field) != 0;
    }

    public static bool IsType<T>(this object obj) {
        return obj?.GetType() == typeof(T);
    }

    public static bool IsType<T>(this Type type) {
        return type == typeof(T);
    }

    public static bool IsNotType<T>(this object obj) {
        return !obj.IsType<T>();
    }

    public static bool IsNotType<T>(this Type type) {
        return !type.IsType<T>();
    }

    public static bool IsSameOrSubclassOf(this Type potentialDescendant, Type potentialBase) {
        return potentialDescendant.IsSubclassOf(potentialBase) || potentialDescendant == potentialBase;
    }

    public static bool IsSameOrBaseclassOf(this Type potentialBase, Type potentialDescendant) {
        return potentialDescendant.IsSubclassOf(potentialBase) || potentialBase == potentialDescendant;
    }

    public static bool IsOverride(this MethodInfo methodInfo) {
        return methodInfo.GetBaseDefinition().DeclaringType != methodInfo.DeclaringType;
    }

    public static string ToBackingField(this string fieldName) {
        return $"<{fieldName}>k__BackingField";
    }

    public static FieldInfo GetFieldInfo(this Type type, string name) {
        var key = new MemberKey(type, name);
        if (CachedFieldInfos.TryGetValue(key, out var result)) {
            return result;
        }

        do {
            result = type.GetField(name, StaticInstanceAnyVisibility);
        } while (result == null && (type = type.BaseType) != null);

        return CachedFieldInfos[key] = result;
    }

    public static PropertyInfo GetPropertyInfo(this Type type, string name) {
        var key = new MemberKey(type, name);
        if (CachedPropertyInfos.TryGetValue(key, out var result)) {
            return result;
        }

        do {
            result = type.GetProperty(name, StaticInstanceAnyVisibility);
        } while (result == null && (type = type.BaseType) != null);

        return CachedPropertyInfos[key] = result;
    }

    public static MethodInfo GetMethodInfo(this Type type, string name, Type[] types = null) {
        var key = new MethodKey(type, name, types.GetCustomHashCode());
        if (CachedMethodInfos.TryGetValue(key, out MethodInfo result)) {
            return result;
        }

        do {
            MethodInfo[] methodInfos = type.GetMethods(StaticInstanceAnyVisibility);
            result = methodInfos.FirstOrDefault(info =>
                info.Name == name && types?.SequenceEqual(info.GetParameters().Select(i => i.ParameterType)) != false);
        } while (result == null && (type = type.BaseType) != null);

        return CachedMethodInfos[key] = result;
    }

    public static FastReflectionDelegate GetMethodDelegate(this Type type, string name, Type[] types = null) {
        var key = new MethodKey(type, name, types.GetCustomHashCode());
        if (CachedMethodDelegates.TryGetValue(key, out var result)) {
            return result;
        }

        return CachedMethodDelegates[key] = type.GetMethodInfo(name, types)?.CreateFastDelegate();
    }

    public static object GetFieldValue(this object obj, string name) {
        return GetFieldValueImpl<object>(obj, obj.GetType(), name);
    }

    public static object GetFieldValue(this Type type, string name) {
        return GetFieldValueImpl<object>(null, type, name);
    }

    public static T GetFieldValue<T>(this object obj, string name) {
        return GetFieldValueImpl<T>(obj, obj.GetType(), name);
    }

    public static T GetFieldValue<T>(this Type type, string name) {
        return GetFieldValueImpl<T>(null, type, name);
    }

    private static T GetFieldValueImpl<T>(object obj, Type type, string name) {
        object result = type.GetFieldGetDelegate<object>(name)?.Invoke(obj);
        if (result == null) {
            return default;
        } else {
            return (T)result;
        }
    }

    public static GetDelegate<TReturn> GetFieldGetDelegate<TReturn>(this Type type, string fieldName) {
        var key = new DelegateKey(type, fieldName, typeof(TReturn));
        if (CachedFieldGetDelegates.TryGetValue(key, out var result)) {
            return (GetDelegate<TReturn>)result;
        }

        return (GetDelegate<TReturn>)(CachedFieldGetDelegates[key] = type.GetFieldInfo(fieldName)?.CreateGetDelegate<TReturn>());
    }

    private static GetDelegate<TReturn> CreateGetDelegate<TReturn>(this FieldInfo field) {
        if (field == null) {
            throw new ArgumentException("Field cannot be null.", nameof(field));
        }

        if (!typeof(TReturn).IsAssignableFrom(field.FieldType)) {
            throw new InvalidCastException($"{field.Name} is of type {field.FieldType}, it cannot be assigned to the type {typeof(TReturn)}.");
        }

        using var method = new DynamicMethodDefinition($"{field} Getter", typeof(TReturn), new[] {typeof(object)});
        var il = method.GetILProcessor();

        if (field.IsStatic) {
            il.Emit(OpCodes.Ldsfld, field);
        } else {
            il.Emit(OpCodes.Ldarg_0);
            if (field.DeclaringType.IsValueType) {
                il.Emit(OpCodes.Unbox_Any, field.DeclaringType);
            }

            il.Emit(OpCodes.Ldfld, field);
        }

        if (field.FieldType.IsValueType && !typeof(TReturn).IsValueType) {
            il.Emit(OpCodes.Box, field.FieldType);
        }

        il.Emit(OpCodes.Ret);

        return (GetDelegate<TReturn>)method.Generate().CreateDelegate(typeof(GetDelegate<TReturn>));
    }

    public static void SetFieldValue(this object obj, string name, object value) {
        SetFieldValueImpl(obj, obj.GetType(), name, value);
    }

    public static void SetFieldValue(this Type type, string name, object value) {
        SetFieldValueImpl(null, type, name, value);
    }

    private static void SetFieldValueImpl(object obj, Type type, string name, object value) {
        GetFieldInfo(type, name)?.SetValue(obj, value);
    }

    public static object GetPropertyValue(this object obj, string name) {
        return GetPropertyValueImpl(obj, obj.GetType(), name);
    }

    public static object GetPropertyValue(this Type type, string name) {
        return GetPropertyValueImpl(null, type, name);
    }

    private static object GetPropertyValueImpl(object obj, Type type, string name) {
        return GetPropertyInfo(type, name)?.GetValue(obj, NoArg);
    }

    public static void SetPropertyValue(this object obj, string name, object value) {
        SetPropertyValueImpl(obj, obj.GetType(), name, value);
    }

    public static void SetPropertyValue(this Type type, string name, object value) {
        SetPropertyValueImpl(null, type, name, value);
    }

    private static void SetPropertyValueImpl(object obj, Type type, string name, object value) {
        GetPropertyInfo(type, name)?.SetValue(obj, value);
    }

    public static object InvokeMethod(this object obj, string name, params object[] parameters) {
        return InvokeMethodImpl(obj, obj.GetType(), name, parameters);
    }

    public static object InvokeMethod(this Type type, string name, params object[] parameters) {
        return InvokeMethodImpl(null, type, name, parameters);
    }

    private static object InvokeMethodImpl(this object obj, Type type, string name, params object[] parameters) {
        parameters ??= NullArg;
        return GetMethodDelegate(type, name)?.Invoke(obj, parameters);
    }

    public static object InvokeOverloadedMethod(this object obj, string name, Type[] types = null, params object[] parameters) {
        return InvokeOverloadedMethodImpl(obj, obj.GetType(), name, types, parameters);
    }

    public static object InvokeOverloadedMethod(this Type type, string name, Type[] types = null, params object[] parameters) {
        return InvokeOverloadedMethodImpl(null, type, name, types, parameters);
    }

    private static object InvokeOverloadedMethodImpl(object obj, Type type, string name, Type[] types = null, params object[] parameters) {
        types ??= EmptyTypes;
        parameters ??= NullArg;
        return GetMethodDelegate(type, name, types)?.Invoke(obj, types, parameters);
    }
}

internal static class HashCodeExtensions {
    public static long GetCustomHashCode<T>(this IEnumerable<T> enumerable) {
        if (enumerable == null) {
            return 0;
        }

        unchecked {
            long hash = 17;
            foreach (T item in enumerable) {
                hash = hash * -1521134295 + EqualityComparer<T>.Default.GetHashCode(item);
            }

            return hash;
        }
    }
}