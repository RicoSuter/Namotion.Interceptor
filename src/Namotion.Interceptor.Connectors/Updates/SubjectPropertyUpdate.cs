using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Connectors.Updates.Collections;
using Namotion.Interceptor.Connectors.Updates.Items;
using Namotion.Interceptor.Connectors.Updates.Values;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Updates;

public class SubjectPropertyUpdate
{
    /// <summary>
    /// Gets the kind of entity which is updated.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonInclude]
    public SubjectPropertyUpdateKind Kind { get; internal set; }

    /// <summary>
    /// Gets or sets the value of the property update if kind is Value.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Gets the timestamp of the property update or null if unknown.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset? Timestamp { get; private set; }

    /// <summary>
    /// Gets the item if kind is Item.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonInclude]
    public SubjectUpdate? Item { get; internal set; }

    /// <summary>
    /// Structural operations (Remove, Insert, Move) to apply in order.
    /// Applied BEFORE property updates in Collection.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonInclude]
    public List<SubjectCollectionOperation>? Operations { get; internal set; }

    /// <summary>
    /// Sparse property updates by FINAL index/key (after Operations applied).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonInclude]
    public IReadOnlyCollection<SubjectPropertyCollectionUpdate>? Collection { get; internal set; }

    /// <summary>
    /// Total count of the collection/dictionary after all operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonInclude]
    public int? Count { get; internal set; }

    /// <summary>
    /// Gets the updated attributes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonInclude]
    public Dictionary<string, SubjectPropertyUpdate>? Attributes { get; internal set; }

    /// <summary>
    /// Gets or sets custom extension data added by the transformPropertyUpdate function.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }

    public static SubjectPropertyUpdate Create<T>(T value, DateTimeOffset? timestamp = null)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Value,
            Value = value,
            Timestamp = timestamp
        };
    }

    public static SubjectPropertyUpdate Create(SubjectUpdate itemUpdate, DateTimeOffset? timestamp = null)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Item,
            Item = itemUpdate,
            Timestamp = timestamp
        };
    }

    public static SubjectPropertyUpdate Create(params IReadOnlyList<SubjectPropertyCollectionUpdate> collectionUpdates)
    {
        return new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Collection,
            Collection = collectionUpdates
        };
    }

    internal static SubjectPropertyUpdate CreateCompleteUpdate(RegisteredSubjectProperty property,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        var propertyUpdate = new SubjectPropertyUpdate
        {
            Attributes = CreateAttributeUpdates(property, processors, knownSubjectUpdates, propertyUpdates, currentPath)
        };

        propertyUpdate.ApplyValue(
            property, property.Reference.TryGetWriteTimestamp(), property.GetValue(),
            processors, knownSubjectUpdates, propertyUpdates, currentPath);

        return propertyUpdate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<string, SubjectPropertyUpdate>? CreateAttributeUpdates(RegisteredSubjectProperty property,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        Dictionary<string, SubjectPropertyUpdate>? attributes = null;
        foreach (var attribute in property.Attributes)
        {
            if (attribute.HasGetter)
            {
                attributes ??= new Dictionary<string, SubjectPropertyUpdate>();

                var attributeUpdate = CreateCompleteUpdate(attribute, processors, knownSubjectUpdates, propertyUpdates, currentPath);
                attributes[attribute.AttributeMetadata.AttributeName] = attributeUpdate;

                propertyUpdates?.TryAdd(attributeUpdate, new SubjectPropertyUpdateReference(attribute, attributes));
            }
        }

        return attributes?.Count > 0 ? attributes : null;
    }

    /// <summary>
    /// Adds a complete update of the given value to the property update.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="timestamp">The timestamp of the value change.</param>
    /// <param name="value">The value to apply.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">Optional list to collect property updates for transformation.</param>
    /// <param name="currentPath">The set of subjects in the current traversal path (for cycle detection).</param>
    internal void ApplyValue(RegisteredSubjectProperty property, DateTimeOffset? timestamp, object? value,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates = null,
        HashSet<IInterceptorSubject>? currentPath = null)
    {
        Timestamp = timestamp;

        // Ensure currentPath is available for cycle detection
        currentPath ??= new HashSet<IInterceptorSubject>();

        if (property.IsSubjectDictionary)
        {
            SubjectDictionaryUpdateLogic.ApplyToUpdate(
                this, value as IDictionary, processors, knownSubjectUpdates, propertyUpdates, currentPath);
        }
        else if (property.IsSubjectCollection)
        {
            SubjectCollectionUpdateLogic.ApplyToUpdate(
                this, value as IEnumerable<IInterceptorSubject>, processors, knownSubjectUpdates, propertyUpdates, currentPath);
        }
        else if (property.IsSubjectReference)
        {
            SubjectItemUpdateLogic.ApplyItemToUpdate(
                this, value as IInterceptorSubject, processors, knownSubjectUpdates, propertyUpdates, currentPath);
        }
        else
        {
            SubjectValueUpdateLogic.ApplyValueToUpdate(this, value);
        }
    }

    /// <summary>
    /// Applies a diff update for collection/dictionary properties, comparing old and new values.
    /// Produces structural Operations (Remove, Insert, Move) and sparse Collection updates separately.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="timestamp">The timestamp of the value change.</param>
    /// <param name="oldValue">The old collection/dictionary value.</param>
    /// <param name="newValue">The new collection/dictionary value.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <param name="knownSubjectUpdates">The known subject updates.</param>
    /// <param name="propertyUpdates">Optional list to collect property updates for transformation.</param>
    /// <param name="currentPath">The set of subjects in the current traversal path (for cycle detection).</param>
    internal void ApplyValueWithDiff(RegisteredSubjectProperty property, DateTimeOffset? timestamp,
        object? oldValue, object? newValue,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        Timestamp = timestamp;

        if (property.IsSubjectDictionary)
        {
            SubjectDictionaryUpdateLogic.ApplyDiffToUpdate(
                this, oldValue, newValue, processors, knownSubjectUpdates, propertyUpdates, currentPath);
        }
        else if (property.IsSubjectCollection)
        {
            SubjectCollectionUpdateLogic.ApplyDiffToUpdate(
                this, oldValue, newValue, processors, knownSubjectUpdates, propertyUpdates, currentPath);
        }
        else
        {
            // For non-collection properties, fall back to regular ApplyValue
            ApplyValue(property, timestamp, newValue, processors, knownSubjectUpdates, propertyUpdates, currentPath);
        }
    }
}
