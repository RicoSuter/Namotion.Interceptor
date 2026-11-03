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
    public void WhenOperationHasNoIcon_ThenTheIconNameIsPlayArrow()
    {
        // Arrange
        var method = new MethodMetadata(_ => null) { Kind = MethodKind.Operation };

        // Act
        var iconName = OperationInvoker.GetIconName(method);

        // Assert
        Assert.Equal("PlayArrow", iconName);
    }

    [Fact]
    public void WhenQueryHasNoIcon_ThenTheIconNameIsSearch()
    {
        // Arrange
        var method = new MethodMetadata(_ => null) { Kind = MethodKind.Query };

        // Act
        var iconName = OperationInvoker.GetIconName(method);

        // Assert
        Assert.Equal("Search", iconName);
    }

    [Fact]
    public void WhenMethodHasAnIcon_ThenTheIconNameIsItsOwnIcon()
    {
        // Arrange
        var method = new MethodMetadata(_ => null) { Kind = MethodKind.Operation, Icon = "Stop" };

        // Act
        var iconName = OperationInvoker.GetIconName(method);

        // Assert
        Assert.Equal("Stop", iconName);
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(new object?[] { 5 }, receivedArguments);
        VerifyShown<MethodConfirmationDialog>(dialogService, Times.Never());
        VerifyShown<MethodResultDialog>(dialogService, Times.Never());
    }

    [Fact]
    public async Task WhenConfirmedMethodReturnsAResult_ThenItIsReportedRunningOnlyWhileItRuns()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var isRunning = false;
        var states = new List<string>();
        SetupDialog<MethodConfirmationDialog>(dialogService, () => { states.Add($"confirmation:{isRunning}"); return DialogResult.Ok(true); });
        SetupDialog<MethodResultDialog>(dialogService, () => { states.Add($"result:{isRunning}"); return DialogResult.Ok(true); });
        var method = new MethodMetadata(_ => { states.Add($"method:{isRunning}"); return 42; }) { RequiresConfirmation = true };

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None,
            running => isRunning = running);

        // Assert
        Assert.Equal(["confirmation:False", "method:True", "result:False"], states);
    }

    [Fact]
    public async Task WhenMethodThrows_ThenItIsNoLongerReportedRunningWhenItsErrorShows()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var reports = new List<bool>();
        bool? isRunningAtError = null;
        SetupDialog<MethodErrorDialog>(dialogService, () => { isRunningAtError = reports[^1]; return DialogResult.Ok(true); });
        var method = new MethodMetadata(_ => throw new InvalidOperationException("Failed"));

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None, reports.Add);

        // Assert
        Assert.Equal([true, false], reports);
        Assert.False(isRunningAtError);
    }

    [Fact]
    public async Task WhenConfirmationIsCanceled_ThenNoRunningIsReported()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodConfirmationDialog>(dialogService, DialogResult.Cancel);
        var reports = new List<bool>();
        var method = new MethodMetadata(_ => null) { RequiresConfirmation = true };

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None, reports.Add);

        // Assert
        Assert.Empty(reports);
    }

    [Fact]
    public async Task WhenParameterDialogIsCanceled_ThenMethodIsNotInvoked()
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, result.Outcome);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenConfirmationIsCanceled_ThenMethodIsNotInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodConfirmationDialog>(dialogService, DialogResult.Cancel);
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; }) { RequiresConfirmation = true };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, result.Outcome);
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.Equal("42", result.Value);
        VerifyShown<MethodResultDialog>(dialogService, Times.Once());
    }

    [Fact]
    public async Task WhenMethodThrows_ThenErrorDialogIsShownAndFailedIsReturned()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        var method = new MethodMetadata(_ => throw new InvalidOperationException("Device offline")) { Title = "Turn On" };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        VerifyShown<MethodErrorDialog>(dialogService, Times.Once());
    }

    [Fact]
    public async Task WhenMethodThrowsOperationCanceledException_ThenErrorDialogIsShownAndFailedIsReturned()
    {
        // Arrange: an HttpClient reports its timeout as a TaskCanceledException.
        var dialogService = new Mock<IDialogService>();
        var method = new MethodMetadata(_ => throw new TaskCanceledException("The request timed out.")) { Title = "Turn On" };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        VerifyShown<MethodErrorDialog>(dialogService, Times.Once());
    }

    [Fact]
    public async Task WhenLifetimeIsCanceledWhileMethodIsCanceled_ThenNoErrorDialogIsShown()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        var method = new MethodMetadata(_ =>
        {
            lifetime.Cancel();
            throw new OperationCanceledException();
        });

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
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
    public async Task WhenLifetimeIsCanceledBeforeStart_ThenNoDialogIsShownAndMethodIsNotInvoked()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var isInvoked = false;
        var method = new MethodMetadata(_ => { isInvoked = true; return null; }) { RequiresConfirmation = true };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, result.Outcome);
        Assert.False(isInvoked);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenLifetimeIsCanceledWhileConfirmationIsOpen_ThenMethodIsNotInvoked()
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, result.Outcome);
        Assert.False(isInvoked);
    }

    [Fact]
    public async Task WhenLifetimeIsCanceledWhileMethodRuns_ThenNoResultDialogIsShown()
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenLifetimeIsCanceledWhileMethodFails_ThenNoErrorDialogIsShown()
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
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, lifetime.Token);

        // Assert
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
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

    [Fact]
    public async Task WhenInitialValuesAreGiven_ThenTheParameterDialogGetsThem()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        DialogParameters? shownParameters = null;
        dialogService
            .Setup(service => service.ShowAsync<MethodParameterDialog>(It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .Callback<string, DialogParameters, DialogOptions>((_, parameters, _) => shownParameters = parameters)
            .ReturnsAsync(() =>
            {
                var reference = new Mock<IDialogReference>();
                reference.Setup(dialogReference => dialogReference.Result).Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
                return reference.Object;
            });
        var method = new MethodMetadata(_ => null) { Title = "Set Speed", Parameters = [InputParameter("speed", typeof(int))] };
        var initialValues = new Dictionary<string, object?> { ["speed"] = 5 };

        // Act
        await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None, initialValues: initialValues);

        // Assert
        Assert.NotNull(shownParameters);
        Assert.Same(initialValues, shownParameters.Get<IReadOnlyDictionary<string, object?>>(nameof(MethodParameterDialog.InitialValues)));
    }

    [Fact]
    public async Task WhenParametersAreEntered_ThenTheyAreReturnedByName()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, () => DialogResult.Ok(new object?[] { 5, "fast" }));
        var method = new MethodMetadata(_ => null)
        {
            Title = "Set Speed",
            Parameters =
            [
                InputParameter("speed", typeof(int)),
                new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true },
                InputParameter("mode", typeof(string))
            ]
        };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Parameters);
        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal(5, result.Parameters["speed"]);
        Assert.Equal("fast", result.Parameters["mode"]);
    }

    [Fact]
    public async Task WhenMethodWithEnteredParametersThrows_ThenParametersAreStillReturned()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, () => DialogResult.Ok(new object?[] { 5 }));
        var method = new MethodMetadata(_ => throw new InvalidOperationException("Device offline"))
        {
            Title = "Set Speed",
            Parameters = [InputParameter("speed", typeof(int))]
        };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Parameters);
        Assert.Equal(5, result.Parameters["speed"]);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task WhenTheParameterDialogIsCanceled_ThenNoParametersAreReturned()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>();
        SetupDialog<MethodParameterDialog>(dialogService, DialogResult.Cancel);
        var method = new MethodMetadata(_ => null) { Title = "Set Speed", Parameters = [InputParameter("speed", typeof(int))] };

        // Act
        var result = await OperationInvoker.InvokeAsync(dialogService.Object, ServiceProvider, method, CancellationToken.None);

        // Assert
        Assert.Equal(OperationOutcome.Canceled, result.Outcome);
        Assert.Null(result.Parameters);
    }
}
