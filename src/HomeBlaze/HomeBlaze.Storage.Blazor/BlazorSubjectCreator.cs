using HomeBlaze.Components;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;

namespace HomeBlaze.Storage.Blazor;

/// <summary>
/// Blazor implementation of ISubjectCreator that opens the CreateSubjectWizard dialog.
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
        return CreateSubjectWizard.ShowAsync(_dialogService);
    }
}
