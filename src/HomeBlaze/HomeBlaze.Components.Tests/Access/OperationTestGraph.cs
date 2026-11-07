using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Tests.Models;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// An operation registered as the member of a subject, and a dialog service whose prompts are accepted and which records
/// the errors and message boxes it shows.
/// </summary>
public sealed class OperationTestGraph
{
    private readonly Mock<IDialogService> _dialogs = new();
    private int _invocationCount;

    private OperationTestGraph(bool requiresConfirmation, bool requiresAdministration, bool hasInputParameter, ExclusionTarget excludedFrom)
    {
        Method = new MethodMetadata(_ =>
        {
            Interlocked.Increment(ref _invocationCount);
            return null;
        })
        {
            Title = "Run",
            Kind = MethodKind.Operation,
            RequiresConfirmation = requiresConfirmation,
            RequiresAdministration = requiresAdministration,
            ExcludedFrom = excludedFrom,
            Parameters = hasInputParameter ? [new MethodParameter { Name = "speed", Type = typeof(int) }] : []
        };
        Operation = CreateMember(Method);

        Setup<MethodConfirmationDialog>(_ =>
        {
            IsConfirmationShown = true;
            OnConfirmationShown?.Invoke();
            return DialogResult.Ok(true);
        });
        Setup<MethodParameterDialog>(_ =>
        {
            IsParametersShown = true;
            OnParametersShown?.Invoke();
            return DialogResult.Ok(new object?[] { 5 });
        });
        _dialogs
            .Setup(service => service.ShowMessageBoxAsync(It.IsAny<MessageBoxOptions>(), It.IsAny<DialogOptions?>()))
            .ReturnsAsync((MessageBoxOptions options, DialogOptions? _) =>
            {
                ShownMessages.Add(options);
                return true;
            });
        Setup<MethodErrorDialog>(parameters =>
        {
            ShownErrors.Add(parameters.Get<Exception>(nameof(MethodErrorDialog.Exception))!);
            return DialogResult.Ok(true);
        });
    }

    public IDialogService Dialogs => _dialogs.Object;

    public IServiceProvider Services { get; } = Mock.Of<IServiceProvider>();

    /// <summary>The member that holds <see cref="Method"/>.</summary>
    public RegisteredSubjectProperty Operation { get; }

    public MethodMetadata Method { get; }

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    /// <summary>Runs while the confirmation is open, before it is accepted.</summary>
    public Action? OnConfirmationShown { get; set; }

    public bool IsConfirmationShown { get; private set; }

    /// <summary>Runs while the parameter dialog is open, before it is accepted.</summary>
    public Action? OnParametersShown { get; set; }

    public bool IsParametersShown { get; private set; }

    /// <summary>The message boxes shown, in order.</summary>
    public List<MessageBoxOptions> ShownMessages { get; } = [];

    /// <summary>The errors the error dialog showed, in order.</summary>
    public List<Exception> ShownErrors { get; } = [];

    public static OperationTestGraph Create(
        bool requiresConfirmation = false, bool requiresAdministration = false, bool hasInputParameter = false, ExclusionTarget excludedFrom = ExclusionTarget.None) =>
        new(requiresConfirmation, requiresAdministration, hasInputParameter, excludedFrom);

    /// <summary>Registers the method as a member of a new subject and returns that member.</summary>
    public static RegisteredSubjectProperty CreateMember(MethodMetadata method)
    {
        var subject = new PersistedDevice(InterceptorSubjectContext.Create().WithRegistry());
        return subject.TryGetRegisteredSubject()!.AddProperty("Run", typeof(MethodMetadata), _ => method, null);
    }

    private void Setup<TDialog>(Func<DialogParameters, DialogResult> getResult)
        where TDialog : IComponent
    {
        _dialogs
            .Setup(service => service.ShowAsync<TDialog>(It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters parameters, DialogOptions _) =>
            {
                var result = getResult(parameters);
                var reference = new Mock<IDialogReference>();
                reference.Setup(dialogReference => dialogReference.Result).Returns(Task.FromResult<DialogResult?>(result));
                return reference.Object;
            });
    }
}
