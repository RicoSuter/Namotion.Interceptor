using HomeBlaze.Components;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Storage.Blazor.Models;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>Changes the blocks of a markdown page that is edited inline, with the dialogs that ask for them.</summary>
internal sealed class MarkdownPageStructureEditor : PageStructureEditor
{
    private const string ChangedWhileAskingMessage = "The page changed while the dialog was open. Try again.";

    private readonly MarkdownFile _file;
    private readonly WidgetSelection _selection;
    private readonly SubjectComponent _pageComponent;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;
    private readonly SubjectFactory _subjectFactory;
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly ILogger _logger;

    // The widgets that render nothing on a viewed page, which a move passes over. Created once, as a method group would
    // allocate a delegate on every call.
    private readonly Func<IInterceptorSubject, bool> _isInvisible;

    public MarkdownPageStructureEditor(
        MarkdownFile file,
        WidgetSelection selection,
        SubjectComponent pageComponent,
        IDialogService dialogService,
        ISnackbar snackbar,
        SubjectFactory subjectFactory,
        SubjectComponentRegistry componentRegistry,
        ILogger logger)
    {
        _file = file;
        _selection = selection;
        _pageComponent = pageComponent;
        _dialogService = dialogService;
        _snackbar = snackbar;
        _subjectFactory = subjectFactory;
        _componentRegistry = componentRegistry;
        _logger = logger;
        _isInvisible = IsInvisible;
    }

    /// <summary>Gets the page whose blocks this editor changes.</summary>
    public MarkdownFile File => _file;

    /// <summary>Gets the selection that the editor sets after an edit.</summary>
    public WidgetSelection Selection => _selection;

    /// <summary>Gets the widget of the page, inside which the editor finds the widget to select.</summary>
    public SubjectComponent PageComponent => _pageComponent;

    public override PageBlockActions GetActions(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject, _isInvisible);
        return new PageBlockActions(actions.CanDelete, actions.CanMoveUp, actions.CanMoveDown);
    }

    public override IReadOnlyList<PageListSlot> GetListSlots(IInterceptorSubject owner)
    {
        var slots = _file.GetListSlots(owner);
        var result = new PageListSlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            result[index] = new PageListSlot(slots[index].Name, slots[index].ItemType);
        }

        return result;
    }

    public override string? GetBlockName(IInterceptorSubject subject) => _file.GetBlockName(subject);

    public override bool CanInsertInto(IInterceptorSubject content) => _file.IsContentOfPage(content);

    public override async Task DeleteAsync(IInterceptorSubject subject)
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

        var content = _file.Content;
        var confirmed = await _dialogService.ShowMessageBoxAsync("Delete block", message, yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true && IsUnchangedSince(content))
        {
            await RunAsync(() => _file.DeleteBlockAsync(subject, CancellationToken.None));
        }
    }

    public override async Task MoveAsync(IInterceptorSubject subject, int direction) =>
        await RunAsync(() => _file.MoveBlockAsync(subject, direction, _isInvisible, CancellationToken.None));

    public override async Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before)
    {
        var pageContent = _file.Content;
        var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions
        {
            NameField = MarkdownBlockNames.CreateNameOptions(pageContent ?? string.Empty)
        });

        if (result is null)
        {
            return;
        }

        // Only the text of the subject goes into the page, and the reload creates the instance that lives on.
        try
        {
            if (IsUnchangedSince(pageContent))
            {
                await RunAsync(() => _file.InsertWidgetAsync(content, before, result.Subject, result.Name, CancellationToken.None));
            }
        }
        finally
        {
            _subjectFactory.DiscardSubject(result.Subject);
        }
    }

    public override async Task AddListItemAsync(IInterceptorSubject owner, PageListSlot slot)
    {
        var pageContent = _file.Content;
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
            if (IsUnchangedSince(pageContent))
            {
                await RunAsync(() => _file.AddSlotItemAsync(owner, slot.Name, item, CancellationToken.None));
            }
        }
        finally
        {
            if (item is not null)
            {
                _subjectFactory.DiscardSubject(item);
            }
        }
    }

    private bool IsInvisible(IInterceptorSubject subject) =>
        _componentRegistry.GetComponent(subject.GetType(), SubjectComponentType.Widget)?.ComponentType is { } componentType &&
        typeof(IInvisibleWidget).IsAssignableFrom(componentType);

    // A dialog asked about the blocks as they were. A reload while it was open can have given an unnamed subject another
    // block, so the edit would hit a block the user did not choose. The same text means the same blocks.
    private bool IsUnchangedSince(string? content)
    {
        if (string.Equals(content, _file.Content, StringComparison.Ordinal))
        {
            return true;
        }

        _snackbar.Add(ChangedWhileAskingMessage, Severity.Warning);
        return false;
    }

    // A write that the storage cannot make must not end the circuit: a file that another process holds open or that
    // may not be written, a storage that does not answer in time (a TimeoutException) or that is not connected (an
    // InvalidOperationException, the base of the internal refusal of the storage). Null when the edit threw.
    private async Task<MarkdownEditResult?> RunAsync(Func<Task<MarkdownEditResult>> edit, bool reportsRefusal = true)
    {
        MarkdownEditResult result;
        try
        {
            result = await edit();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            _logger.Log(
                IsStorageFailure(exception) ? LogLevel.Warning : LogLevel.Error,
                exception,
                "The structure edit of the page {Path} could not be saved",
                _file.FullPath);
            _snackbar.Add($"The page could not be saved. {exception.Message}", Severity.Error);
            return null;
        }

        if (!result.IsWritten)
        {
            if (reportsRefusal)
            {
                _snackbar.Add($"The page was not changed. {result.Refusal}", Severity.Warning);
            }
        }
        else if (result.Subject is { } subject)
        {
            _selection.SelectSubject(subject, _pageComponent);
        }

        return result;
    }

    // The refusal and the timeout of the storage are internal types of its assembly. Any other invalid operation or
    // timeout is a defect, such as an edit called from within a write of the page.
    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException ||
        exception.GetType().Assembly == typeof(MarkdownFile).Assembly;
}
