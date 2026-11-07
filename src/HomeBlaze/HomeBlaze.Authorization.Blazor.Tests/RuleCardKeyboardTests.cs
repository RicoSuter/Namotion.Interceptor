using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>Tests of the keyboard paths of the chip fields of a rule card, rendered in the role editor.</summary>
public sealed class RuleCardKeyboardTests : RoleEditorTestContext
{
    private const string Card = "role-edit-allow-rule-0";

    [Fact]
    public void WhenChipsAreShown_ThenEachHasARemoveButtonTheKeyboardReachesNamedAfterItsValue()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.ReaderName];
        role.Allow = [new PermissionRule { Members = ["TurnOn"], Tags = ["state"], Actions = [PermissionAction.Read, new PermissionAction("run")] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var removeButtons = component.FindAll(".mud-chip .mud-chip-close-button");
        Assert.Equal(
            ["Remove Reader", "Remove TurnOn", "Remove state", "Remove run"],
            removeButtons.Select(button => button.GetAttribute("aria-label")));
        Assert.All(removeButtons, button => Assert.Null(button.GetAttribute("tabindex")));
    }

    [Fact]
    public async Task WhenTheRemoveButtonOfAChipIsPressed_ThenItsValueIsRemoved()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Members = ["TurnOn", "TurnOff"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await component.Find($"[data-testid='{Card}'] button[aria-label='Remove TurnOn']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal(["TurnOff"], component.Instance.GetEditedRole().Allow[0].Members.ToArray());
    }

    [Theory]
    [InlineData("rule-members")]
    [InlineData("rule-types")]
    public async Task WhenBackspaceIsPressedInTheEmptyField_ThenTheLastChipIsRemoved(string input)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["TestRoot", "Role"], Members = ["TurnOn", "TurnOff"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await FindInput(component, input).KeyDownAsync(new KeyboardEventArgs { Key = "Backspace" });

        // Assert
        var rule = component.Instance.GetEditedRole().Allow[0];
        Assert.Equal(input == "rule-members" ? ["TurnOn"] : ["TestRoot"], input == "rule-members" ? rule.Members.ToArray() : rule.Types.ToArray());
    }

    [Theory]
    [InlineData("rule-members")]
    [InlineData("rule-types")]
    public async Task WhenBackspaceIsPressedInAFieldWithText_ThenNoChipIsRemoved(string input)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["TestRoot"], Members = ["TurnOn"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await FindInput(component, input).InputAsync(new ChangeEventArgs { Value = "Te" });

        // Act
        await FindInput(component, input).KeyDownAsync(new KeyboardEventArgs { Key = "Backspace" });

        // Assert
        var rule = component.Instance.GetEditedRole().Allow[0];
        Assert.Equal(["TestRoot"], rule.Types.ToArray());
        Assert.Equal(["TurnOn"], rule.Members.ToArray());
    }

    [Theory]
    [InlineData("rule-members")]
    [InlineData("rule-types")]
    public async Task WhenBackspaceIsHeldDownInTheEmptyField_ThenOnlyTheLastChipIsRemoved(string input)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["TestRoot", "Role", "TestDevice"], Members = ["TurnOn", "TurnOff", "Toggle"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await FindInput(component, input).KeyDownAsync(new KeyboardEventArgs { Key = "Backspace" });
        await FindInput(component, input).KeyDownAsync(new KeyboardEventArgs { Key = "Backspace", Repeat = true });
        await FindInput(component, input).KeyDownAsync(new KeyboardEventArgs { Key = "Backspace", Repeat = true });

        // Assert
        var rule = component.Instance.GetEditedRole().Allow[0];
        Assert.Equal(input == "rule-members" ? ["TurnOn", "TurnOff"] : ["TestRoot", "Role"], input == "rule-members" ? rule.Members.ToArray() : rule.Types.ToArray());
    }

    [Theory]
    [InlineData("rule-members")]
    [InlineData("rule-types")]
    public async Task WhenTheRemoveButtonOfTheLastChipIsPressed_ThenTheFieldGetsTheFocus(string input)
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["TestRoot", "Role"], Members = ["TurnOn", "TurnOff"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await RemoveChipAsync(component, Card, input, 1);

        // Assert
        var focused = (ElementReference)JSInterop.VerifyFocusAsyncInvoke().Arguments[0]!;
        Assert.Equal(FindInput(component, input).GetAttribute("blazor:elementReference"), focused.Id);
    }

    [Fact]
    public async Task WhenTheRemoveButtonOfAChipBeforeTheLastIsPressed_ThenTheFocusIsNotMoved()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Members = ["TurnOn", "TurnOff"], Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await RemoveChipAsync(component, Card, "rule-members", 0);

        // Assert
        Assert.Equal(["TurnOff"], component.Instance.GetEditedRole().Allow[0].Members.ToArray());
        Assert.Empty(JSInterop.Invocations["Blazor._internal.domWrapper.focus"]);
    }

    [Fact]
    public void WhenAPathCanBePicked_ThenItsBrowseButtonIsNextToTheFieldWhereTheKeyboardReachesIt()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var browse = component.Find($"[data-testid='{Card}'] [data-testid='rule-paths-browse']");
        Assert.Equal("Browse Paths", browse.GetAttribute("aria-label"));
        Assert.Null(browse.GetAttribute("tabindex"));
        Assert.Empty(component.FindAll($"[data-testid='{Card}'] [data-testid='rule-paths'] .mud-input-adornment button"));
    }

    [Fact]
    public async Task WhenKeysChangeNothingShown_ThenTheFieldDoesNotRenderAndATypedKeyRendersItAtMostOnce()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        // The Add button renders again exactly when its field does.
        var addButton = component.FindComponents<ChipListInput>()
            .Single(input => input.Instance.TestId == "rule-types")
            .FindComponent<MudButton>();
        var input = FindInput(component, "rule-types");
        var renders = addButton.RenderCount;

        // Act: the first character enables Add, the second changes nothing else shown.
        await input.KeyDownAsync(new KeyboardEventArgs { Key = "T" });
        await input.InputAsync(new ChangeEventArgs { Value = "T" });
        await input.KeyUpAsync(new KeyboardEventArgs { Key = "T" });
        var rendersOfFirstKey = addButton.RenderCount - renders;
        await input.FocusInAsync(new FocusEventArgs());
        await input.KeyDownAsync(new KeyboardEventArgs { Key = "e" });
        await input.InputAsync(new ChangeEventArgs { Value = "Te" });
        await input.KeyUpAsync(new KeyboardEventArgs { Key = "e" });

        // Assert
        Assert.Equal(1, rendersOfFirstKey);
        Assert.Equal(renders + 1, addButton.RenderCount);
    }

    [Fact]
    public async Task WhenEnterIsPressedOnTypedText_ThenTheTypedTextIsAddedRatherThanAMatchingSuggestion()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await TypeUntilSuggestionsOpenAsync(component, "Rol");

        // Act
        await PressAsync(component, "Enter");

        // Assert
        Assert.Equal(["Rol"], component.Instance.GetEditedRole().Allow[0].Types.ToArray());
        Assert.True(string.IsNullOrEmpty(FindInput(component, "rule-types").GetAttribute("value")));
    }

    [Fact]
    public async Task WhenASuggestionIsChosenWithTheArrowKeysAndEnterIsPressed_ThenTheSuggestionIsAdded()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await TypeUntilSuggestionsOpenAsync(component, "Rol");

        // Act: the typed text is the first suggestion, the role type the second.
        await PressAsync(component, "ArrowDown");
        await PressAsync(component, "Enter");

        // Assert
        Assert.Equal(["Role"], component.Instance.GetEditedRole().Allow[0].Types.ToArray());
    }

    [Fact]
    public async Task WhenAChosenSuggestionIsDismissedWithEscape_ThenEnterAddsTheTypedText()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await TypeUntilSuggestionsOpenAsync(component, "Rol");

        // Act
        await PressAsync(component, "ArrowDown");
        await PressAsync(component, "Escape");
        await PressAsync(component, "Enter");

        // Assert
        Assert.Equal(["Rol"], component.Instance.GetEditedRole().Allow[0].Types.ToArray());
    }

    [Fact]
    public async Task WhenTheFieldIsLeftWithItsSuggestionsOpen_ThenTheTypedTextIsAddedOnceTheyClose()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var autocomplete = await TypeUntilSuggestionsOpenAsync(component, "Rol");
        await FindInput(component, "rule-types").BlurAsync(new FocusEventArgs());
        var typesWhileOpen = component.Instance.GetEditedRole().Allow[0].Types.ToArray();

        // Act
        await component.InvokeAsync(autocomplete.Instance.CloseMenuAsync);

        // Assert
        Assert.Empty(typesWhileOpen);
        Assert.Equal(["Rol"], component.Instance.GetEditedRole().Allow[0].Types.ToArray());
    }

    private static AngleSharp.Dom.IElement FindInput(IRenderedComponent<RoleEditComponent> component, string input) =>
        component.Find($"[data-testid='{Card}'] [data-testid='{input}'] input");

    private static async Task<IRenderedComponent<MudAutocomplete<string>>> TypeUntilSuggestionsOpenAsync(
        IRenderedComponent<RoleEditComponent> component, string text)
    {
        var autocomplete = component.FindComponents<ChipListInput>()
            .Single(input => input.Instance.TestId == "rule-types")
            .FindComponent<MudAutocomplete<string>>();
        await FindInput(component, "rule-types").InputAsync(new ChangeEventArgs { Value = text });
        await component.WaitForAssertionAsync(() => Assert.True(autocomplete.Instance.Open), TimeSpan.FromSeconds(10));
        return autocomplete;
    }

    private static async Task PressAsync(IRenderedComponent<RoleEditComponent> component, string key)
    {
        await FindInput(component, "rule-types").KeyDownAsync(new KeyboardEventArgs { Key = key });
        await FindInput(component, "rule-types").KeyUpAsync(new KeyboardEventArgs { Key = key });
    }
}
