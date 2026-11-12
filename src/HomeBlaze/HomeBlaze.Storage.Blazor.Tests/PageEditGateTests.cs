using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Storage.Blazor.Files;
using HomeBlaze.Storage.Files;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Blazor.Tests;

/// <summary>
/// The structure editor of a page and the editors of Markdown and JSON files change a file only when the viewer may
/// administer its content, also when the method is called with its control hidden.
/// </summary>
public sealed class PageEditGateTests : BunitContext, IAsyncLifetime
{
    private const string Refusal = "Your roles do not allow this change.";
    private const string MarkdownEditorModule = "./_content/HomeBlaze.Storage.Blazor/Files/MarkdownSourceEditor.razor.js";

    private readonly TestUserPermissions _permissions = new();
    private readonly Mock<IDialogService> _dialogService = new(MockBehavior.Strict);
    private readonly List<string> _snackbarMessages = [];

    public PageEditGateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheContentOfThePage_ThenTheStructureEditorOffersNothingAndChangesNothing()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        _permissions.CannotAdminister.Add(GetMember(graph.Page, nameof(MarkdownFile.Content)));
        var editor = CreateStructureEditor(graph.Page);
        var content = graph.Page.Content;

        // Act
        var actions = editor.GetActions(graph.Door);
        var addTargets = editor.GetAddTargets(graph.Page);
        var canInsert = editor.CanInsertInto(graph.Page);
        await editor.MoveAsync(graph.Door, 1);
        await editor.DeleteAsync(graph.Door);
        await editor.InsertWidgetAsync(graph.Page, null);

        // Assert
        Assert.Equal(default, actions);
        Assert.Empty(addTargets);
        Assert.False(canInsert);
        Assert.Equal(content, graph.Page.Content);
        Assert.Equal(content, await graph.ReadTextAsync("page.md"));
        Assert.Equal(3, _snackbarMessages.Count(message => message == Refusal));
        _dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheMemberThatHoldsABlock_ThenTheBlockIsNotDeleted()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        _permissions.CannotAdminister.Add(GetMember(graph.Page, nameof(MarkdownFile.Children)));
        var editor = CreateStructureEditor(graph.Page);
        var content = graph.Page.Content;

        // Act
        await editor.DeleteAsync(graph.Door);

        // Assert
        Assert.Equal(content, graph.Page.Content);
        Assert.Contains(Refusal, _snackbarMessages);
        _dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheContentOfThePage_ThenTheStructureEditorMovesABlock()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        var editor = CreateStructureEditor(graph.Page);
        var content = graph.Page.Content;

        // Act
        var actions = editor.GetActions(graph.Door);
        await editor.MoveAsync(graph.Door, 1);

        // Assert
        Assert.True(actions.CanDelete);
        Assert.NotEqual(content, graph.Page.Content);
        Assert.DoesNotContain(Refusal, _snackbarMessages);
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheContentOfAMarkdownFile_ThenSavingItsEditorWritesNothing()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        _permissions.CannotAdminister.Add(GetMember(graph.Page, nameof(MarkdownFile.Content)));
        var content = await graph.ReadTextAsync("page.md");
        var (page, editor) = RenderMarkdownEditor(graph, "Changed text.");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.InvokeAsync(() => editor.Instance.SaveAsync(CancellationToken.None)));
        Assert.Equal(Refusal, exception.Message);
        Assert.Equal(content, await graph.ReadTextAsync("page.md"));
        Assert.Empty(page.FindAll("[data-testid='create-subject-button']"));
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheContentOfAMarkdownFile_ThenSavingItsEditorWritesTheText()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        var (page, editor) = RenderMarkdownEditor(graph, "Changed text.");

        // Act
        await editor.InvokeAsync(() => editor.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal("Changed text.", await graph.ReadTextAsync("page.md"));
        Assert.NotEmpty(page.FindAll("[data-testid='create-subject-button']"));
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheFolderOfAJsonFile_ThenSavingItsEditorWritesNothing()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        var file = await graph.AddJsonFileAsync("data.json", "{}");
        _permissions.CannotAdminister.Add(GetMember(graph.Storage, nameof(FluentStorageContainer.Children)));
        var component = RenderJsonEditor(file, "{ \"changed\": true }");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Equal(Refusal, exception.Message);
        Assert.Equal("{}", await graph.ReadTextAsync("data.json"));
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheFolderOfAJsonFile_ThenSavingItsEditorWritesTheText()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("Some text.");
        var file = await graph.AddJsonFileAsync("data.json", "{}");
        var component = RenderJsonEditor(file, "{ \"changed\": true }");

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal("{ \"changed\": true }", await graph.ReadTextAsync("data.json"));
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    private MarkdownPageStructureEditor CreateStructureEditor(MarkdownFile page)
    {
        var snackbar = new Mock<ISnackbar>();
        snackbar
            .Setup(instance => instance.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()))
            .Callback((string message, Severity _, Action<SnackbarOptions>? _, string? _) => _snackbarMessages.Add(message));
        return new MarkdownPageStructureEditor(
            page,
            new WidgetSelection(),
            new SubjectComponent(),
            null,
            _dialogService.Object,
            snackbar.Object,
            new SubjectFactory(new ServiceCollection().BuildServiceProvider()),
            NullLogger.Instance,
            _permissions,
            () => false);
    }

    private (IRenderedComponent<IComponent> Page, IRenderedComponent<MarkdownFileEditComponent> Editor) RenderMarkdownEditor(
        PageTestGraph graph, string shownText)
    {
        // The editor shows its text in a text area, as on a touch device, whose text the script gives.
        var module = JSInterop.SetupModule(MarkdownEditorModule);
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<bool>("isCoarsePointer").SetResult(true);
        module.Setup<string?>("getTextAreaText", _ => true).SetResult(shownText);
        AddGraphServices(graph);

        // The actions of the editor render in the head of the page.
        var page = Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), "EditActions");
            builder.CloseComponent();
            builder.OpenComponent<MarkdownFileEditComponent>(2);
            builder.AddAttribute(3, nameof(MarkdownFileEditComponent.Subject), graph.Page);
            builder.CloseComponent();
        });
        page.WaitForAssertion(() => Assert.NotNull(page.Find("textarea")));
        return (page, page.FindComponent<MarkdownFileEditComponent>());
    }

    private IRenderedComponent<JsonFileEditComponent> RenderJsonEditor(JsonFile file, string shownText)
    {
        JSInterop.Setup<string>(invocation => invocation.Identifier.EndsWith("getValue", StringComparison.Ordinal)).SetResult(shownText);
        return Render<JsonFileEditComponent>(parameters => parameters.Add(editor => editor.Subject, file));
    }

    private void AddGraphServices(PageTestGraph graph)
    {
        Services.AddSingleton(graph.Services.GetRequiredService<ConfigurableSubjectSerializer>());
        Services.AddSingleton(graph.Services.GetRequiredService<SubjectFactory>());
        Services.AddSingleton(graph.Services.GetRequiredService<TypeProvider>());
        Services.AddSingleton(new SubjectComponentRegistry(graph.Services.GetRequiredService<TypeProvider>()));
    }
}
