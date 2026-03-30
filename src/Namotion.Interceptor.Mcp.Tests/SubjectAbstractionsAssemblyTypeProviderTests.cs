using Namotion.Interceptor.Mcp.Implementations;

namespace Namotion.Interceptor.Mcp.Tests;

public class SubjectAbstractionsAssemblyTypeProviderTests
{
    [Fact]
    public void WhenMarkedAssemblyScanned_ThenReturnsInterfaceTypes()
    {
        // Arrange
        var provider = new SubjectAbstractionsAssemblyTypeProvider();

        // Act
        var types = provider.GetTypes().ToList();

        // Assert
        Assert.Contains(types, t => t.Name == typeof(ITestSensor).FullName);
        Assert.All(types, t => Assert.True(t.IsInterface));
    }

    [Fact]
    public void WhenMarkedAssemblyScanned_ThenExcludesNonInterfaceTypes()
    {
        // Arrange
        var provider = new SubjectAbstractionsAssemblyTypeProvider();

        // Act
        var types = provider.GetTypes().ToList();

        // Assert
        Assert.DoesNotContain(types, t => t.Name == typeof(SubjectAbstractionsAssemblyTypeProviderTests).FullName);
    }
}

/// <summary>
/// Test interface in the marked assembly for type provider discovery tests.
/// </summary>
public interface ITestSensor
{
    decimal Temperature { get; }
}
