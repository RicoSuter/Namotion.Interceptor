using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

/// <summary>The history dialog and the history link of value widgets show only the history the viewer may read.</summary>
public sealed class HistoryAccessTests : BunitContext, IAsyncLifetime
{
    private const string TemperaturePath = "/" + nameof(ChartAccessRoot.Temperature);

    private readonly TestUserPermissions _permissions = new();
    private readonly ChartAccessRoot _root;
    private readonly TestHistoryStore _history;
    private readonly SubjectPathResolver _resolver;
    private readonly ISubjectRegistry _registry;
    private IRenderedComponent<MudDialogProvider> _provider = null!;

    public HistoryAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var context = SubjectContextFactory.Create(new ServiceCollection());
        var now = DateTimeOffset.UtcNow;
        _history = new TestHistoryStore(new HistoryCoverage(now.AddDays(-2), now.AddDays(1)))
            .Add(TemperaturePath, now.AddHours(-2), 20)
            .Add(TemperaturePath, now.AddHours(-1), 22);
        _root = new ChartAccessRoot(context) { Temperature = 21, Store = new ChartStoreSubject(_history) };
        _resolver = new SubjectPathResolver(() => _root);
        _registry = context.GetService<ISubjectRegistry>();

        Services.AddSingleton(context);
        Services.AddSingleton(_resolver);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync()
    {
        _provider = Render<MudDialogProvider>();
        return Task.CompletedTask;
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private RegisteredSubjectProperty Temperature => _root.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChartAccessRoot.Temperature))!;

    private IDialogService DialogService => Services.GetRequiredService<IDialogService>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheHistoryDialogIsOpenedForAPropertyTheViewerMayNotRead_ThenItIsNotShown(bool refusesHistoryOnly)
    {
        // Arrange
        (refusesHistoryOnly ? _permissions.CannotReadHistory : _permissions.CannotRead).Add(Temperature);

        // Act
        var dialog = await _provider.InvokeAsync(() => PropertyHistoryDialog.ShowAsync(
            DialogService, _registry, _resolver, _permissions, Temperature, "Temperature"));

        // Assert
        Assert.Null(dialog);
        Assert.Empty(_provider.FindAll("[data-testid='property-history-dialog']"));
        Assert.Empty(_history.ReceivedQueries);
    }

    [Fact]
    public async Task WhenTheHistoryDialogIsOpenedForAPropertyTheViewerMayRead_ThenItShowsItsHistory()
    {
        // Act
        var dialog = await _provider.InvokeAsync(() => PropertyHistoryDialog.ShowAsync(
            DialogService, _registry, _resolver, _permissions, Temperature, "Temperature"));

        // Assert
        Assert.NotNull(dialog);
        _provider.WaitForAssertion(() => Assert.NotEmpty(_history.ReceivedQueries));
        Assert.Single(_provider.FindAll("[data-testid='property-history-dialog']"));
    }

    [Fact]
    public async Task WhenTheHistoryDialogIsShownDirectlyForAPathTheViewerMayNotRead_ThenItQueriesNothing()
    {
        // Arrange
        _permissions.CannotReadHistory.Add(Temperature);
        var parameters = new DialogParameters<PropertyHistoryDialog>
        {
            { dialog => dialog.Registry, _registry },
            { dialog => dialog.PropertyPath, TemperaturePath },
            { dialog => dialog.DisplayName, "Temperature" },
            { dialog => dialog.PropertyType, typeof(double) }
        };

        // Act
        await _provider.InvokeAsync(() => DialogService.ShowAsync<PropertyHistoryDialog>("History", parameters));

        // Assert
        _provider.WaitForAssertion(() => Assert.Contains("No data in this range.", _provider.Markup));
        Assert.Empty(_history.ReceivedQueries);
    }

    [Fact]
    public void WhenTheViewerMayNotReadTheHistoryOfAProperty_ThenTheViewerOfValueWidgetsCannotShowIt()
    {
        // Arrange
        var viewer = new PropertyHistoryViewer(DialogService, _resolver, ((IInterceptorSubject)_root).Context, _permissions);
        var canShowBefore = viewer.CanShow(Temperature);
        _permissions.CannotReadHistory.Add(Temperature);

        // Act
        var canShow = viewer.CanShow(Temperature);

        // Assert
        Assert.True(canShowBefore);
        Assert.False(canShow);
    }
}
