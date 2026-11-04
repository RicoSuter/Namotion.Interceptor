using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services;

/// <summary>
/// Changes whenever the paths of subjects may have changed. <see cref="SubjectPathResolver"/> reads it on every
/// lookup, so that a component that resolved a path from the cache records a dependency on the paths and renders
/// again when they change.
/// </summary>
[InterceptorSubject]
internal partial class SubjectPathsVersion
{
    public partial long Value { get; set; }
}
