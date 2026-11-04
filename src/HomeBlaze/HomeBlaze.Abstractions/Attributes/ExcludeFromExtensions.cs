using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Checks of <see cref="ExcludeFromAttribute"/> on subjects.
/// </summary>
public static class ExcludeFromExtensions
{
    /// <summary>
    /// Gets whether the type of the subject, or a type it derives from, is excluded from any of the given targets.
    /// </summary>
    public static bool IsExcludedFrom(this IInterceptorSubject subject, ExclusionTarget target)
        => ExcludeFromAttribute.IsExcluded(subject.GetType(), target);
}
