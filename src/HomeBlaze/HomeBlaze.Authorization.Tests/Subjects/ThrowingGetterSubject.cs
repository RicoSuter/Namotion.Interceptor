using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authorization.Tests.Subjects;

/// <summary>
/// A subject with a member whose getter throws, which only a reader resolving a path through it calls.
/// </summary>
[InterceptorSubject]
public partial class ThrowingGetterSubject
{
    public partial string? Plain { get; set; }

    public string Broken => throw new NotSupportedException("The getter fails.");
}
