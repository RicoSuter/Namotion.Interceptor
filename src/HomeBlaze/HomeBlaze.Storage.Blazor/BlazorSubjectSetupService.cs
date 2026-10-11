using HomeBlaze.Components.Dialogs;
using HomeBlaze.Storage.Abstractions;
using MudBlazor;

namespace HomeBlaze.Storage.Blazor;

public class BlazorSubjectSetupService : ISubjectSetupService
{
    private readonly IDialogService _dialogService;

    public BlazorSubjectSetupService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public Task<CreateSubjectResult?> CreateSubjectAsync(SubjectSetupRequest request, CancellationToken cancellationToken)
    {
        return SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions
        {
            NameField = new SubjectNameOptions("File name", FileExtensions.Json, request.ExistingNames)
        });
    }
}
