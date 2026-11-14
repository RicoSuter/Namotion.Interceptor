using Bunit;
using HomeBlaze.Components.Inputs;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Virtual.Blazor.Tests;

// Owns the context instead of deriving from it: the popover services of MudBlazor can only be disposed asynchronously,
// and xUnit disposes a test class synchronously.
public class VirtualSubjectEditComponentTests : IAsyncLifetime
{
    private readonly BunitContext _context = new();
    private readonly VirtualSubject _subject;

    public VirtualSubjectEditComponentTests()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.Services.AddMudServices();
        _context.Services.AddSingleton(TimeProvider.System);
        _context.Services.AddScoped<PreviewTicker>();
        var resolver = new SubjectPathResolver(() => null);
        _context.Services.AddSingleton(resolver);
        var awaiter = new VirtualPropertyAwaiter(InterceptorSubjectContext.Create(), NullLogger<VirtualPropertyAwaiter>.Instance);
        _subject = new VirtualSubject(resolver, awaiter, NullLogger<VirtualSubject>.Instance);
        _context.Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Fact]
    public void WhenAPropertyIsAdded_ThenTheEditorIsInvalidUntilItsExpressionIsWritten()
    {
        // Arrange
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));

        // Act
        editor.Find("[data-testid=virtual-add-property]").Click();
        var isValidWithoutExpression = editor.Instance.IsValid;
        editor.Find("[data-testid=virtual-expression-text]").Input("1 + 1");

        // Assert
        Assert.False(isValidWithoutExpression);
        Assert.True(editor.Instance.IsValid);
    }

    [Fact]
    public async Task WhenTheEditorIsSaved_ThenTheSubjectHasTheEntries()
    {
        // Arrange
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));
        await editor.Find("[data-testid=virtual-add-property]").ClickAsync();
        await editor.Find("[data-testid=virtual-expression-text]").InputAsync("1 + 1");

        // Act
        await editor.InvokeAsync(() => editor.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal("1 + 1", _subject.Properties!["Property"].Expression);
        Assert.False(editor.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenTheEditorIsSavedWithErrors_ThenItThrowsAndTheSubjectIsUnchanged()
    {
        // Arrange
        var properties = new Dictionary<string, VirtualProperty> { ["A"] = new() { Expression = "1", DataType = VirtualDataType.Number } };
        _subject.Properties = properties;
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));
        await editor.Find("[data-testid=virtual-add-property]").ClickAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.InvokeAsync(() => editor.Instance.SaveAsync(CancellationToken.None)));
        Assert.Same(properties, _subject.Properties);
        Assert.True(editor.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenAnEntryIsAddedToACollapsedSubject_ThenTheSubjectIsExpanded()
    {
        // Arrange
        _subject.Properties = new Dictionary<string, VirtualProperty>
        {
            ["Group"] = new() { Properties = new Dictionary<string, VirtualProperty> { ["A"] = new() { Expression = "1", DataType = VirtualDataType.Number } } }
        };
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));
        await editor.Find("[data-testid=virtual-entry-Group] > .mud-treeview-item-content").ClickAsync();
        await editor.Find("[data-testid=virtual-entry-Group] .mud-treeview-item-arrow button").ClickAsync();
        var isCollapsed = !editor.Find("[data-testid=virtual-entry-Group] .mud-treeview-item-arrow button").ClassList.Contains("mud-transform");

        // Act
        await editor.Find("[data-testid=virtual-add-property]").ClickAsync();

        // Assert
        Assert.True(isCollapsed);
        Assert.Contains("mud-transform", editor.Find("[data-testid=virtual-entry-Group] .mud-treeview-item-arrow button").ClassList);
        Assert.Contains("mud-treeview-item-selected", editor.Find("[data-testid='virtual-entry-Group/Property'] > .mud-treeview-item-content").ClassList);
    }

    [Fact]
    public async Task WhenTheRootIsSelected_ThenAPropertyIsAddedAtTheRoot()
    {
        // Arrange
        _subject.Properties = new Dictionary<string, VirtualProperty>
        {
            ["Group"] = new() { Properties = new Dictionary<string, VirtualProperty> { ["A"] = new() { Expression = "1", DataType = VirtualDataType.Number } } }
        };
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));
        await editor.Find("[data-testid='virtual-entry-Group/A'] > .mud-treeview-item-content").ClickAsync();

        // Act
        await editor.Find("[data-testid=virtual-entry-root] > .mud-treeview-item-content").ClickAsync();
        await editor.Find("[data-testid=virtual-add-property]").ClickAsync();

        // Assert
        Assert.Contains("mud-treeview-item-selected", editor.Find("[data-testid=virtual-entry-Property] > .mud-treeview-item-content").ClassList);
        Assert.Empty(editor.FindAll("[data-testid='virtual-entry-Group/Property']"));
    }

    [Fact]
    public async Task WhenTheSelectionChanges_ThenTheAddTargetNamesTheRootOrTheSubjectThatHoldsIt()
    {
        // Arrange
        _subject.Title = "Pump overview";
        _subject.Properties = new Dictionary<string, VirtualProperty>
        {
            ["Group"] = new() { Properties = new Dictionary<string, VirtualProperty> { ["A"] = new() { Expression = "1", DataType = VirtualDataType.Number } } }
        };
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));
        var initialTarget = editor.Find("[data-testid=virtual-add-target]").TextContent;
        var isRootSelectedInitially = editor.Find("[data-testid=virtual-entry-root] > .mud-treeview-item-content").ClassList.Contains("mud-treeview-item-selected");

        // Act
        await editor.Find("[data-testid='virtual-entry-Group/A'] > .mud-treeview-item-content").ClickAsync();

        // Assert
        Assert.True(isRootSelectedInitially);
        Assert.Equal("New entries go into Pump overview", initialTarget);
        Assert.Equal("New entries go into Group", editor.Find("[data-testid=virtual-add-target]").TextContent);
    }

    [Fact]
    public async Task WhenATopLevelEntryIsDeleted_ThenTheRootIsSelected()
    {
        // Arrange
        _subject.Properties = new Dictionary<string, VirtualProperty> { ["A"] = new() { Expression = "1", DataType = VirtualDataType.Number } };
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));
        await editor.Find("[data-testid=virtual-entry-A] > .mud-treeview-item-content").ClickAsync();

        // Act
        await editor.Find("[data-testid=virtual-delete]").ClickAsync();

        // Assert
        Assert.Contains("mud-treeview-item-selected", editor.Find("[data-testid=virtual-entry-root] > .mud-treeview-item-content").ClassList);
        Assert.Contains("become properties of the subject", editor.Find("[data-testid=virtual-root-detail]").TextContent);
    }

    [Fact]
    public void WhenEntriesReadEachOther_ThenTheEditorReportsItAndIsInvalid()
    {
        // Arrange
        _subject.Properties = new Dictionary<string, VirtualProperty>
        {
            ["A"] = new() { Expression = "B", DataType = VirtualDataType.Number },
            ["B"] = new() { Expression = "A", DataType = VirtualDataType.Number }
        };

        // Act
        var editor = _context.Render<VirtualSubjectEditComponent>(parameters => parameters.Add(component => component.Subject, _subject));

        // Assert
        Assert.False(editor.Instance.IsValid);
        Assert.Contains("reads itself", editor.Find("[data-testid=virtual-errors]").TextContent);
    }
}
