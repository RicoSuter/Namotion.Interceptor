using HomeBlaze.Components;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Blazor.Models;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>Changes the blocks of a markdown page that is edited inline, with the dialogs that ask for them.</summary>
internal sealed class MarkdownPageStructureEditor : PageStructureEditor
{
    private const string ChangedWhileAskingMessage = "The page changed while the dialog was open. Try again.";
    private const string TextChangedWhileEditingMessage =
        "The text changed since you opened it. Copy your text, close the dialog and open the text again.";
    private const string PageChangedWhileCreatingMessage =
        "The page changed since you opened the dialog. Copy your text, close the dialog and try again.";

    private readonly MarkdownFile _file;
    private readonly WidgetSelection _selection;
    private readonly SubjectComponent _pageComponent;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;
    private readonly SubjectFactory _subjectFactory;
    private readonly ILogger _logger;
    private readonly Func<bool> _isDarkMode;

    public MarkdownPageStructureEditor(
        MarkdownFile file,
        WidgetSelection selection,
        SubjectComponent pageComponent,
        ISubjectRevealTarget? sourceEditor,
        IDialogService dialogService,
        ISnackbar snackbar,
        SubjectFactory subjectFactory,
        ILogger logger,
        Func<bool> isDarkMode)
    {
        SourceEditor = sourceEditor;
        _file = file;
        _selection = selection;
        _pageComponent = pageComponent;
        _dialogService = dialogService;
        _snackbar = snackbar;
        _subjectFactory = subjectFactory;
        _logger = logger;
        _isDarkMode = isDarkMode;
    }

    /// <summary>Gets the page whose blocks this editor changes.</summary>
    public MarkdownFile File => _file;

    /// <summary>Gets the selection that the editor sets after an edit.</summary>
    public WidgetSelection Selection => _selection;

    /// <summary>Gets the widget of the page, inside which the editor finds the widget to select.</summary>
    public SubjectComponent PageComponent => _pageComponent;

    /// <summary>Gets the page, which switches to an editor of its source to show the lines of a block, or null.</summary>
    public ISubjectRevealTarget? SourceEditor { get; }

    public override PageBlockActions GetActions(IInterceptorSubject subject)
    {
        var actions = _file.GetBlockActions(subject);
        return new PageBlockActions(
            actions.CanDelete,
            actions.CanMoveUp,
            actions.CanMoveDown,
            CanEditText: subject is HtmlSegment && actions.CanDelete,
            CanEditInSource: subject is HtmlSegment { Start: HtmlSegmentStart.Source } && SourceEditor is not null);
    }

    public override Task EditInSourceAsync(IInterceptorSubject subject) =>
        subject is HtmlSegment { Start: HtmlSegmentStart.Source } && SourceEditor is { } sourceEditor
            ? sourceEditor.RevealAsync(subject)
            : Task.CompletedTask;

    // Two reads of the page, which a reload between them can answer from two versions. The bar renders again after every
    // reload, as GetContentSlots reads the children of the page, and every add checks the page again.
    public override IReadOnlyList<PageAddTarget> GetAddTargets(IInterceptorSubject owner) =>
        CreateAddTargets(_file.GetContentSlots(owner), _file.GetListSlots(owner));

    private static PageAddTarget[] CreateAddTargets(IReadOnlyList<MarkdownContentSlot> contentSlots, IReadOnlyList<MarkdownListSlot> listSlots)
    {
        if (contentSlots.Count + listSlots.Count == 0)
        {
            return [];
        }

        var result = new PageAddTarget[contentSlots.Count + listSlots.Count];
        var index = 0;
        foreach (var kind in (ReadOnlySpan<MarkdownContentSlotKind>)[MarkdownContentSlotKind.Default, MarkdownContentSlotKind.Named, MarkdownContentSlotKind.ListItem])
        {
            foreach (var slot in contentSlots)
            {
                if (slot.Kind == kind)
                {
                    result[index++] = new PageAddTarget(ToTargetKind(kind), slot.Name, null);
                }
            }
        }

        foreach (var slot in listSlots)
        {
            result[index++] = new PageAddTarget(PageAddTargetKind.ConfiguredItem, slot.Name, slot.ItemType);
        }

        return result;
    }

    private static PageAddTargetKind ToTargetKind(MarkdownContentSlotKind kind) => kind switch
    {
        MarkdownContentSlotKind.Default => PageAddTargetKind.DefaultContent,
        MarkdownContentSlotKind.Named => PageAddTargetKind.NamedContent,
        _ => PageAddTargetKind.ContentItem
    };

    public override string? GetBlockName(IInterceptorSubject subject) => _file.GetBlockName(subject);

    public override bool CanInsertInto(IInterceptorSubject content) => _file.IsContentOfPage(content);

    public override async Task DeleteAsync(IInterceptorSubject subject)
    {
        if (subject is HtmlSegment segment)
        {
            if (_file.GetTextBlockText(segment) is not { } text)
            {
                return;
            }

            var isConfirmed = await _dialogService.ShowMessageBoxAsync("Delete text", "Delete this text?", yesText: "Delete", cancelText: "Cancel");
            if (isConfirmed == true)
            {
                await RunAsync(() => _file.ReplaceTextBlockAsync(segment, text, string.Empty, CancellationToken.None));
            }

            return;
        }

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
        await RunAsync(() => subject is HtmlSegment segment
            ? _file.MoveTextBlockAsync(segment, direction, CancellationToken.None)
            : _file.MoveBlockAsync(subject, direction, CancellationToken.None));

    public override Task InsertWidgetAsync(IInterceptorSubject content, IInterceptorSubject? before) =>
        CreateWidgetAsync((widget, name) => _file.InsertWidgetAsync(content, before, widget, name, CancellationToken.None));

    public override Task AddWidgetAsync(IInterceptorSubject owner, PageAddTarget target) =>
        CreateWidgetAsync((widget, name) => _file.AddWidgetToSlotAsync(owner, target.SlotName, widget, name, CancellationToken.None));

    public override Task AddTextAsync(IInterceptorSubject owner, PageAddTarget target) =>
        CreateTextAsync(
            target.AddTextName ?? "Add text",
            text => _file.AddTextToSlotAsync(owner, target.SlotName, text, CancellationToken.None));

    public override async Task AddListItemAsync(IInterceptorSubject owner, PageAddTarget target)
    {
        if (target is not { SlotName: { } slotName, ItemType: { } itemType })
        {
            return;
        }

        var pageContent = _file.Content;
        var result = await SubjectSetupDialog.ShowAsync(_dialogService, new SubjectSetupOptions { Type = itemType });
        if (result is null)
        {
            return;
        }

        try
        {
            if (IsUnchangedSince(pageContent))
            {
                await RunAsync(() => _file.AddSlotItemAsync(owner, slotName, result.Subject, CancellationToken.None));
            }
        }
        finally
        {
            _subjectFactory.DiscardSubject(result.Subject);
        }
    }

    public override async Task EditTextAsync(IInterceptorSubject subject)
    {
        if (subject is not HtmlSegment segment || _file.GetTextBlockText(segment) is not { } text)
        {
            return;
        }

        await MarkdownTextDialog.ShowAsync(
            _dialogService,
            "Edit text",
            text,
            _isDarkMode(),
            async newText =>
            {
                // Text saved as it was opened is not written: the write would still change the block, such as the indentation
                // of a lazy line.
                if (string.Equals(newText.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"), StringComparison.Ordinal))
                {
                    return null;
                }

                var result = await RunAsync(() => _file.ReplaceTextBlockAsync(segment, text, newText, CancellationToken.None), reportsInSnackbar: false);

                // The dialog keeps what was typed, but a save of it is refused again once the text changed, so the message
                // says how to get it into the page.
                return result.IsWritten || _file.GetTextBlockText(segment) == text
                    ? result.Refusal
                    : TextChangedWhileEditingMessage;
            });
    }

    public override bool CanInsertText(IInterceptorSubject content, IInterceptorSubject? before) =>
        _file.CanInsertTextAt(content, before);

    public override Task InsertTextAsync(IInterceptorSubject content, IInterceptorSubject? before) =>
        CreateTextAsync("Insert text", text => _file.InsertTextAsync(content, before, text, CancellationToken.None));

    // Asks for a widget and its name, and writes it unless the page changed while the dialog was open.
    private async Task CreateWidgetAsync(Func<IInterceptorSubject, string?, Task<MarkdownEditResult>> write)
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
                await RunAsync(() => write(result.Subject, result.Name));
            }
        }
        finally
        {
            _subjectFactory.DiscardSubject(result.Subject);
        }
    }

    // Asks for new text in an empty dialog, which shows a refusal and keeps the text.
    private async Task CreateTextAsync(string title, Func<string, Task<MarkdownEditResult>> write)
    {
        var pageContent = _file.Content;
        await MarkdownTextDialog.ShowAsync(
            _dialogService,
            title,
            string.Empty,
            _isDarkMode(),
            async text =>
            {
                // The place was chosen in the page as it was, and every later save is refused as well, so the message says
                // how to get the text into the page.
                if (!string.Equals(pageContent, _file.Content, StringComparison.Ordinal))
                {
                    return PageChangedWhileCreatingMessage;
                }

                var result = await RunAsync(() => write(text), reportsInSnackbar: false);
                return result.Refusal;
            });
    }

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
    // InvalidOperationException, the base of the internal refusal of the storage), which is then the refusal of the result.
    // A caller that shows the refusal itself, such as a dialog, passes false for reportsInSnackbar.
    private async Task<MarkdownEditResult> RunAsync(Func<Task<MarkdownEditResult>> edit, bool reportsInSnackbar = true)
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
            var failure = $"The page could not be saved. {exception.Message}";
            if (reportsInSnackbar)
            {
                _snackbar.Add(failure, Severity.Error);
            }

            return MarkdownEditResult.Refused(failure);
        }

        if (!result.IsWritten)
        {
            if (reportsInSnackbar)
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
