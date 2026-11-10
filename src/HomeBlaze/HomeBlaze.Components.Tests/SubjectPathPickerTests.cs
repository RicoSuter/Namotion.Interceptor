using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Inputs;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Components.Tests;

public sealed class SubjectPathPickerTests : BunitContext, IAsyncLifetime
{
    private readonly ServiceProvider _serviceProvider = new ServiceCollection().BuildServiceProvider();
    private readonly RootManager _rootManager;
    private readonly IInterceptorSubjectContext _context;
    private readonly TestUserPermissions _permissions = new();

    public SubjectPathPickerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        var context = _context = SubjectContextFactory.Create(new ServiceCollection());
        var typeProvider = new TypeProvider();
        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager?.Root);
        _rootManager = rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _serviceProvider),
            context,
            pathResolver);
        _rootManager.Root = new PathTargetSubject(context);

        Services.AddSingleton(_rootManager);
        Services.AddSingleton(pathResolver);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _rootManager.Dispose();
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public void WhenPropertiesWithoutStateAreNotAsked_ThenOnlyOperationsAndPropertiesWithListedAttributesAreListed()
    {
        // Act
        var component = RenderPicker(listsPropertiesWithoutState: false);

        // Assert
        var items = GetItemNames(component);
        Assert.Contains("Stop", items);
        Assert.Contains("Speed", items);
        Assert.DoesNotContain("IsOn", items);
        Assert.DoesNotContain("Name", items);
    }

    [Fact]
    public void WhenPropertiesWithoutStateAreAsked_ThenTheyAreListedWithTheOperationsAndNoOperationTwice()
    {
        // Act
        var component = RenderPicker(listsPropertiesWithoutState: true);

        // Assert
        var items = GetItemNames(component);
        Assert.Contains("IsOn", items);
        Assert.Contains("Name", items);
        Assert.Contains("Speed", items);
        Assert.Single(items, item => item == "Stop");
    }

    [Fact]
    public void WhenPropertiesWithoutStateAreAskedButCannotBeSelected_ThenTheyAreNotListed()
    {
        // Act
        var component = Render<SubjectPathPicker>(parameters => parameters
            .Add(picker => picker.Value, "/Stop")
            .Add(picker => picker.CanSelectProperty, property => property.Name != nameof(PathTargetSubject.IsOn))
            .Add(picker => picker.ListsPropertiesWithoutState, true));

        // Assert
        var items = GetItemNames(component);
        Assert.Contains("Name", items);
        Assert.DoesNotContain("IsOn", items);
    }

    [Theory]
    [InlineData("/Password")] // a secret configuration property
    [InlineData("/Token")] // a secret that is state as well
    [InlineData("/Credentials")] // a value whose text shows a secret
    public void WhenAPathNamesASecret_ThenItCanBePickedButItsValueIsNotPreviewed(string path)
    {
        // Arrange
        _rootManager.Root = CreateSecretTarget();

        // Act
        var component = Render<SubjectPathPicker>(parameters => parameters
            .Add(picker => picker.Value, path)
            .Add(picker => picker.CanSelectProperty, _ => true)
            .Add(picker => picker.ListsPropertiesWithoutState, true));

        // Assert
        Assert.Contains(path.TrimStart('/'), GetItemNames(component));
        Assert.DoesNotContain("s3cr3t", component.Markup);
        Assert.DoesNotContain("Preview:", component.Markup);
    }

    [Fact]
    public async Task WhenASecretIsSelectedInTheTree_ThenItsPathIsTakenButItsValueIsNotPreviewed()
    {
        // Arrange
        _rootManager.Root = CreateSecretTarget();
        string? selectedPath = null;
        var component = Render<SubjectPathPicker>(parameters => parameters
            .Add(picker => picker.Value, "/Host")
            .Add(picker => picker.ValueChanged, path => selectedPath = path)
            .Add(picker => picker.CanSelectProperty, _ => true)
            .Add(picker => picker.ListsPropertiesWithoutState, true));

        // Act
        await component.FindAll(".mud-treeview-item-content").Single(item => item.TextContent.Trim() == "Password").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal("/Password", selectedPath);
        Assert.DoesNotContain("s3cr3t", component.Markup);
        Assert.DoesNotContain("Preview:", component.Markup);
    }

    [Fact]
    public void WhenAPathNamesAValueThatIsNoSecret_ThenItIsPreviewed()
    {
        // Arrange
        _rootManager.Root = CreateSecretTarget();

        // Act
        var component = Render<SubjectPathPicker>(parameters => parameters
            .Add(picker => picker.Value, "/Host")
            .Add(picker => picker.CanSelectProperty, _ => true)
            .Add(picker => picker.ListsPropertiesWithoutState, true));

        // Assert
        Assert.Contains("Preview: kitchen.local", component.Find(".mud-typography-caption").TextContent);
    }

    [Fact]
    public void WhenTheViewerMayNotReadAMember_ThenThePickerDoesNotListIt()
    {
        // Arrange
        var root = (PathTargetSubject)_rootManager.Root!;
        _permissions.CannotRead.Add(root.TryGetRegisteredSubject()!.TryGetProperty(nameof(PathTargetSubject.Speed))!);

        // Act
        var component = RenderPicker(listsPropertiesWithoutState: true);

        // Assert
        var items = GetItemNames(component);
        Assert.DoesNotContain("Speed", items);
        Assert.Contains("IsOn", items);
    }

    [Fact]
    public void WhenTheViewerMayNotReadTheMemberAPathNames_ThenItsValueIsNotPreviewed()
    {
        // Arrange
        var root = (PathTargetSubject)_rootManager.Root!;
        root.Name = "Kitchen pump";
        _permissions.CannotRead.Add(root.TryGetRegisteredSubject()!.TryGetProperty(nameof(PathTargetSubject.Name))!);

        // Act
        var component = Render<SubjectPathPicker>(parameters => parameters.Add(picker => picker.Value, "/Name"));

        // Assert
        Assert.DoesNotContain("Kitchen pump", component.Markup);
        Assert.DoesNotContain("Preview:", component.Markup);
    }

    [Fact]
    public void WhenTheViewerMayNotSeeTheSubjectOfTheMemberAPathNames_ThenItsValueIsNotPreviewed()
    {
        // Arrange
        var root = (PathTargetSubject)_rootManager.Root!;
        root.Name = "Kitchen pump";
        var shown = Render<SubjectPathPicker>(parameters => parameters.Add(picker => picker.Value, "/Name")).Markup;
        _permissions.CannotShow.Add(root);

        // Act
        var component = Render<SubjectPathPicker>(parameters => parameters.Add(picker => picker.Value, "/Name"));

        // Assert
        Assert.DoesNotContain("Kitchen pump", component.Markup);
        Assert.Contains("Preview: Kitchen pump", shown);
    }

    [Fact]
    public void WhenTheViewerMayNotSeeASubject_ThenThePickerDoesNotListIt()
    {
        // Arrange
        var device = new AccessTestDevice(_context)
        {
            Devices = [new AccessTestRow { Name = "Pump" }, new AccessTestRow { Name = "Fan" }]
        };
        _rootManager.Root = device;
        _permissions.CannotShow.Add(device.Devices[0]);

        // Act
        var component = Render<SubjectPathPicker>(parameters => parameters.Add(picker => picker.Value, "/Devices[1]/Name"));

        // Assert
        var items = GetItemNames(component);
        Assert.DoesNotContain("[0]", items);
        Assert.Contains("[1]", items);
    }

    private SecretTargetSubject CreateSecretTarget() =>
        new(_context)
        {
            Host = "kitchen.local",
            Password = "s3cr3t",
            Token = "s3cr3t",
            Credentials = new SecretCredentials("admin", "s3cr3t")
        };

    // A path below the root expands it, so that its members are listed.
    private IRenderedComponent<SubjectPathPicker> RenderPicker(bool listsPropertiesWithoutState) =>
        Render<SubjectPathPicker>(parameters => parameters
            .Add(picker => picker.Value, "/Stop")
            .Add(picker => picker.CanSelectProperty, _ => true)
            .Add(picker => picker.ListsPropertiesWithoutState, listsPropertiesWithoutState));

    private static List<string> GetItemNames(IRenderedComponent<SubjectPathPicker> component) =>
        component.FindAll(".mud-treeview-item-content").Select(item => item.TextContent.Trim()).ToList();
}
