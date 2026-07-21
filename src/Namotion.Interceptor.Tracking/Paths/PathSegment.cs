using System;

namespace Namotion.Interceptor.Tracking.Paths;

internal enum PathSegmentKind
{
    Property,
    CollectionIndex,
    DictionaryKey
}

/// <summary>
/// One node of a decomposed path: a single subscribed property name plus an optional fixed
/// collection index or dictionary key evaluated once at subscribe time. Resolution is by name
/// against the runtime subject; the static-type fields drive accessor construction and validation only.
/// </summary>
internal sealed class PathSegment
{
    public required string PropertyName { get; init; }
    public required PathSegmentKind Kind { get; init; }
    public required Type PropertyStaticType { get; init; }
    public bool IsLeaf { get; init; }

    // Valid when Kind == CollectionIndex.
    public int CollectionIndex { get; init; }
    // True only when the collection segment's static type is ImmutableArray<T> (a value type that
    // would box through the object-returning metadata getter).
    public bool IsValueTypedCollection { get; init; }

    // Valid when Kind == DictionaryKey.
    public object? DictionaryKey { get; init; }
    public Type? DictionaryInterfaceType { get; init; }
    public Type? DictionaryKeyType { get; init; }
    public Type? DictionaryValueType { get; init; }
}
