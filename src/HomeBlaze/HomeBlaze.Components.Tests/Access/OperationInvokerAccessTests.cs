using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Operations;
using HomeBlaze.Components.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

public class OperationInvokerAccessTests
{
    private const string DeniedMessage = "Your roles do not allow running this.";

    [Fact]
    public async Task WhenTheUserMayInvoke_ThenTheOperationRuns()
    {
        // Arrange
        var graph = OperationTestGraph.Create(requiresConfirmation: true);
        var permissions = new TestUserPermissions();

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, graph.InvocationCount);
        Assert.Empty(graph.ShownErrors);
        Assert.Empty(graph.ShownMessages);
    }

    [Fact]
    public async Task WhenTheUserMayNotInvoke_ThenItIsDeniedBeforeTheConfirmationShows()
    {
        // Arrange
        var graph = OperationTestGraph.Create(requiresConfirmation: true);
        var permissions = new TestUserPermissions { CannotInvoke = { graph.Operation } };

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Denied, result.Outcome);
        Assert.False(graph.IsConfirmationShown);
        Assert.Equal(0, graph.InvocationCount);
        AssertRefused(graph);
    }

    [Fact]
    public async Task WhenTheUserLosesExecuteWhileTheConfirmationIsOpen_ThenTheOperationDoesNotRun()
    {
        // Arrange
        var graph = OperationTestGraph.Create(requiresConfirmation: true);
        var permissions = new TestUserPermissions();
        graph.OnConfirmationShown = () => permissions.CannotInvoke.Add(graph.Operation);

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Denied, result.Outcome);
        Assert.True(graph.IsConfirmationShown);
        Assert.Equal(0, graph.InvocationCount);
        AssertRefused(graph);
    }

    [Fact]
    public async Task WhenTheOperationIsExcludedFromUserInterfaces_ThenItIsDeniedAlthoughTheUserMayInvokeIt()
    {
        // Arrange
        var graph = OperationTestGraph.Create(excludedFrom: ExclusionTarget.UserInterface);
        var permissions = new TestUserPermissions();

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Denied, result.Outcome);
        Assert.Equal(0, graph.InvocationCount);
        AssertRefused(graph);
    }

    [Fact]
    public async Task WhenAnOperationRequiresAdministrationAndTheUserMayNotAdministerIt_ThenItIsDenied()
    {
        // Arrange
        var graph = OperationTestGraph.Create(requiresAdministration: true);
        var permissions = new TestUserPermissions { CannotAdminister = { graph.Operation } };

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Denied, result.Outcome);
        Assert.Equal(0, graph.InvocationCount);
        AssertRefused(graph);
    }

    [Fact]
    public async Task WhenTheUserLosesExecuteWhileTheParameterDialogIsOpen_ThenTheOperationDoesNotRun()
    {
        // Arrange
        var graph = OperationTestGraph.Create(hasInputParameter: true);
        var permissions = new TestUserPermissions();
        graph.OnParametersShown = () => permissions.CannotInvoke.Add(graph.Operation);

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Denied, result.Outcome);
        Assert.True(graph.IsParametersShown);
        Assert.Equal(0, graph.InvocationCount);
        AssertRefused(graph);
    }

    [Fact]
    public async Task WhenTheCallerIsGoneAndTheUserMayNotInvoke_ThenNoErrorShows()
    {
        // Arrange
        var graph = OperationTestGraph.Create();
        var permissions = new TestUserPermissions { CannotInvoke = { graph.Operation } };
        using var lifetime = new CancellationTokenSource();
        await lifetime.CancelAsync();

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, permissions, graph.Operation, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, result.Outcome);
        Assert.Equal(0, graph.InvocationCount);
        Assert.Empty(graph.ShownErrors);
        Assert.Empty(graph.ShownMessages);
    }

    // A refusal shows as a "Not allowed" message, not as a failure in the error dialog.
    private static void AssertRefused(OperationTestGraph graph)
    {
        var message = Assert.Single(graph.ShownMessages);
        Assert.Equal("Not allowed", message.Title);
        Assert.Equal(DeniedMessage, message.Message);
        Assert.Empty(graph.ShownErrors);
    }

    [Fact]
    public async Task WhenTheMemberHoldsNoMethod_ThenItIsDenied()
    {
        // Arrange
        var graph = OperationTestGraph.Create(requiresConfirmation: true);
        var subject = new PersistedDevice(InterceptorSubjectContext.Create().WithRegistry());
        var member = subject.TryGetRegisteredSubject()!.AddProperty("Value", typeof(int), _ => 1, null);

        // Act
        var result = await OperationInvoker.InvokeAsync(graph.Dialogs, graph.Services, new TestUserPermissions(), member, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Denied, result.Outcome);
        Assert.False(graph.IsConfirmationShown);
        Assert.Equal(0, graph.InvocationCount);
        Assert.Equal(DeniedMessage, Assert.Single(graph.ShownMessages).Message);
    }
}
