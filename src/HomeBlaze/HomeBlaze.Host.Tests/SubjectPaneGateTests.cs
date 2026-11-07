using Bunit;
using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Host.Components.Browser;
using HomeBlaze.Host.Preferences;
using HomeBlaze.Host.Services.Display;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor.Files;
using HomeBlaze.Storage.Blazor.Tests;
using HomeBlaze.Storage.Files;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Tests;

/// <summary>
/// The pane of a subject shows its edit, delete and reorder buttons only to a viewer who may make the change, and the browser
/// deletes a subject only when the viewer may administer the member that holds it, also when its button was clicked.
/// </summary>
public sealed class SubjectPaneGateTests : BunitContext, IAsyncLifetime
{
    private const string Refusal = "Your roles do not allow this change.";

    private readonly TestUserPermissions _permissions = new();
    private PageTestGraph _graph = null!;
    private IRenderedComponent<MudDialogProvider>? _dialogProvider;

    public SubjectPaneGateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton(new SubjectComponentRegistry(new TypeProvider().AddAssembly(typeof(MarkdownFileEditComponent).Assembly)));
        Services.AddSingleton<PropertyRowLayoutService>();
        Services.AddSingleton<MetadataLinkResolver>();
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        Services.AddSingleton<BrowserPreferences>();
        AddBunitPersistentComponentState();
    }

    public async Task InitializeAsync()
    {
        _graph = await PageTestGraph.CreateAsync("Some text.");
        await _graph.AddJsonFileAsync("data.json", "{}");
        var rootManager = _graph.Services.GetRequiredService<RootManager>();
        rootManager.Root = _graph.Storage;
        Services.AddSingleton(rootManager);
        Services.AddSingleton(_graph.Services.GetRequiredService<SubjectPathResolver>());
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private MarkdownFile Page => _graph.Page;

    private IEnumerable<string?> SnackbarMessages => Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(snackbar => snackbar.Message);

    [Fact]
    public void WhenTheViewerMayNotAdministerTheMemberThatHoldsAFile_ThenItsPaneShowsNoDeleteButton()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(_graph.Storage, nameof(FluentStorageContainer.Children)));

        // Act
        var panel = RenderPanel(Page);

        // Assert
        Assert.Empty(panel.FindAll("button[aria-label='Delete']"));
    }

    [Fact]
    public void WhenTheViewerMayAdministerTheMemberThatHoldsAFile_ThenItsPaneShowsTheDeleteButton()
    {
        // Act
        var panel = RenderPanel(Page);

        // Assert
        Assert.Single(panel.FindAll("button[aria-label='Delete']"));
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerTheContentOfAPage_ThenItsPaneShowsNoEditButton()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(Page, nameof(MarkdownFile.Content)));

        // Act
        var panel = RenderPanel(Page);

        // Assert
        Assert.Empty(panel.FindAll("[data-testid='edit-subject-button']"));
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerAnyConfigurationMember_ThenThePaneShowsNoEditButton()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(_graph.Door, nameof(TestDoor.IsOpen)));
        _permissions.CannotAdminister.Add(GetMember(_graph.Door, nameof(TestDoor.IsLocked)));

        // Act
        var panel = RenderPanel(_graph.Door);

        // Assert
        Assert.Empty(panel.FindAll("[data-testid='edit-subject-button']"));
    }

    [Fact]
    public void WhenTheViewerMayAdministerAConfigurationMember_ThenThePaneShowsTheEditButton()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(_graph.Door, nameof(TestDoor.IsOpen)));

        // Act
        var panel = RenderPanel(_graph.Door);

        // Assert
        Assert.Single(panel.FindAll("[data-testid='edit-subject-button']"));
    }

    [Fact]
    public void WhenTheViewerMayNotWriteTheMetadataOfAFolder_ThenItsPaneShowsNoReorderButton()
    {
        // Arrange
        _permissions.CannotWrite.Add(FluentStorageContainer.GetOrAddMetadataMember(_graph.Storage.TryGetRegisteredSubject()!, null)!);

        // Act
        var panel = RenderPanel(_graph.Storage);

        // Assert
        Assert.Empty(panel.FindAll("[data-testid='reorder-children-button']"));
    }

    [Fact]
    public void WhenTheViewerMayWriteTheMetadataOfAFolder_ThenItsPaneShowsTheReorderButton()
    {
        // Act
        var panel = RenderPanel(_graph.Storage);

        // Assert
        Assert.Single(panel.FindAll("[data-testid='reorder-children-button']"));
    }

    [Fact]
    public async Task WhenTheViewerMayNoLongerWriteTheMetadataOfAFolderWhenAChildIsDropped_ThenTheOrderIsKept()
    {
        // Arrange
        var panel = RenderPanel(_graph.Storage);
        await panel.Find("[data-testid='reorder-children-button']").ClickAsync(new MouseEventArgs());
        var order = _graph.Storage.ChildOrder;

        // Act
        _permissions.CannotWrite.Add(FluentStorageContainer.GetOrAddMetadataMember(_graph.Storage.TryGetRegisteredSubject()!, null)!);
        await panel.InvokeAsync(() => panel.Instance.OnChildDropped("data.json", "page.md"));

        // Assert
        Assert.Equal(order, _graph.Storage.ChildOrder);
        Assert.Contains("Your roles do not allow changing this.", SnackbarMessages);
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerTheMemberThatHoldsAFile_ThenTheBrowserShowsNoDeleteButtonInItsPane()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(_graph.Storage, nameof(FluentStorageContainer.Children)));

        // Act
        var browser = RenderBrowser("page.md");

        // Assert
        Assert.Equal(2, browser.FindAll("[data-testid='subject-pane']").Count);
        Assert.Empty(browser.FindAll("button[aria-label='Delete']"));
    }

    [Fact]
    public void WhenTheViewerMayNoLongerAdministerTheMemberThatHoldsAFileWhenDeleteIsClicked_ThenTheBrowserKeepsTheFile()
    {
        // Arrange
        var browser = RenderBrowser("page.md");
        var deleteButton = browser.Find("button[aria-label='Delete']");

        // Act
        _permissions.CannotAdminister.Add(GetMember(_graph.Storage, nameof(FluentStorageContainer.Children)));
        deleteButton.Click();

        // Assert
        Assert.Same(Page, _graph.Storage.Children["page.md"]);
        Assert.DoesNotContain("Delete Subject", _dialogProvider!.Markup, StringComparison.Ordinal);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public void WhenAnOperationIsDisabledWithAReason_ThenItsButtonIsDisabledWithTheReasonAsTooltip()
    {
        // Arrange
        var member = ((IInterceptorSubject)_graph.Storage).TryGetMethodMember(nameof(FluentStorageContainer.CreateMarkdownFileAsync))!;
        member.AddAttribute<bool>(KnownAttributes.IsEnabled, _ => false);
        member.AddAttribute<string?>(KnownAttributes.DisabledReason, _ => "Not while the storage is read-only.");

        // Act
        var panel = RenderPanel(_graph.Storage);

        // Assert
        var button = panel.FindAll("button.subject-pane-method").Single(candidate => candidate.TextContent.Contains(member.TryGetMethodMetadata()!.Title, StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("Not while the storage is read-only.", button.GetAttribute("title"));
    }

    [Fact]
    public void WhenAnOperationIsEnabled_ThenItsButtonShowsNoReason()
    {
        // Arrange
        var member = ((IInterceptorSubject)_graph.Storage).TryGetMethodMember(nameof(FluentStorageContainer.CreateMarkdownFileAsync))!;
        member.AddAttribute<string?>(KnownAttributes.DisabledReason, _ => "Not while the storage is read-only.");

        // Act
        var panel = RenderPanel(_graph.Storage);

        // Assert
        var button = panel.FindAll("button.subject-pane-method").Single(candidate => candidate.TextContent.Contains(member.TryGetMethodMetadata()!.Title, StringComparison.Ordinal));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Null(button.GetAttribute("title"));
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    private IRenderedComponent<SubjectPropertyPanel> RenderPanel(IInterceptorSubject subject) =>
        Render<SubjectPropertyPanel>(parameters => parameters
            .Add(panel => panel.Subject, subject)
            .Add(panel => panel.SubjectRegistry, subject.Context.GetService<ISubjectRegistry>()));

    private IRenderedComponent<SubjectBrowser> RenderBrowser(string path)
    {
        _dialogProvider = Render<MudDialogProvider>();
        return Render<SubjectBrowser>(parameters => parameters.Add(browser => browser.Path, path));
    }
}
