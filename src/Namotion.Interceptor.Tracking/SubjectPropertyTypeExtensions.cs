using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Compatibility extensions for determining whether a property type can contain interceptor subjects.
/// The Core classifier owns the cached structural classification.
/// </summary>
public static class SubjectPropertyTypeExtensions
{
    /// <summary>
    /// Checks whether a property can contain subjects, using both a JIT-constant fast path
    /// via <typeparamref name="TProperty"/> and a runtime fallback via the declared
    /// <paramref name="type"/>. <typeparamref name="TProperty"/> is a hint: it may be
    /// <c>object</c> when values are boxed through non-generic paths (this applies to
    /// <c>TProperty</c> throughout the interceptor interfaces, not just here).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects<TProperty>(this Type type)
    {
        if (typeof(TProperty).IsPrimitive ||
            typeof(TProperty) == typeof(decimal) ||
            typeof(TProperty) == typeof(string) ||
            typeof(TProperty) == typeof(DateTime) ||
            typeof(TProperty) == typeof(DateTimeOffset) ||
            typeof(TProperty) == typeof(TimeSpan) ||
            typeof(TProperty) == typeof(Guid))
        {
            return false;
        }

        return SubjectPropertyTypeClassifier.CanContainSubjects(type);
    }

    /// <summary>
    /// Returns true if the given type could potentially contain or be an <see cref="IInterceptorSubject"/>.
    /// Results are cached per type for O(1) lookups after the first call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanContainSubjects(this Type type)
    {
        return SubjectPropertyTypeClassifier.CanContainSubjects(type);
    }

    /// <summary>
    /// Returns true if the given type is a single subject reference: an <see cref="IInterceptorSubject"/>,
    /// <see cref="object"/>, or a plain interface that could carry a subject. Generic interfaces over
    /// non-subject content (e.g. <c>IList&lt;int&gt;</c>) and enumerable subjects are excluded.
    /// Mutually exclusive with <see cref="IsSubjectCollectionType"/> and <see cref="IsSubjectDictionaryType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectReferenceType(this Type type)
    {
        return SubjectPropertyTypeClassifier.IsSubjectReferenceType(type);
    }

    /// <summary>
    /// Returns true if the given type is a collection of subject references.
    /// Mutually exclusive with <see cref="IsSubjectReferenceType"/> and <see cref="IsSubjectDictionaryType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectCollectionType(this Type type)
    {
        return SubjectPropertyTypeClassifier.IsSubjectCollectionType(type);
    }

    /// <summary>
    /// Returns true if the given type is a dictionary with subject reference values.
    /// Mutually exclusive with <see cref="IsSubjectReferenceType"/> and <see cref="IsSubjectCollectionType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubjectDictionaryType(this Type type)
    {
        return SubjectPropertyTypeClassifier.IsSubjectDictionaryType(type);
    }
}
