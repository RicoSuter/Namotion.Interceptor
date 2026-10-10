using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

internal sealed record SlotDefinition(string Name, PropertyInfo Property, Type ValueType, bool IsList, bool IsDefault)
{
    /// <summary>An open slot takes block content as a fragment, any other slot takes an instance of its type.</summary>
    public bool IsOpen => ValueType == typeof(IInterceptorSubject);
}

/// <summary>The slots a type declares with <see cref="SlotAttribute"/>.</summary>
internal sealed class SlotMap
{
    private static readonly ConcurrentDictionary<Type, SlotMap> Cache = new();

    private SlotMap(SlotDefinition? defaultSlot, IReadOnlyDictionary<string, SlotDefinition> named, string? error)
    {
        Default = defaultSlot;
        Named = named;
        Error = error;
    }

    public SlotDefinition? Default { get; }

    /// <summary>All slots by block name, including the default slot.</summary>
    public IReadOnlyDictionary<string, SlotDefinition> Named { get; }

    /// <summary>Set when the type declares its slots in a way that cannot be used.</summary>
    public string? Error { get; }

    public static SlotMap For(Type type) => Cache.GetOrAdd(type, Create);

    private static SlotMap Create(Type type)
    {
        var named = new Dictionary<string, SlotDefinition>();
        SlotDefinition? defaultSlot = null;

        SlotMap Invalid(string error) => new(null, new Dictionary<string, SlotDefinition>(), $"{type.Name}: {error}");

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<SlotAttribute>(true);
            if (attribute is null)
            {
                continue;
            }

            var name = attribute.Name ?? property.Name;
            if (name.Contains('.'))
            {
                return Invalid($"the slot name '{name}' must not contain a dot.");
            }

            var isList = property.PropertyType.IsArray;
            var valueType = isList ? property.PropertyType.GetElementType()! : property.PropertyType;
            var slot = new SlotDefinition(name, property, valueType, isList, attribute.IsDefault);

            if (!typeof(IInterceptorSubject).IsAssignableFrom(valueType))
            {
                return Invalid($"the slot '{name}' must hold subjects.");
            }

            if (!named.TryAdd(name, slot))
            {
                return Invalid($"the slot '{name}' is declared twice.");
            }

            if (attribute.IsDefault)
            {
                if (defaultSlot is not null)
                {
                    return Invalid("there is more than one default slot.");
                }

                if (isList || !slot.IsOpen)
                {
                    return Invalid($"the default slot '{name}' must be of type IInterceptorSubject.");
                }

                defaultSlot = slot;
            }
        }

        return new SlotMap(defaultSlot, named, null);
    }
}
