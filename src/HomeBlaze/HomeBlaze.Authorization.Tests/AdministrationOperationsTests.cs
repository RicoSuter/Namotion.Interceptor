using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class AdministrationOperationsTests
{
    public static TheoryData<Type, string> Operations => new()
    {
        { typeof(HomeBlaze.Storage.FluentStorageContainer), "CreateAsync" },
        { typeof(HomeBlaze.Storage.FluentStorageContainer), "CreateMarkdownFileAsync" },
        { typeof(HomeBlaze.Storage.VirtualFolder), "CreateAsync" },
        { typeof(HomeBlaze.Storage.VirtualFolder), "CreateMarkdownFileAsync" },
        { typeof(HomeBlaze.AI.Agents.LlmAgentBase), "RunNow" },
        { typeof(HomeBlaze.Plugins.PluginManager), "AddPlugin" },
        { typeof(HomeBlaze.Plugins.Plugin), "RemovePlugin" },
        { typeof(HomeBlaze.Installation.HomeBlazeClient), "RenameAsync" },
        { typeof(HomeBlaze.Installation.HomeBlazeClient), "ForgetAsync" },
        { typeof(User), "CreateKeyAsync" },
        { typeof(User), "RevokeKeyAsync" },
        { typeof(Authorization), "AddRoleAsync" },
        { typeof(Role), "RenameAsync" },
        { typeof(Role), "DeleteAsync" }
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void WhenAnOperationChangesWhatTheApplicationIs_ThenItRequiresAdministration(Type type, string methodName)
    {
        // Arrange
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.Name == methodName && candidate.GetCustomAttribute<OperationAttribute>() is not null);

        // Act
        var attribute = method.GetCustomAttribute<OperationAttribute>()!;

        // Assert
        Assert.True(attribute.RequiresAdministration);
    }

    [Theory]
    [InlineData("ReplaceContentAsync")]
    [InlineData("ReplaceTextAsync")]
    public void WhenAnOperationRewritesTheTextOfAPage_ThenNoUserInterfaceRunsIt(string methodName)
    {
        // Arrange
        var method = typeof(HomeBlaze.Storage.Files.MarkdownFile).GetMethod(methodName)!;

        // Act
        var targets = method.GetCustomAttribute<ExcludeFromAttribute>()?.Targets ?? ExclusionTarget.None;

        // Assert
        Assert.True(targets.HasFlag(ExclusionTarget.UserInterface));
        Assert.False(targets.HasFlag(ExclusionTarget.Agents));
    }
}
