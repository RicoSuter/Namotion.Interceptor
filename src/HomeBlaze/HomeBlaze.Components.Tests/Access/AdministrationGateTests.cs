using Bunit;
using Excubo.Blazor.Diagrams;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Components;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Editors;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Storage.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// The configuration editor, the create dialog and the layouts change configuration, create and delete subjects only when
/// the viewer may administer the members the change writes, also when the method is called with its control hidden.
/// </summary>
public sealed class AdministrationGateTests : BunitContext, IAsyncLifetime
{
    private const string Refusal = "Your roles do not allow this change.";

    private readonly TestUserPermissions _permissions = new();
    private readonly ServiceProvider _subjectServices;
    private readonly RootManager _rootManager;
    private readonly AdministeredStore _store;

    public AdministrationGateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var context = SubjectContextFactory.Create(new ServiceCollection());
        var typeProvider = new TypeProvider().AddAssembly(typeof(Widget).Assembly).AddAssembly(typeof(AdministrationGateTests).Assembly);
        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager?.Root);

        var subjectServices = new ServiceCollection();
        subjectServices.AddSingleton(context);
        subjectServices.AddSingleton(pathResolver);
        _subjectServices = subjectServices.BuildServiceProvider();

        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        _rootManager = rootManager = new RootManager(
            typeRegistry, new ConfigurableSubjectSerializer(typeProvider, _subjectServices), context, pathResolver);
        _store = new AdministeredStore(context)
        {
            Device = new AdministeredDevice { Host = "old", Label = "Pump" },
            Hosted = new HostedDevice { Host = "old", Label = "Fan" }
        };
        _rootManager.Root = _store;

        Services.AddSingleton(_rootManager);
        Services.AddSingleton(pathResolver);
        Services.AddSingleton(new SubjectComponentRegistry(typeProvider));
        Services.AddSingleton(new SubjectFactory(_subjectServices));
        Services.AddSingleton(new SubjectTypeCatalog(typeRegistry));
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        Services.AddSingleton<IUserPermissions>(_permissions);
        Render<MudPopoverProvider>();
    }

    private AdministeredDevice Device => _store.Device!;

    private HostedDevice Hosted => _store.Hosted!;

    private RegisteredSubjectProperty StoreDeviceMember => GetMember(_store, nameof(AdministeredStore.Device));

    private RegisteredSubjectProperty StoreCreateMember => _store.TryGetMethodMember(nameof(AdministeredStore.CreateAsync))!;

    private IEnumerable<string?> SnackbarMessages => Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(snackbar => snackbar.Message);

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _rootManager.Dispose();
        await _subjectServices.DisposeAsync();
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheChangedMember_ThenSavingTheEditPanelChangesNothing()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(Device, nameof(AdministeredDevice.Host)));
        var panel = RenderEditPanel(Device);
        await ChangeValueAsync(panel, nameof(AdministeredDevice.Host), "new");

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));
        var isApplied = await panel.InvokeAsync(() => panel.Instance.ApplyChangesAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.False(isApplied);
        Assert.Equal("old", Device.Host);
        Assert.Empty(_store.WrittenSubjects);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerAMemberThatIsNotChanged_ThenSavingTheEditPanelChangesNothing()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(Device, nameof(AdministeredDevice.Label)));
        var panel = RenderEditPanel(Device);
        await ChangeValueAsync(panel, nameof(AdministeredDevice.Host), "new");

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));
        var isPersisted = await panel.InvokeAsync(() => panel.Instance.TryPersistAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.False(isPersisted);
        Assert.Equal("old", Device.Host);
        Assert.Empty(_store.WrittenSubjects);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public async Task WhenTheEditPanelHasNoSubject_ThenNothingIsPersisted()
    {
        // Arrange
        var panel = Render<SubjectEditPanel>();

        // Act
        var isPersisted = await panel.InvokeAsync(() => panel.Instance.TryPersistAsync(CancellationToken.None));

        // Assert
        Assert.False(isPersisted);
        Assert.Empty(_store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheConfiguration_ThenSavingTheEditPanelAppliesAndPersistsTheEdits()
    {
        // Arrange
        var panel = RenderEditPanel(Device);
        await ChangeValueAsync(panel, nameof(AdministeredDevice.Host), "new");

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(isSaved);
        Assert.Equal("new", Device.Host);
        Assert.Equal([Device], _store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerAConfigurationMemberOfAHostedEditComponent_ThenItDoesNotSave()
    {
        // Arrange
        _permissions.CannotAdminister.Add(GetMember(Hosted, nameof(HostedDevice.Label)));
        var panel = RenderEditPanel(Hosted);
        var editComponent = panel.FindComponent<HostedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));
        var isApplied = await panel.InvokeAsync(() => panel.Instance.ApplyChangesAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.False(isApplied);
        Assert.Equal(0, editComponent.SaveCount);
        Assert.Equal("old", Hosted.Host);
        Assert.Empty(_store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheConfigurationOfAHostedEditComponent_ThenItSaves()
    {
        // Arrange
        var panel = RenderEditPanel(Hosted);
        var editComponent = panel.FindComponent<HostedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(isSaved);
        Assert.Equal(1, editComponent.SaveCount);
        Assert.Equal("new", Hosted.Host);
        Assert.Equal([Hosted], _store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenAnEditOperationReplacesTheSubject_ThenSavingSucceedsWithoutARefusalOrAHostWrite()
    {
        // Arrange
        var edited = new SelfPersistedDevice { Label = "old" };
        _store.SelfPersisted = edited;
        var panel = RenderEditPanel(edited);
        var editComponent = panel.FindComponent<SelfPersistedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(isSaved);
        Assert.Equal(1, editComponent.SaveCount);
        Assert.NotSame(edited, _store.SelfPersisted);
        Assert.DoesNotContain(Refusal, SnackbarMessages);
        Assert.Contains("Configuration saved.", SnackbarMessages);
        Assert.Empty(_store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheConfigurationOfASubjectWithAnEditOperation_ThenItDoesNotSave()
    {
        // Arrange
        var edited = new SelfPersistedDevice { Label = "old" };
        _store.SelfPersisted = edited;
        _permissions.CannotAdminister.Add(GetMember(edited, nameof(SelfPersistedDevice.Label)));
        var panel = RenderEditPanel(edited);
        var editComponent = panel.FindComponent<SelfPersistedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.Equal(0, editComponent.SaveCount);
        Assert.Same(edited, _store.SelfPersisted);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public async Task WhenTheViewerMayInvokeTheEditOperationOfASubjectWithoutConfiguration_ThenTheEditPanelSavesThroughIt()
    {
        // Arrange
        var edited = new OperationSavedDevice { Label = "old" };
        _store.OperationSaved = edited;
        var panel = RenderEditPanel(edited);
        var editComponent = panel.FindComponent<OperationSavedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(isSaved);
        Assert.Equal(1, editComponent.SaveCount);
        Assert.Equal("new", edited.Label);
        Assert.DoesNotContain(Refusal, SnackbarMessages);
        Assert.Empty(_store.WrittenSubjects);
    }

    [Fact]
    public async Task WhenTheViewerMayNotInvokeTheEditOperationOfASubjectWithoutConfiguration_ThenTheEditPanelDoesNotSave()
    {
        // Arrange
        var edited = new OperationSavedDevice { Label = "old" };
        _store.OperationSaved = edited;
        _permissions.CannotAdminister.Add(GetMember(edited, nameof(OperationSavedDevice.Save)));
        var panel = RenderEditPanel(edited);
        var editComponent = panel.FindComponent<OperationSavedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));
        var isApplied = await panel.InvokeAsync(() => panel.Instance.ApplyChangesAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.False(isApplied);
        Assert.Equal(0, editComponent.SaveCount);
        Assert.Equal("old", edited.Label);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public async Task WhenTheViewerMayInvokeTheEditOperationButNotAdministerTheConfiguration_ThenTheEditPanelDoesNotSave()
    {
        // Arrange
        var edited = new ConfiguredOperationSavedDevice { Label = "old" };
        _store.ConfiguredOperationSaved = edited;
        _permissions.CannotAdminister.Add(GetMember(edited, nameof(ConfiguredOperationSavedDevice.Label)));
        var panel = RenderEditPanel(edited);
        var editComponent = panel.FindComponent<ConfiguredOperationSavedDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));
        var isApplied = await panel.InvokeAsync(() => panel.Instance.ApplyChangesAsync(CancellationToken.None));

        // Assert
        Assert.True(_permissions.CanInvoke(GetMember(edited, "Save")));
        Assert.False(isSaved);
        Assert.False(isApplied);
        Assert.Equal(0, editComponent.SaveCount);
        Assert.Equal("old", edited.Label);
        Assert.Empty(_store.WrittenSubjects);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public async Task WhenAnEditComponentOfASubjectWithoutConfigurationHasNoEditOperation_ThenTheEditPanelDoesNotSave()
    {
        // Arrange
        var edited = new UnconfiguredDevice { Label = "old" };
        _store.Unconfigured = edited;
        var panel = RenderEditPanel(edited);
        var editComponent = panel.FindComponent<UnconfiguredDeviceEditComponent>().Instance;

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));
        var isApplied = await panel.InvokeAsync(() => panel.Instance.ApplyChangesAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.False(isApplied);
        Assert.Equal(0, editComponent.SaveCount);
        Assert.Equal("old", edited.Label);
        Assert.Empty(_store.WrittenSubjects);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public void WhenTheCreateDialogHasNoMember_ThenItsCreateButtonIsDisabled()
    {
        // Arrange
        var provider = Render<MudDialogProvider>();

        // Act
        ShowCreateDialog(provider, new SubjectSetupOptions { Type = typeof(AdministeredDevice) });

        // Assert
        Assert.True(provider.WaitForElement("[data-testid='create-button']").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerTheAdministerMember_ThenTheCreateButtonIsDisabled()
    {
        // Arrange
        var provider = Render<MudDialogProvider>();
        _permissions.CannotAdminister.Add(StoreDeviceMember);

        // Act
        ShowCreateDialog(provider, new SubjectSetupOptions { Type = typeof(AdministeredDevice), AdministerMember = StoreDeviceMember });

        // Assert
        Assert.True(provider.WaitForElement("[data-testid='create-button']").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenTheViewerMayNotInvokeTheInvokeMember_ThenTheCreateButtonIsDisabled()
    {
        // Arrange
        var provider = Render<MudDialogProvider>();
        _permissions.CannotInvoke.Add(StoreCreateMember);

        // Act
        ShowCreateDialog(provider, new SubjectSetupOptions { Type = typeof(AdministeredDevice), InvokeMember = StoreCreateMember });

        // Assert
        Assert.True(provider.WaitForElement("[data-testid='create-button']").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenOneOfBothMembersRefuses_ThenTheCreateButtonIsDisabled()
    {
        // Arrange
        var provider = Render<MudDialogProvider>();
        _permissions.CannotInvoke.Add(StoreCreateMember);

        // Act
        ShowCreateDialog(provider, new SubjectSetupOptions
        {
            Type = typeof(AdministeredDevice),
            InvokeMember = StoreCreateMember,
            AdministerMember = StoreDeviceMember
        });

        // Assert
        Assert.True(provider.WaitForElement("[data-testid='create-button']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenTheViewerMayInvokeTheInvokeMember_ThenCreateReturnsTheSubject()
    {
        // Arrange
        var provider = Render<MudDialogProvider>();
        var dialog = ShowCreateDialog(provider, new SubjectSetupOptions { Type = typeof(AdministeredDevice), InvokeMember = StoreCreateMember });
        provider.WaitForAssertion(() => Assert.False(provider.Find("[data-testid='create-button']").HasAttribute("disabled")));

        // Act
        provider.Find("[data-testid='create-button']").Click();

        // Assert
        var result = await dialog.WaitAsync(TestTimeouts.Default);
        Assert.IsType<AdministeredDevice>(result?.Subject);
    }

    [Fact]
    public void WhenTheViewerMayNoLongerCreateWhenCreateIsClicked_ThenNothingIsCreated()
    {
        // Arrange
        var isAttached = false;
        var provider = Render<MudDialogProvider>();
        var dialog = ShowCreateDialog(provider, new SubjectSetupOptions
        {
            Type = typeof(AdministeredDevice),
            AdministerMember = StoreDeviceMember,
            BeforeSave = _ => isAttached = true
        });
        provider.WaitForAssertion(() => Assert.False(provider.Find("[data-testid='create-button']").HasAttribute("disabled")));

        // Act
        _permissions.CannotAdminister.Add(StoreDeviceMember);
        provider.Find("[data-testid='create-button']").Click();

        // Assert
        Assert.False(isAttached);
        Assert.False(dialog.IsCompleted);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public void WhenTheViewerMayNoLongerCreateRightBeforeTheSubjectIsAttached_ThenItIsNotAttached()
    {
        // Arrange
        var checksSinceClick = -1;
        var isAttached = false;
        var gate = StoreDeviceMember;
        var provider = Render<MudDialogProvider>();
        var dialog = ShowCreateDialog(provider, new SubjectSetupOptions
        {
            Type = typeof(AdministeredDevice),
            AdministerMember = gate,
            BeforeSave = _ => isAttached = true,
            RevertBeforeSave = _ => { }
        });
        provider.WaitForAssertion(() => Assert.False(provider.Find("[data-testid='create-button']").HasAttribute("disabled")));

        // The check when Create is clicked passes; the one right before the subject is attached refuses.
        _permissions.AdministerChecking = member =>
        {
            if (ReferenceEquals(member, gate) && checksSinceClick >= 0 && checksSinceClick++ == 1)
            {
                _permissions.CannotAdminister.Add(gate);
            }
        };

        // Act
        checksSinceClick = 0;
        provider.Find("[data-testid='create-button']").Click();

        // Assert
        Assert.True(checksSinceClick >= 2);
        Assert.False(isAttached);
        Assert.False(dialog.IsCompleted);
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public void WhenPersistingTheAttachedSubjectIsRefused_ThenItIsDetachedAndTheDialogStaysOpen()
    {
        // Arrange
        var device = Device;
        var provider = Render<MudDialogProvider>();
        var dialog = ShowCreateDialog(provider, new SubjectSetupOptions
        {
            Type = typeof(AdministeredDevice),
            AdministerMember = StoreDeviceMember,
            BeforeSave = subject =>
            {
                _store.Device = (AdministeredDevice)subject;
                _permissions.CannotAdminister.Add(GetMember(subject, nameof(AdministeredDevice.Label)));
            },
            RevertBeforeSave = _ => _store.Device = device
        });
        provider.WaitForAssertion(() => Assert.False(provider.Find("[data-testid='create-button']").HasAttribute("disabled")));

        // Act
        provider.Find("[data-testid='create-button']").Click();

        // Assert
        Assert.Same(device, _store.Device);
        Assert.Empty(_store.WrittenSubjects);
        Assert.False(dialog.IsCompleted);
        Assert.Contains(Refusal, SnackbarMessages);
        Assert.Contains("The subject was not created.", SnackbarMessages);
    }

    [Fact]
    public async Task WhenTheViewerMayCreate_ThenCreateAttachesTheSubject()
    {
        // Arrange
        IInterceptorSubject? attached = null;
        var provider = Render<MudDialogProvider>();
        var dialog = ShowCreateDialog(provider, new SubjectSetupOptions
        {
            Type = typeof(AdministeredDevice),
            AdministerMember = StoreDeviceMember,
            BeforeSave = subject => attached = subject
        });
        provider.WaitForAssertion(() => Assert.False(provider.Find("[data-testid='create-button']").HasAttribute("disabled")));

        // Act
        provider.Find("[data-testid='create-button']").Click();

        // Assert
        var result = await dialog.WaitAsync(TestTimeouts.Default);
        Assert.IsType<AdministeredDevice>(attached);
        Assert.Same(attached, result?.Subject);
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerTheCellsOfAGrid_ThenItShowsNoDeleteAndNoAddButton()
    {
        // Arrange
        var grid = CreateGrid();
        _permissions.CannotAdminister.Add(GetMember(grid, nameof(GridLayout.Cells)));

        // Act
        var widget = RenderEditedLayout<GridLayoutWidget>(grid);

        // Assert
        Assert.Empty(widget.FindAll("[data-testid='layout-item-delete-button']"));
        Assert.Empty(widget.FindAll(".grid-cell.empty"));
    }

    [Fact]
    public void WhenTheViewerMayNoLongerAdministerTheCellsWhenDeleteIsClicked_ThenTheCellIsKept()
    {
        // Arrange
        var grid = CreateGrid();
        var widget = RenderEditedLayout<GridLayoutWidget>(grid);
        var cell = Assert.Single(grid.Cells);

        // Act
        _permissions.CannotAdminister.Add(GetMember(grid, nameof(GridLayout.Cells)));
        widget.Find("[data-testid='layout-item-delete-button']").Click();

        // Assert
        Assert.Same(cell, Assert.Single(grid.Cells));
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public void WhenTheViewerMayAdministerTheCellsOfAGrid_ThenItShowsTheDeleteAndAddButtons()
    {
        // Arrange
        var grid = CreateGrid();

        // Act
        var widget = RenderEditedLayout<GridLayoutWidget>(grid);

        // Assert
        Assert.Single(widget.FindAll("[data-testid='layout-item-delete-button']"));
        Assert.NotEmpty(widget.FindAll(".grid-cell.empty"));
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerTheNodesOfACanvas_ThenItShowsNoDeleteButton()
    {
        // Arrange
        var canvas = CreateCanvas();
        _permissions.CannotAdminister.Add(GetMember(canvas, nameof(CanvasLayout.Nodes)));

        // Act
        var widget = RenderEditedLayout<CanvasLayoutWidget>(canvas);

        // Assert
        Assert.Empty(widget.FindAll("[data-testid='layout-item-delete-button']"));
    }

    [Fact]
    public void WhenTheViewerMayNoLongerAdministerTheNodesWhenDeleteIsClicked_ThenTheNodeIsKept()
    {
        // Arrange
        var canvas = CreateCanvas();
        var widget = RenderEditedLayout<CanvasLayoutWidget>(canvas);
        var node = Assert.Single(canvas.Nodes);

        // Act
        _permissions.CannotAdminister.Add(GetMember(canvas, nameof(CanvasLayout.Nodes)));
        widget.Find("[data-testid='layout-item-delete-button']").Click();

        // Assert
        Assert.Same(node, Assert.Single(canvas.Nodes));
        Assert.Contains(Refusal, SnackbarMessages);
    }

    [Fact]
    public void WhenTheViewerMayAdministerTheNodesOfACanvas_ThenItShowsTheDeleteButton()
    {
        // Arrange
        var canvas = CreateCanvas();

        // Act
        var widget = RenderEditedLayout<CanvasLayoutWidget>(canvas);

        // Assert
        Assert.Single(widget.FindAll("[data-testid='layout-item-delete-button']"));
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    private IRenderedComponent<SubjectEditPanel> RenderEditPanel(IInterceptorSubject subject) =>
        Render<SubjectEditPanel>(parameters => parameters.Add(panel => panel.Subject, subject));

    private static async Task ChangeValueAsync(IRenderedComponent<SubjectEditPanel> panel, string propertyName, object? value)
    {
        var editor = panel.FindComponents<PropertyEditor>().Single(component => component.Instance.Property.Name == propertyName);
        await panel.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync(value));
    }

    private Task<CreateSubjectResult?> ShowCreateDialog(IRenderedComponent<MudDialogProvider> provider, SubjectSetupOptions options)
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        Task<CreateSubjectResult?>? result = null;
        provider.InvokeAsync(() => result = SubjectSetupDialog.ShowAsync(dialogService, options));
        return result!;
    }

    private static void CloseDialogs(IRenderedComponent<MudDialogProvider> provider) =>
        provider.Find("[data-testid='cancel-button']").Click();

    private GridLayout CreateGrid()
    {
        var factory = Services.GetRequiredService<SubjectFactory>();
        var grid = factory.CreateSubject<GridLayout>();
        var cell = factory.CreateSubject<GridCell>();
        cell.Row = 0;
        cell.Column = 0;
        grid.Rows = 1;
        grid.Columns = 2;
        grid.Cells = [cell];
        return grid;
    }

    private CanvasLayout CreateCanvas()
    {
        // The diagram measures itself through its script after it rendered, which a test has not.
        ComponentFactories.Add<Diagram>(() => new UnmeasuredDiagram());
        var factory = Services.GetRequiredService<SubjectFactory>();
        var canvas = factory.CreateSubject<CanvasLayout>();
        var node = factory.CreateSubject<CanvasNode>();
        node.Width = 100;
        node.Height = 100;
        canvas.Nodes = [node];
        return canvas;
    }

    private IRenderedComponent<TWidget> RenderEditedLayout<TWidget>(IInterceptorSubject layout)
        where TWidget : IComponent, ISubjectComponent =>
        Render<CascadingValue<bool>>(parameters => parameters
                .Add(cascade => cascade.Name, "IsEditing")
                .Add(cascade => cascade.Value, true)
                .AddChildContent<TWidget>(widget => widget.Add(component => component.Subject, layout)))
            .FindComponent<TWidget>();

    private sealed class UnmeasuredDiagram : Diagram
    {
        protected override Task OnAfterRenderAsync(bool first_render) => Task.CompletedTask;
    }
}
