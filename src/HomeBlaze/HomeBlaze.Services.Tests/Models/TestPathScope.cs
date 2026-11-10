using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>A scope that knows its <see cref="Block"/> as <c>block</c>.</summary>
[InterceptorSubject]
public partial class TestPathScope : IPathScope
{
    public partial TestContainer? Block { get; set; }

    public IInterceptorSubject? TryGetNamedSubject(string name) => name == "block" ? Block : null;
}

/// <summary>A root that holds a scope and, beside it, a subject outside of the scope.</summary>
[InterceptorSubject]
public partial class TestPathScopeRoot
{
    public partial TestContainer? Outside { get; set; }

    public partial TestPathScope? Scope { get; set; }
}
