using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model for [Children] attribute behavior.
/// Has both Child property and Children dictionary for precedence tests.
/// </summary>
[InterceptorSubject]
public partial class TestContainerWithChildren
{
    public partial string? Name { get; set; }

    public partial TestContainerWithChildren? Child { get; set; }

    [Children]
    public partial Dictionary<string, TestContainerWithChildren> Children { get; set; }

    public TestContainerWithChildren()
    {
        Children = new Dictionary<string, TestContainerWithChildren>();
    }
}
