using Namotion.Interceptor.Generator.Tests.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class GeneratorTests
{
    [Fact]
    public void WhenHasFileScopedNamespace_ThenCodeIsGenerated()
    {
        // Arrange & Act
        var person = new PersonWithFileScopedNamespace() as IInterceptorSubject;
        
        // Assert
        Assert.NotNull(person);
    }
}