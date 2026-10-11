using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>One slot of a type. The accessors are those of the intercepted property, so a write is tracked.</summary>
internal sealed record SlotDefinition(
    string Name,
    Type PropertyType,
    Type ValueType,
    bool IsList,
    bool IsDefault,
    Func<IInterceptorSubject, object?> GetValue,
    Action<IInterceptorSubject, object?> SetValue)
{
    /// <summary>An open slot takes block content as a fragment, any other slot takes an instance of its type.</summary>
    public bool IsOpen => ValueType == typeof(IInterceptorSubject);
}

/// <summary>The slots a type declares with <see cref="SlotAttribute"/>.</summary>
internal sealed class SlotMap
{
    // The name of the static member that the subject generator emits on every subject class.
    private const string DefaultPropertiesName = "DefaultProperties";

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
        var interceptedProperties = FindInterceptedProperties(type);

        SlotMap Invalid(string error) => new(null, new Dictionary<string, SlotDefinition>(), $"{type.Name}: {error}");

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<SlotAttribute>(true);
            if (attribute is null)
            {
                continue;
            }

            var name = attribute.Name ?? property.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return Invalid($"the slot of the property '{property.Name}' needs a name.");
            }

            if (name.Contains('.'))
            {
                return Invalid($"the slot name '{name}' must not contain a dot.");
            }

            if (!MarkdownBlockHead.IsValidSlotName(name))
            {
                return Invalid(
                    $"the slot name '{name}' cannot be written on a page. " +
                    "A slot name starts with a letter or '_' and has only letters, digits and '_'.");
            }

            var isList = property.PropertyType.IsArray;
            var valueType = isList ? property.PropertyType.GetElementType()! : property.PropertyType;
            var isOpen = valueType == typeof(IInterceptorSubject);

            if (!typeof(IInterceptorSubject).IsAssignableFrom(valueType))
            {
                return Invalid($"the slot '{name}' must hold subjects.");
            }

            // A slot that is not open gets an instance of its type per block.
            if (!isOpen && (valueType.IsInterface || valueType.IsAbstract))
            {
                return Invalid($"the type {valueType.Name} of the slot '{name}' cannot be created because it is abstract or an interface.");
            }

            if (named.ContainsKey(name))
            {
                return Invalid($"the slot '{name}' is declared twice.");
            }

            if (attribute.IsDefault)
            {
                if (defaultSlot is not null)
                {
                    return Invalid("there is more than one default slot.");
                }

                if (isList || !isOpen)
                {
                    return Invalid($"the default slot '{name}' must be of type IInterceptorSubject.");
                }
            }

            // Only a write through an intercepted property attaches the slot values to the graph.
            if (interceptedProperties is null ||
                !interceptedProperties.TryGetValue(property.Name, out var metadata) ||
                metadata is not { IsIntercepted: true, GetValue: { } getValue })
            {
                return Invalid($"the slot '{name}' must be a partial property of a subject class.");
            }

            if (metadata.SetValue is not { } setValue)
            {
                return Invalid($"the slot '{name}' needs a setter.");
            }

            var slot = new SlotDefinition(name, property.PropertyType, valueType, isList, attribute.IsDefault, getValue, setValue);
            named.Add(name, slot);
            if (attribute.IsDefault)
            {
                defaultSlot = slot;
            }
        }

        return new SlotMap(defaultSlot, named, null);
    }

    // An instance has the same metadata in IInterceptorSubject.Properties, but the map is needed before
    // an instance exists. A class that derives from a subject class without being one has the member of its base.
    private static IReadOnlyDictionary<string, SubjectPropertyMetadata>? FindInterceptedProperties(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetProperty(DefaultPropertiesName, flags)?.GetValue(null)
                is IReadOnlyDictionary<string, SubjectPropertyMetadata> properties)
            {
                return properties;
            }
        }

        return null;
    }
}
