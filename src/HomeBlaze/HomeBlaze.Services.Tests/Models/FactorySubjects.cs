using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

[InterceptorSubject]
public partial class FactoryContextSubject
{
    public partial string? Label { get; set; }
}

/// <summary>
/// Has no parameterless constructor, so no context constructor is generated and the factory creates it detached.
/// </summary>
[InterceptorSubject]
public partial class FactoryDependencySubject
{
    public FactoryDependencySubject(TypeProvider typeProvider)
    {
        TypeProvider = typeProvider;
    }

    public TypeProvider TypeProvider { get; }

    public partial string? Label { get; set; }
}
