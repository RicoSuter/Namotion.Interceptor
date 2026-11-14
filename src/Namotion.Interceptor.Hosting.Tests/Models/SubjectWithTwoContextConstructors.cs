using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject with the generated (IInterceptorSubjectContext) constructor beside a declared one that also
/// takes a dependency, so two constructors accept the context.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithTwoContextConstructors
{
    public SubjectWithTwoContextConstructors()
    {
    }

    public SubjectWithTwoContextConstructors(IInterceptorSubjectContext context, ILogger<SubjectWithTwoContextConstructors> logger)
        : this(context)
    {
        Logger = logger;
    }

    public ILogger<SubjectWithTwoContextConstructors>? Logger { get; }

    public partial string? Name { get; set; }
}
