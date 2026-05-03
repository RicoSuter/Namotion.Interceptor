using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class SubjectAbstractionTypeProviderTests
{
    [Fact]
    public void WhenScanned_ThenReturnsMarkedInterfaces()
    {
        // Arrange
        var provider = new SubjectAbstractionTypeProvider(
            [typeof(ITestSubjectAbstraction).Assembly]);

        // Act
        var types = provider.GetTypes().ToList();

        // Assert
        Assert.Contains(types, t => t.Name == typeof(ITestSubjectAbstraction).FullName);
        Assert.All(types, t => Assert.True(t.IsInterface));
    }

    [Fact]
    public void WhenScanned_ThenExcludesUnmarkedInterfaces()
    {
        // Arrange
        var provider = new SubjectAbstractionTypeProvider(
            [typeof(ITestSubjectAbstraction).Assembly]);

        // Act
        var types = provider.GetTypes().ToList();

        // Assert
        Assert.DoesNotContain(types, t => t.Name == typeof(IUnmarkedInterface).FullName);
    }

    [Fact]
    public void WhenInterfaceHasDescription_ThenDescriptionIsPopulated()
    {
        // Arrange
        var provider = new SubjectAbstractionTypeProvider(
            [typeof(ITestSubjectAbstraction).Assembly]);

        // Act
        var types = provider.GetTypes().ToList();
        var testType = types.Single(t => t.Name == typeof(ITestSubjectAbstraction).FullName);

        // Assert
        Assert.Equal("Test subject abstraction for unit tests.", testType.Description);
    }
}

[SubjectAbstraction]
[Description("Test subject abstraction for unit tests.")]
public interface ITestSubjectAbstraction
{
    decimal Temperature { get; }
}

public interface IUnmarkedInterface
{
    string Name { get; }
}
