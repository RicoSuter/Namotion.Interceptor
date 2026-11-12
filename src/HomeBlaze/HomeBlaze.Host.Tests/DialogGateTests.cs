using Bunit;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Host.Components.Clients;
using HomeBlaze.Host.Components.Dialogs;
using HomeBlaze.Services;
using HomeBlaze.Services.Clients;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Blazor.Tests;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Tests;

/// <summary>A client whose Rename operation requires administration, as that of the application does.</summary>
[InterceptorSubject]
public partial class RenamedClient
{
    public string? Name { get; private set; }

    [Operation(RequiresAdministration = true)]
    public Task RenameAsync(string name, CancellationToken cancellationToken)
    {
        Name = name;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The metadata dialog saves only when the viewer may write the metadata member, and the dialog that names a browser runs
/// the Rename operation of its client only when the viewer may run it, also when Save was clicked.
/// </summary>
public sealed class DialogGateTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();
    private readonly ClientConnectionService _clientConnections = new(TimeProvider.System, NullLogger<ClientConnectionService>.Instance);
    private PageTestGraph _graph = null!;
    private IRenderedComponent<MudDialogProvider> _provider = null!;

    public DialogGateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton(_clientConnections);
        Services.AddSingleton(new SubjectComponentRegistry(new TypeProvider()));
    }

    public async Task InitializeAsync()
    {
        _graph = await PageTestGraph.CreateAsync("Some text.");
        Services.AddSingleton(_graph.Services.GetRequiredService<RootManager>());
        _provider = Render<MudDialogProvider>();
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    private RegisteredSubjectProperty PageMetadataMember =>
        FluentStorageContainer.GetOrAddMetadataMember(_graph.Page.TryGetRegisteredSubject()!, null)!;

    private IEnumerable<string?> SnackbarMessages => Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(snackbar => snackbar.Message);

    [Fact]
    public async Task WhenTheViewerMayNotWriteTheMetadataMember_ThenTheMetadataDialogCannotSave()
    {
        // Arrange
        _permissions.CannotWrite.Add(PageMetadataMember);

        // Act
        await ShowMetadataDialogAsync();

        // Assert
        Assert.True(_provider.Find("[data-testid='metadata-save']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenTheViewerMayNoLongerWriteTheMetadataMemberWhenSaveIsClicked_ThenNoMetadataIsWritten()
    {
        // Arrange
        await ShowMetadataDialogAsync();
        await _provider.Find("input[data-testid='metadata-navigation-title']").ChangeAsync(new ChangeEventArgs { Value = "Start" });

        // Act
        _permissions.CannotWrite.Add(PageMetadataMember);
        await _provider.Find("[data-testid='metadata-save']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Null(_graph.Page.GetMetadata());
        Assert.Contains("Your roles do not allow changing this.", SnackbarMessages);
        Assert.NotEmpty(_provider.FindAll("[data-testid='metadata-edit-dialog']"));
    }

    [Fact]
    public async Task WhenTheViewerMayWriteTheMetadataMember_ThenTheMetadataDialogCanSave()
    {
        // Act
        await ShowMetadataDialogAsync();

        // Assert
        Assert.False(_provider.Find("[data-testid='metadata-save']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenTheViewerMayNotRunTheRenameOperation_ThenTheNameDialogCannotSave()
    {
        // Arrange
        var (connection, member, _) = CreateClient();
        _permissions.CannotInvoke.Add(member);

        // Act
        await ShowNameDialogAsync(connection);
        await _provider.Find("input[data-testid='client-name-input']").InputAsync(new ChangeEventArgs { Value = "Kitchen" });

        // Assert
        Assert.True(_provider.Find("[data-testid='client-name-save']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenTheViewerMayNoLongerRunTheRenameOperationWhenSaveIsClicked_ThenTheClientIsNotRenamed()
    {
        // Arrange
        var (connection, member, client) = CreateClient();
        await ShowNameDialogAsync(connection);
        await _provider.Find("input[data-testid='client-name-input']").InputAsync(new ChangeEventArgs { Value = "Kitchen" });

        // Act
        _permissions.CannotAdminister.Add(member);
        await _provider.Find("[data-testid='client-name-save']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Null(client.Name);
        Assert.Contains("Your roles do not allow running this.", _provider.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheViewerMayRunTheRenameOperation_ThenTheNameDialogRenamesTheClient()
    {
        // Arrange
        var (connection, _, client) = CreateClient();
        await ShowNameDialogAsync(connection);
        await _provider.Find("input[data-testid='client-name-input']").InputAsync(new ChangeEventArgs { Value = "Kitchen" });

        // Act
        await _provider.Find("[data-testid='client-name-save']").ClickAsync(new MouseEventArgs());

        // Assert
        await _provider.WaitForAssertionAsync(() => Assert.Equal("Kitchen", client.Name));
        await _provider.WaitForAssertionAsync(() => Assert.Empty(_provider.FindAll("[data-testid='client-name-dialog']")));
    }

    private Task ShowMetadataDialogAsync() =>
        _provider.InvokeAsync(() => MetadataEditDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), _graph.Page, "Metadata"));

    private Task ShowNameDialogAsync(ClientConnection connection) =>
        _provider.InvokeAsync(() => ClientNameDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), connection));

    private (ClientConnection Connection, RegisteredSubjectProperty Member, RenamedClient Client) CreateClient()
    {
        var client = new RenamedClient(_graph.Services.GetRequiredService<IInterceptorSubjectContext>());
        var member = client.TryGetMethodMember(nameof(RenamedClient.RenameAsync))!;
        var connection = _clientConnections.Connect(null, new NoCallbacks());
        _clientConnections.SetRenameMemberResolver(resolved => ReferenceEquals(resolved, connection) ? member : null);
        return (connection, member, client);
    }

    private sealed class NoCallbacks : IClientConnectionCallbacks
    {
        public Task ShowNotificationAsync(Notification notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoreClientTokenAsync(string token) => Task.CompletedTask;
    }
}
