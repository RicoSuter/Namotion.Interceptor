using HomeBlaze.Services;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests;

public class SubjectTypeRegistryTests
{
    [Fact]
    public void WhenAnAbstractSubjectTypeIsProvided_ThenItIsNotRegistered()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(AbstractTestSubject), typeof(ConcreteTestSubject)]);
        var registry = new SubjectTypeRegistry(typeProvider);

        // Act
        var types = registry.RegisteredTypes;

        // Assert
        Assert.Contains(typeof(ConcreteTestSubject), types);
        Assert.DoesNotContain(typeof(AbstractTestSubject), types);
        Assert.Null(registry.ResolveType(typeof(AbstractTestSubject).FullName!));
    }
}

[InterceptorSubject]
public abstract partial class AbstractTestSubject
{
    public partial string? Name { get; set; }
}

[InterceptorSubject]
public partial class ConcreteTestSubject : AbstractTestSubject
{
    public partial int Count { get; set; }
}
