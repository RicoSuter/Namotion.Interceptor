using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model that references another subject by path through a derived property, like a widget.
/// </summary>
[InterceptorSubject]
public partial class TestPathReference
{
    public required SubjectPathResolver Resolver { get; init; }

    public required string Path { get; init; }

    [Derived]
    public IInterceptorSubject? ResolvedSubject => Resolver.ResolveSubject(Path, PathStyle.Canonical, relativeTo: this);
}
