using HomeBlaze.Components;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Blazor.Models;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>Changes the blocks of a markdown page that is edited inline, with the dialogs that ask for them.</summary>
internal sealed class MarkdownPageStructureEditor : IPageStructureEditor
{
    private readonly MarkdownFile _file;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;
    private readonly SubjectFactory _subjectFactory;

    public MarkdownPageStructureEditor(MarkdownFile file, IDialogService dialogService, ISnackbar snackbar, SubjectFactory subjectFactory)
    {
        _file = file;
        _dialogService = dialogService;
        _snackbar = snackbar;
        _subjectFactory = subjectFactory;
    }

    /// <summary>Gets the page whose blocks this editor changes.</summary>
    public MarkdownFile File => _file;

    public PageBlockActions GetActions(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject);
        return new PageBlockActions(actions.CanDelete, actions.CanMoveUp, actions.CanMoveDown);
    }

    public IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner)
    {
        var slots = _file.GetListSlots(owner);
        var result = new PageListSlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            result[index] = new PageListSlot(slots[index].Name, slots[index].ItemType);
        }

        return result;
    }

    public string? GetBlockName(IInterceptorSubject subject) => _file.GetBlockName(subject);

    public bool CanInsertInto(IInterceptorSubject content) => _file.IsContentOfPage(content);

    public async Task DeleteAsync(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject);
        if (!actions.CanDelete)
        {
            return;
        }

        var name = _file.GetBlockName(subject) ?? SubjectTypeCatalog.Describe(subject.GetType()).DisplayName;
        var message = actions.ContainedBlockCount switch
        {
            0 => $"Delete {name}?",
            1 => $"Delete {name} and the block inside it?",
            var count => $"Delete {name} and the {count} blocks inside it?"
        };

        var confirmed = await _dialogService.ShowMessageBoxAsync("Delete block", message, yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true)
        {
            await RunAsync(() => _file.DeleteBlockAsync(subject, CancellationToken.None));
        }
    }

    public Task MoveAsync(IInterceptorSubject subject, int direction) =>
        RunAsync(() => _file.MoveBlockAsync(subject, direction, CancellationToken.None));

    public async Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions
        {
            NameField = MarkdownBlockNames.CreateNameOptions(_file.Content ?? string.Empty)
        });

        if (result is null)
        {
            return;
        }

        // Only the text of the subject goes into the page, and the reload creates the instance that lives on.
        try
        {
            await RunAsync(() => _file.InsertWidgetAsync(content, before, result.Subject, result.Name, CancellationToken.None));
        }
        finally
        {
            _subjectFactory.DiscardSubject(result.Subject);
        }
    }

    public async Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot)
    {
        IInterceptorSubject? item = null;
        if (slot.ItemType is { } itemType)
        {
            var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions { Type = itemType });
            if (result is null)
            {
                return;
            }

            item = result.Subject;
        }

        try
        {
            await RunAsync(() => _file.AddSlotItemAsync(owner, slot.Name, item, CancellationToken.None));
        }
        finally
        {
            if (item is not null)
            {
                _subjectFactory.DiscardSubject(item);
            }
        }
    }

    // A write that the storage cannot make, such as a file that another process holds open, must not end the circuit.
    private async Task RunAsync(Func<Task<MarkdownEditResult>> edit)
    {
        try
        {
            Report(await edit());
        }
        catch (IOException exception)
        {
            _snackbar.Add($"The page could not be saved. {exception.Message}", Severity.Error);
        }
    }

    private void Report(MarkdownEditResult result)
    {
        if (!result.IsWritten)
        {
            _snackbar.Add($"The page was not changed. {result.Refusal}", Severity.Warning);
        }
    }
}
