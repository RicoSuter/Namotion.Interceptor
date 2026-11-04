using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace HomeBlaze.Plugins.Tests;

public class PluginManagerOperationTests
{
    [Fact]
    public void WhenAddingAPlugin_ThenTheOperationRequiresConfirmation()
    {
        // Act
        var attribute = typeof(PluginManager).GetMethod(nameof(PluginManager.AddPlugin))!.GetCustomAttribute<OperationAttribute>();

        // Assert
        Assert.NotNull(attribute);
        Assert.True(attribute.RequiresConfirmation);
    }

    [Fact]
    public void WhenRemovingAPlugin_ThenTheOperationIsTheDeleteOperationAndRequiresConfirmation()
    {
        // Act
        var attribute = typeof(Plugin).GetMethod(nameof(Plugin.RemovePlugin))!.GetCustomAttribute<OperationAttribute>();

        // Assert
        Assert.NotNull(attribute);
        Assert.Equal(OperationPurpose.Delete, attribute.Purpose);
        Assert.True(attribute.RequiresConfirmation);
    }
}
