using HomeBlaze.Components.Dialogs;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;

namespace HomeBlaze.Storage.Blazor;

/// <summary>
/// Blazor implementation of ISubjectSetupService that opens the SubjectSetupDialog dialog.
/// </summary>
public class BlazorSubjectSetupService : ISubjectSetupService
{
    private readonly IDialogService _dialogService;

    public BlazorSubjectSetupService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public Task<CreateSubjectResult?> CreateSubjectAsync(CancellationToken cancellationToken)
    {
        return SubjectSetupDialog.ShowAsync(_dialogService);
    }
}
