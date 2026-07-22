using System;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// The runtime tier of the path validation boundary: walks the decomposed <see cref="PathSegment"/>s
/// against a concrete subject graph and resolves the leaf value. Resolution is by property name against
/// the runtime subject, so a shape that was valid at subscribe time may still be unreachable now (a null
/// intermediate, an out-of-range index, a missing key). The walk never throws: any missing property,
/// non-intercepted segment, throwing getter or container operation, out-of-range index, missing key, or a
/// leaf value not assignable to the requested value type resolves the whole path to
/// <see cref="SubjectPathValue{TValue}.Unresolved"/> rather than propagating the failure.
/// </summary>
internal static class PathWalker
{
    /// <summary>
    /// Walks <paramref name="segments"/> from <paramref name="root"/> and returns the leaf value, or
    /// unresolved when any segment is unreachable. <paramref name="resolvedSubjects"/> (length ==
    /// <paramref name="segments"/>.Length) records the subject each segment is read on: index 0 is the
    /// root and each resolved intermediate sets the next entry; entries beyond the resolved prefix are
    /// left null so a caller never observes a stale subject from a prior walk.
    /// </summary>
    internal static SubjectPathValue<TValue> Walk<TValue>(
        PathSegment[] segments, IInterceptorSubject root, IInterceptorSubject?[] resolvedSubjects)
    {
        // Clear any stale subjects from a prior walk up front so an early bail-out below leaves every
        // entry beyond the resolved prefix null without further bookkeeping.
        Array.Clear(resolvedSubjects, 0, resolvedSubjects.Length);
        resolvedSubjects[0] = root;

        // Single never-throw boundary around the whole walk: there is no recover-and-continue path, so
        // any exception from a getter, an accessor build/invoke or a container operation means the leaf is
        // unresolved. The already-resolved prefix stays recorded in resolvedSubjects; nothing past it was
        // written, so it remains null.
        try
        {
            IInterceptorSubject? current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                if (current is null)
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                if (!current.Properties.TryGetValue(segment.PropertyName, out var metadata))
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                if (!(metadata.IsIntercepted || metadata.IsDerived))
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                if (segment.IsLeaf)
                {
                    return ReadLeaf<TValue>(current, metadata);
                }

                var child = ResolveChild(current, metadata, segment);
                if (child is null)
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                resolvedSubjects[i + 1] = child;
                current = child;
            }

            // A well-formed path always ends in a leaf segment and returns above; reaching here means the
            // segments carried no leaf, which the decomposer rejects, so treat it as unresolved.
            return SubjectPathValue<TValue>.Unresolved;
        }
        catch
        {
            return SubjectPathValue<TValue>.Unresolved;
        }
    }

    private static SubjectPathValue<TValue> ReadLeaf<TValue>(IInterceptorSubject current, SubjectPropertyMetadata metadata)
    {
        // Typed path: when the declared property type is assignable to TValue, read through the compiled
        // typed accessor. This reads a value-typed leaf without boxing and delivers a resolved-null for a
        // null reference leaf (the guarded fallback cast below cannot, since a null is not a TValue).
        var propertyInfo = metadata.PropertyInfo;
        if (propertyInfo is not null && typeof(TValue).IsAssignableFrom(propertyInfo.PropertyType))
        {
            var value = PathValueAccessors.GetLeafAccessor<TValue>(current.GetType(), propertyInfo)(current);
            return SubjectPathValue<TValue>.Resolved(value);
        }

        // Fallback: a dynamic property (no PropertyInfo) or one whose declared type is not assignable to
        // TValue. Read the boxed value and accept it only when it is actually a TValue; a mismatch (or a
        // null, which cannot satisfy TValue here) is unresolved.
        if (metadata.GetValue?.Invoke(current) is TValue typedValue)
        {
            return SubjectPathValue<TValue>.Resolved(typedValue);
        }

        return SubjectPathValue<TValue>.Unresolved;
    }

    private static IInterceptorSubject? ResolveChild(
        IInterceptorSubject current, SubjectPropertyMetadata metadata, PathSegment segment)
    {
        switch (segment.Kind)
        {
            case PathSegmentKind.Property:
            {
                return metadata.GetValue?.Invoke(current) as IInterceptorSubject;
            }

            case PathSegmentKind.CollectionIndex:
            {
                if (segment.IsValueTypedCollection)
                {
                    // A value-typed collection segment is a closed ImmutableArray<T>; its single generic
                    // argument is the element type the indexer accessor is compiled against.
                    var elementType = segment.PropertyStaticType.GetGenericArguments()[0];
                    return PathValueAccessors
                        .GetImmutableArrayIndexer(current.GetType(), metadata.PropertyInfo!, elementType)(current, segment.CollectionIndex);
                }

                var collection = metadata.GetValue?.Invoke(current);
                return collection is null ? null : PathValueAccessors.IndexReferenceCollection(collection, segment.CollectionIndex);
            }

            case PathSegmentKind.DictionaryKey:
            {
                return PathValueAccessors
                    .GetDictionaryLookup(current.GetType(), metadata.PropertyInfo!, segment)(current, segment.DictionaryKey!);
            }

            default:
            {
                return null;
            }
        }
    }
}
