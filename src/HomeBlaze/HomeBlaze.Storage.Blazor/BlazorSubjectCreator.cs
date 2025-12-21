using HomeBlaze.Components;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;

namespace HomeBlaze.Storage.Blazor;

/// <summary>
/// Blazor implementation of ISubjectCreator that opens the SubjectSetupDialog dialog.
/// </summary>
public class BlazorSubjectCreator : ISubjectCreator
{
    private readonly IDialogService _dialogService;

    public BlazorSubjectCreator(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public Task<CreateSubjectResult?> CreateAsync(CancellationToken cancellationToken)
    {
        return SubjectSetupDialog.ShowAsync(_dialogService);
    }
}
