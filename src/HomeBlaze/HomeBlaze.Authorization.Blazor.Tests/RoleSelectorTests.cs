using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RoleSelectorTests : BunitContext, IAsyncLifetime
{
    // Twin and TWIN differ only in case, so neither can be used.
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("Kitchen", "Twin", "TWIN");

    private readonly IRenderedComponent<MudPopoverProvider> _popoverProvider;

    private IReadOnlyList<string>? _changedSelection;

    public RoleSelectorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
        _popoverProvider = Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _graph.Dispose();
    }

    [Fact]
    public void WhenNamesDifferInCaseOrAreBlank_ThenChipsShowEachRoleOnceInItsCanonicalCasing()
    {
        // Arrange
        IReadOnlyList<string> selected = ["everything", " ", "kitchen", "Everything", "READER"];

        // Act
        var component = RenderSelector(selected);

        // Assert
        Assert.Equal(["Everything", "Kitchen", "Reader"], GetChipNames(component));
    }

    [Fact]
    public void WhenNamesAreNormalized_ThenCanonicalCasingIsUsedAndDuplicatesAndBlanksAreDropped()
    {
        // Act
        var names = RoleSelector.Normalize(["kitchen", "", "Ghost", "KITCHEN", "ghost", "reader"], ["Reader", "Kitchen"]);

        // Assert
        Assert.Equal(["Kitchen", "Ghost", "Reader"], names);
    }

    [Fact]
    public async Task WhenRolesAreSelected_ThenTheSelectedOnesKeepTheirOrderAndNewOnesAreAppended()
    {
        // Arrange
        var component = RenderSelector([BuiltInRoles.EverythingName, BuiltInRoles.ReaderName]);
        var select = component.FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync(
            [BuiltInRoles.OperatorName, BuiltInRoles.ReaderName, "Kitchen"]));

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, "Kitchen"], _changedSelection);
    }

    [Fact]
    public void WhenARoleIsUnknown_ThenItIsShownAsAnErrorChipWithATooltip()
    {
        // Act
        var component = RenderSelector([BuiltInRoles.ReaderName, "Ghost"]);

        // Assert
        Assert.Contains("mud-chip-color-error", component.Find("[data-testid='role-Ghost']").ClassName);
        Assert.DoesNotContain("mud-chip-color-error", component.Find("[data-testid='role-Reader']").ClassName);
        var tooltip = Assert.Single(component.FindComponents<MudTooltip>());
        Assert.Equal("Unknown role", tooltip.Instance.Text);
    }

    [Fact]
    public void WhenARoleExistsButCannotBeUsed_ThenItIsShownAsAnErrorChipWithTheReason()
    {
        // Act
        var component = RenderSelector(["twin"]);

        // Assert
        Assert.Contains("mud-chip-color-error", component.Find("[data-testid='role-twin']").ClassName);
        var tooltip = Assert.Single(component.FindComponents<MudTooltip>());
        Assert.Equal("The name 'Twin' is used by more than one role, ignoring case.", tooltip.Instance.Text);
    }

    [Fact]
    public async Task WhenTheSelectIsOpened_ThenItOffersTheRolesAndTheUnknownSelectedNames()
    {
        // Arrange
        var component = RenderSelector(["Ghost"]);

        // Act
        var options = await GetOfferedNamesAsync(component);

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "Kitchen", "Ghost"], options);
    }

    [Fact]
    public async Task WhenNamesAreExcluded_ThenTheyAreNotOfferedIgnoringCase()
    {
        // Arrange
        var component = RenderSelector([], parameters => parameters.Add(selector => selector.Excluded, ["kitchen", "READER"]));

        // Act
        var options = await GetOfferedNamesAsync(component);

        // Assert
        Assert.Equal([BuiltInRoles.OperatorName, BuiltInRoles.EverythingName], options);
    }

    [Fact]
    public async Task WhenAChipIsClosed_ThenTheRoleIsRemovedFromTheSelection()
    {
        // Arrange
        var component = RenderSelector([BuiltInRoles.ReaderName, "Ghost", "Kitchen"]);

        // Act
        await component.Find("[data-testid='role-Ghost'] .mud-chip-close-button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, "Kitchen"], _changedSelection);
    }

    [Fact]
    public void WhenAnErrorIsSet_ThenTheErrorTextIsShown()
    {
        // Act
        var component = RenderSelector([], parameters => parameters
            .Add(selector => selector.Label, "Includes")
            .Add(selector => selector.Error, true)
            .Add(selector => selector.ErrorText, "Select a role."));

        // Assert
        Assert.Contains("Includes", component.Markup);
        Assert.Contains("Select a role.", component.Markup);
    }

    [Fact]
    public void WhenATestIdIsGiven_ThenTheChipsUseItAsPrefix()
    {
        // Act
        var component = RenderSelector([BuiltInRoles.ReaderName], parameters => parameters.Add(selector => selector.TestId, "agent-edit-role"));

        // Assert
        Assert.Equal(BuiltInRoles.ReaderName, component.Find("[data-testid='agent-edit-role-Reader']").TextContent.Trim());
    }

    private IRenderedComponent<RoleSelector> RenderSelector(
        IReadOnlyList<string> selected, Action<ComponentParameterCollectionBuilder<RoleSelector>>? configure = null) =>
        Render<RoleSelector>(parameters =>
        {
            parameters
                .Add(selector => selector.Selected, selected)
                .Add(selector => selector.SelectedChanged, (IReadOnlyList<string> names) => _changedSelection = names);
            configure?.Invoke(parameters);
        });

    private static IEnumerable<string> GetChipNames(IRenderedComponent<RoleSelector> component) =>
        component.FindAll("[data-testid^='role-']").Select(chip => chip.TextContent.Trim());

    private async Task<IReadOnlyList<string?>> GetOfferedNamesAsync(IRenderedComponent<RoleSelector> component)
    {
        var select = component.FindComponent<MudSelect<string>>();
        await component.InvokeAsync(select.Instance.OpenMenu);
        return _popoverProvider.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value).ToList();
    }
}
