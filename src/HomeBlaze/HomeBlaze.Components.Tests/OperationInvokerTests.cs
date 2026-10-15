using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Operations;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class OperationInvokerTests
{
    private static readonly IServiceProvider ServiceProvider = Mock.Of<IServiceProvider>();

    private static MethodParameter InputParameter(string name, Type type) => new() { Name = name, Type = type };

    private static void SetupDialog<TDialog>(Mock<IDialogService> dialogService, Func<DialogResult?> getResult)
        where TDialog : IComponent
    {
        dialogService
            .Setup(service => service.ShowAsync<TDialog>(It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                var reference = new Mock<IDialogReference>();
                reference.Setup(dialogReference => dialogReference.Result).Returns(Task.FromResult(getResult()));
                return reference.Object;
            });
    }

    private static void VerifyShown<TDialog>(Mock<IDialogService> dialogService, Times times)
        where TDialog : IComponent
    {
        dialogService.Verify(
            service => service.ShowAsync<TDialog>(It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()),
            times);
    }

    [Fact]
    public void WhenAParameterRequiresInput_ThenParametersArePrompted()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            RequiresConfirmation = true,
            Parameters = [InputParameter("speed", typeof(int))]
        };

        // Act
        var prompt = OperationInvoker.GetPrompt(method);

        // Assert
        Assert.Equal(OperationPrompt.Parameters, prompt);
    }

    [Fact]
    public void WhenOnlyRuntimeAndServiceParametersExistAndConfirmationIsRequired_ThenConfirmationIsPrompted()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            RequiresConfirmation = true,
            Parameters =
            [
                new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true },
                new MethodParameter { Name = "services", Type = typeof(IServiceProvider), IsFromServices = true }
            ]
        };

        // Act
        var prompt = OperationInvoker.GetPrompt(method);

        // Assert
        Assert.Equal(OperationPrompt.Confirmation, prompt);
    }

    [Fact]
    public void WhenNothingIsRequired_ThenNothingIsPrompted()
    {
        // Arrange
        var method = new MethodMetadata(_ => null);

        // Act
        var prompt = OperationInvoker.GetPrompt(method);

        // Assert
        Assert.Equal(OperationPrompt.None, prompt);
    }

    [Fact]
    public async Task WhenParametersAreEntered_ThenMethodIsInvokedWithThem()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, () => DialogResult.Ok(new object?[] { 5 }));
        object?[]? receivedArguments = null;
        var method = new MethodMetadata(arguments => { receivedArguments = arguments; return null; })
        {
            Title = "Set Speed",
            RequiresConfirmation = true,
            Parameters = [InputParameter("speed", typeof(int))]
        };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, outcome);
        Assert.Equal(new object?[] { 5 }, receivedArguments);
        VerifyShown<MethodConfirmationDialog>(dialogService, Times.Never());
        VerifyShown<MethodResultDialog>(dialogService, Times.Never());
    }

    [Fact]
    public async Task WhenParameterDialogIsCancelled_ThenMethodIsNotInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, DialogResult.Cancel);
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; })
        {
            Parameters = [InputParameter("speed", typeof(int))]
        };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Cancelled, outcome);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenConfirmationIsCancelled_ThenMethodIsNotInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodConfirmationDialog>(dialogService, DialogResult.Cancel);
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; }) { RequiresConfirmation = true };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Cancelled, outcome);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenConfirmed_ThenMethodIsInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodConfirmationDialog>(dialogService, () => DialogResult.Ok(true));
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; }) { RequiresConfirmation = true };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, outcome);
        Assert.True(isInvoked);
        VerifyShown<MethodConfirmationDialog>(dialogService, Times.Once());
    }

    [Fact]
    public async Task WhenNothingIsPrompted_ThenMethodIsInvokedWithoutDialog()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; });

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, outcome);
        Assert.True(isInvoked);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenQueryReturnsAResult_ThenResultDialogIsShown()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var method = new MethodMetadata(_ => "42") { Kind = MethodKind.Query, Title = "Get Answer" };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, outcome);
        VerifyShown<MethodResultDialog>(dialogService, Times.Once());
    }

    [Fact]
    public async Task WhenMethodThrows_ThenErrorDialogIsShownAndFailedIsReturned()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var method = new MethodMetadata(_ => throw new InvalidOperationException("Device offline")) { Title = "Turn On" };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Failed, outcome);
        VerifyShown<MethodErrorDialog>(dialogService, Times.Once());
    }

    [Fact]
    public async Task WhenMethodIsCancelled_ThenNoDialogIsShownAndCancelledIsReturned()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var method = new MethodMetadata(_ => throw new OperationCanceledException());

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Cancelled, outcome);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenMethodTakesACancellationToken_ThenItIsNotTheLifetime()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        CancellationToken receivedToken = default;
        var method = new MethodMetadata(arguments => { receivedToken = (CancellationToken)arguments![0]!; return null; })
        {
            Parameters = [new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true }]
        };

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.False(receivedToken.CanBeCanceled);
    }

    [Fact]
    public async Task WhenLifetimeIsCancelledBeforeStart_ThenNoDialogIsShownAndMethodIsNotInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; }) { RequiresConfirmation = true };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Cancelled, outcome);
        Assert.False(isInvoked);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenLifetimeIsCancelledWhileConfirmationIsOpen_ThenMethodIsNotInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        SetupDialog<MethodConfirmationDialog>(dialogService, () =>
        {
            lifetime.Cancel();
            return DialogResult.Ok(true);
        });
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; }) { RequiresConfirmation = true };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Cancelled, outcome);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenLifetimeIsCancelledWhileMethodRuns_ThenNoResultDialogIsShown()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        var method = new MethodMetadata(_ =>
        {
            lifetime.Cancel();
            return "42";
        })
        { Kind = MethodKind.Query };

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, outcome);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenLifetimeIsCancelledWhileMethodFails_ThenNoErrorDialogIsShown()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        var method = new MethodMetadata(_ =>
        {
            lifetime.Cancel();
            throw new InvalidOperationException("Device offline");
        });

        // Act
        var outcome = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Failed, outcome);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenResultDialogThrows_ThenTheExceptionPropagates()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        dialogService
            .Setup(service => service.ShowAsync<MethodResultDialog>(It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .ThrowsAsync(new InvalidOperationException("Dialog failed"));
        var invocationCount = 0;
        var method = new MethodMetadata(_ =>
        {
            invocationCount++;
            return "42";
        })
        { Kind = MethodKind.Query, Title = "Get Answer" };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None));
        Assert.Equal(1, invocationCount);
        VerifyShown<MethodErrorDialog>(dialogService, Times.Never());
    }
}
