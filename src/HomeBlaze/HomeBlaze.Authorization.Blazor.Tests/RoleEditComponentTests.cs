using Bunit;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RoleEditComponentTests : BunitContext, IAsyncLifetime
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("Kitchen", "Garden");

    private readonly IRenderedComponent<MudPopoverProvider> _popoverProvider;

    public RoleEditComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_graph.Service);
        Services.AddSingleton(_graph.PathResolver);

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Role), typeof(TestRoot)]);
        Services.AddSingleton(new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider)));

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
    public void WhenTheRoleIsLoaded_ThenItsNameIncludesAndRulesAreShown()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Includes = ["reader"];
        role.Allow = [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read, AuthorizationActions.Execute] }];
        role.Deny = [new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Equal("Kitchen", component.Find("[data-testid='role-edit-name']").TextContent.Trim());
        Assert.Equal(BuiltInRoles.ReaderName, component.Find("[data-testid='role-edit-include-Reader']").TextContent.Trim());

        var allowCard = Assert.Single(GetCards(component, "allow"));
        Assert.Equal(["/Devices/**"], GetChips(allowCard, "rule-paths"));
        Assert.Equal("any", allowCard.QuerySelector("[data-testid='rule-types-any']")?.TextContent.Trim());
        Assert.Equal([true, false, true, false], GetActionStates(component, 0));

        var denyCard = Assert.Single(GetCards(component, "deny"));
        Assert.Equal([MemberTags.Secret], GetChips(denyCard, "rule-tags"));
        Assert.True(component.Instance.IsValid);
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenARuleIsAdded_ThenItHasNoActionAndTheRoleIsInvalidUntilOneIsChecked()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("Kitchen"));
        var validity = new List<bool>();
        component.Instance.IsValidChanged += validity.Add;

        // Act
        await component.Find("[data-testid='role-edit-add-deny']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Single(GetCards(component, "deny"));
        Assert.Empty(GetCards(component, "allow"));
        Assert.False(component.Instance.IsValid);
        Assert.True(component.Instance.IsDirty);
        Assert.Contains("Select at least one action.", component.Markup);

        // Act
        await SetActionAsync(component, 0, "write", true);

        // Assert
        Assert.True(component.Instance.IsValid);
        Assert.Equal([false, true], validity);
    }

    [Fact]
    public async Task WhenTheOnlyActionOfARuleIsUnchecked_ThenTheRoleIsInvalid()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await SetActionAsync(component, 0, "read", false);

        // Assert
        Assert.False(component.Instance.IsValid);
        Assert.Contains("Select at least one action.", component.Markup);
    }

    [Fact]
    public async Task WhenARuleIsRemoved_ThenItsCardIsGoneAndTheOthersKeepTheirRules()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow =
        [
            new PermissionRule { Paths = ["/First"], Actions = [PermissionAction.Read] },
            new PermissionRule { Paths = ["/Second"], Actions = [PermissionAction.Read] }
        ];
        var component = RenderEditor(role);

        // Act
        await GetCards(component, "allow")[0].QuerySelector("[data-testid='rule-remove']")!.ClickAsync(new MouseEventArgs());

        // Assert
        var card = Assert.Single(GetCards(component, "allow"));
        Assert.Equal(["/Second"], GetChips(card, "rule-paths"));
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenAValidPatternIsEntered_ThenItIsAddedAsAChip()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", " /Devices/** ");

        // Assert
        Assert.Equal(["/Devices/**"], GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.Equal(["/Devices/**"], component.Instance.GetEditedRole().Allow[0].Paths.ToArray());
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenAnInvalidPatternIsEntered_ThenItsErrorIsShownOnTheFieldAndItIsNotAdded()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "Devices");

        // Assert
        var field = component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths']");
        Assert.Contains("must start with '/'", field.TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public void WhenALoadedPatternIsInvalid_ThenItIsShownAsAnErrorChipAndTheRoleIsInvalid()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Paths = ["/Devices", "Garden"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths-chip']");
        Assert.DoesNotContain("mud-chip-color-error", chips[0].ClassName);
        Assert.Contains("mud-chip-color-error", chips[1].ClassName);
        var tooltip = Assert.Single(component.FindComponent<RuleCard>().FindComponents<MudTooltip>());
        Assert.Equal("Path pattern 'Garden' must start with '/'.", tooltip.Instance.Text);
        Assert.False(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenATagIsNotAMemberTag_ThenItIsRejected()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-tags", "colour");

        // Assert
        Assert.Contains("Unknown tag", component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-tags']").TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-tags"));
    }

    [Fact]
    public async Task WhenTheTypeFieldIsSearched_ThenItSuggestsTheKnownTypesAndTheTypedText()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var typeInput = component.FindComponents<ChipListInput>().Single(input => input.Instance.TestId == "rule-types");
        var search = typeInput.FindComponent<MudAutocomplete<string>>().Instance.SearchFunc!;

        // Act
        var suggestions = await search("o", CancellationToken.None)!;

        // Assert
        Assert.Equal(["o", nameof(HomeBlaze.Abstractions.IConfigurable), "Role", "TestRoot"], suggestions);
    }

    [Fact]
    public async Task WhenATypeSuggestionIsPicked_ThenItIsAdded()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var typeInput = component.FindComponents<ChipListInput>().Single(input => input.Instance.TestId == "rule-types");
        var autocomplete = typeInput.FindComponent<MudAutocomplete<string>>();

        // Act
        await component.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync("TestRoot"));

        // Assert
        Assert.Equal(["TestRoot"], component.Instance.GetEditedRole().Allow[0].Types.ToArray());
    }

    [Fact]
    public async Task WhenSaved_ThenTheRoleGetsNewListsWithTheEditedRules()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.ReaderName];
        role.Allow = [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read] }];
        var oldIncludes = role.Includes;
        var oldAllow = role.Allow;
        var oldDeny = role.Deny;
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-deny']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 1, "write", true);
        await EnterTextAsync(component, "role-edit-deny-rule-0", "rule-members", "Secret*");

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.NotSame(oldIncludes, role.Includes);
        Assert.NotSame(oldAllow, role.Allow);
        Assert.NotSame(oldDeny, role.Deny);
        Assert.Equal([BuiltInRoles.ReaderName], role.Includes);
        Assert.Equal([new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read] }], role.Allow);
        Assert.Equal([new PermissionRule { Members = ["Secret*"], Actions = [PermissionAction.Write] }], role.Deny);
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenTheRoleIsInvalid_ThenSavingThrowsAndChangesNothing()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        var oldAllow = role.Allow;
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Same(oldAllow, role.Allow);
    }

    [Fact]
    public async Task WhenValidTextIsTypedAndTheFieldLosesFocus_ThenItIsAddedAsAChip()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        var selector = "[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths'] input";
        await component.Find(selector).InputAsync(new ChangeEventArgs { Value = " /Devices/** " });

        // Act
        await component.Find(selector).BlurAsync(new FocusEventArgs());

        // Assert
        Assert.Equal(["/Devices/**"], GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.Equal(["/Devices/**"], component.Instance.GetEditedRole().Allow[0].Paths.ToArray());
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenValidTextIsTypedButNotAdded_ThenSavingIncludesIt()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices");
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-types", "TestDevice");
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-members", "Temperature");

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(
            [new PermissionRule { Paths = ["/Devices"], Types = ["TestDevice"], Members = ["Temperature"], Actions = [PermissionAction.Read] }],
            role.Allow);
    }

    [Fact]
    public async Task WhenInvalidTextIsTypedButNotAdded_ThenTheRoleIsInvalidUntilItIsCleared()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var oldAllow = role.Allow;
        var component = RenderEditor(role);
        var validity = new List<bool>();
        component.Instance.IsValidChanged += validity.Add;

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "Devices");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.False(component.Instance.IsValid);
        Assert.Same(oldAllow, role.Allow);

        // Act
        await TypeTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "");

        // Assert
        Assert.True(component.Instance.IsValid);
        Assert.Equal([false, true], validity);
    }

    [Fact]
    public void WhenEverySelectorOfARuleIsEmpty_ThenTheCardWarnsThatItAppliesToEveryMember()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow =
        [
            new PermissionRule { Actions = [PermissionAction.Read] },
            new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Write] }
        ];
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Write] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var allowWarning = GetCards(component, "allow")[0].QuerySelector("[data-testid='rule-any-member-warning']");
        Assert.Equal("This rule applies to every member.", allowWarning?.TextContent.Trim());
        Assert.Contains("mud-alert-text-error", allowWarning!.ClassName);
        Assert.Null(GetCards(component, "allow")[1].QuerySelector("[data-testid='rule-any-member-warning']"));

        var denyWarning = GetCards(component, "deny")[0].QuerySelector("[data-testid='rule-any-member-warning']");
        Assert.Contains("mud-alert-text-warning", denyWarning!.ClassName);
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenIncludesAreOffered_ThenTheRoleItselfIsNot()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("Kitchen"));
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(select.Instance.OpenMenu);

        // Assert
        var options = _popoverProvider.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value).ToList();
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "Garden"], options);
    }

    [Fact]
    public async Task WhenAnIncludeIsSelected_ThenTheEditedRoleHasItAndIsDirty()
    {
        // Arrange
        var component = RenderEditor(_graph.GetRole("Kitchen"));
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync(["Garden"]));

        // Assert
        Assert.Equal(["Garden"], component.Instance.GetEditedRole().Includes.ToArray());
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public void WhenTheRoleHasAStatus_ThenItIsShownAsAnError()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Includes = ["Ghost"];

        // Act
        var component = RenderEditor(role);

        // Assert
        var alert = component.Find("[data-testid='role-edit-status']");
        Assert.Contains("Ghost", alert.TextContent);
        Assert.Contains("mud-alert-text-error", alert.ClassName);
    }

    [Fact]
    public void WhenTheRoleHasNoStatus_ThenNoAlertIsShown()
    {
        // Act
        var component = RenderEditor(_graph.GetRole("Kitchen"));

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-status']"));
    }

    [Fact]
    public void WhenAgentsUseTheRole_ThenTheirCountIsShown()
    {
        // Arrange
        _graph.Service.SetPrincipalRoles(new object(), ["kitchen"]);
        _graph.Service.SetPrincipalRoles(new object(), ["Kitchen", BuiltInRoles.ReaderName]);

        // Act
        var component = RenderEditor(_graph.GetRole("Kitchen"));

        // Assert
        Assert.Equal("Used by 2 agents", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheRoleIsEdited_ThenTheEditedRoleHasTheUnsavedState()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await SetActionAsync(component, 0, "history", true);

        // Assert
        var editedRole = component.Instance.GetEditedRole();
        Assert.Equal("Kitchen", editedRole.Name);
        Assert.Equal([PermissionAction.Read, AuthorizationActions.History], editedRole.Allow[0].Actions.ToArray());
        Assert.Equal([PermissionAction.Read], role.Allow[0].Actions.ToArray());
    }

    [Fact]
    public async Task WhenTheRolesAreReloadedWhileTheDialogIsOpen_ThenSavingThrowsAndChangesNothing()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        var reloadedRole = new Role();
        _graph.Authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kitchen"] = reloadedRole,
            ["Garden"] = new Role()
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Equal("The roles changed while this dialog was open. Close it and open it again.", exception.Message);
        Assert.Empty(role.Allow);
        Assert.Empty(reloadedRole.Allow);
    }

    [Fact]
    public async Task WhenTheRoleMovesToAnotherAuthorizationWhileTheDialogIsOpen_ThenSavingThrows()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        _graph.Root.Children = new Dictionary<string, IInterceptorSubject>(_graph.Root.Children)
        {
            ["Authorization"] = new Authorization
            {
                Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["Kitchen"] = role }
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Empty(role.Allow);
    }

    [Fact]
    public async Task WhenTheRoleIsRenamedWhileTheDialogIsOpen_ThenTheDialogUsesTheNewName()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        var garden = _graph.GetRole("Garden");
        var component = RenderEditor(role);

        // Act: renamed as the Authorization dialog does, left out once so that it is attached again under its new key.
        _graph.Authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["Garden"] = garden };
        _graph.Authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["Pantry"] = role, ["Garden"] = garden };
        component.Render();

        // Assert
        Assert.Equal("Pantry", component.Find("[data-testid='role-edit-name']").TextContent.Trim());
        Assert.Equal("Pantry", component.Instance.GetEditedRole().Name);
        Assert.Equal(["Pantry"], component.FindComponent<RoleSelector>().Instance.Excluded!);

        // Act
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read] }], role.Allow);
    }

    [Fact]
    public async Task WhenALoadedSelectorListsAValueTwice_ThenTheLaterOneIsAnErrorChipUntilItIsRemoved()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Members = ["TurnOn", "TurnOn"], Tags = ["state", "STATE"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-members-chip']");
        Assert.Equal(2, chips.Count);
        Assert.DoesNotContain("mud-chip-color-error", chips[0].ClassName);
        Assert.Contains("mud-chip-color-error", chips[1].ClassName);
        var tagChips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-tags-chip']");
        Assert.Contains("mud-chip-color-error", tagChips[1].ClassName);
        Assert.All(component.FindComponent<RuleCard>().FindComponents<MudTooltip>(), tooltip => Assert.Equal("Listed twice.", tooltip.Instance.Text));
        Assert.False(component.Instance.IsValid);

        // Act
        await RemoveChipAsync(component, "role-edit-allow-rule-0", "rule-members", 1);
        await RemoveChipAsync(component, "role-edit-allow-rule-0", "rule-tags", 1);

        // Assert
        Assert.Equal(["TurnOn"], component.Instance.GetEditedRole().Allow[0].Members.ToArray());
        Assert.Equal(["state"], component.Instance.GetEditedRole().Allow[0].Tags.ToArray());
        Assert.True(component.Instance.IsValid);
    }

    [Theory]
    [InlineData("rule-members", "Temperature, Set*,,Temperature", new[] { "Temperature", "Set*" })]
    [InlineData("rule-types", "ILight, ISwitch", new[] { "ILight", "ISwitch" })]
    [InlineData("rule-paths", "/Devices/A,/Devices/B", new[] { "/Devices/A", "/Devices/B" })]
    [InlineData("rule-tags", "state, Secret", new[] { "state", "Secret" })]
    public async Task WhenTextWithCommasIsEntered_ThenEachPartIsAddedAsAChip(string input, string text, string[] expectedChips)
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", input, text);

        // Assert
        Assert.Equal(expectedChips, GetChips(GetCards(component, "allow")[0], input));
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public async Task WhenTextWithCommasHasAnInvalidPart_ThenNothingIsAddedAndThePartsErrorIsShown()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", "rule-paths", "/Devices/A, Devices");

        // Assert
        Assert.Contains("Path pattern 'Devices' must start with '/'.",
            component.Find("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-paths']").TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], "rule-paths"));
        Assert.False(component.Instance.IsValid);
    }

    [Theory]
    [InlineData("rule-members", "Turn On", "A member name contains no whitespace.")]
    [InlineData("rule-types", "I Light", "A type name contains no whitespace.")]
    [InlineData("rule-tags", "requires confirmation", "A tag contains no whitespace.")]
    public async Task WhenANameWithWhitespaceIsEntered_ThenItIsRejected(string input, string text, string expectedError)
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-allow-rule-0", input, text);

        // Assert
        Assert.Contains(expectedError, component.Find($"[data-testid='role-edit-allow-rule-0'] [data-testid='{input}']").TextContent);
        Assert.Empty(GetChips(GetCards(component, "allow")[0], input));
        Assert.False(component.Instance.IsValid);
    }

    [Theory]
    [InlineData("Members", " TurnOn", "Has leading or trailing whitespace.")]
    [InlineData("Members", "TurnOn, TurnOff", "Contains a comma, which separates values; remove it and add each part on its own.")]
    [InlineData("Members", "Turn On", "A member name contains no whitespace.")]
    [InlineData("Types", "ILight,ISwitch", "Contains a comma, which separates values; remove it and add each part on its own.")]
    [InlineData("Types", "I Light", "A type name contains no whitespace.")]
    [InlineData("Paths", "/Devices ", "Has leading or trailing whitespace.")]
    [InlineData("Paths", "/Devices/A,/Devices/B", "Contains a comma, which separates values; remove it and add each part on its own.")]
    [InlineData("Tags", "state ", "Has leading or trailing whitespace.")]
    public void WhenALoadedValueWouldBeRejected_ThenItIsAnErrorChipAndTheRoleIsInvalid(string selector, string value, string expectedError)
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        var rule = new PermissionRule { Actions = [PermissionAction.Read] };
        role.Allow = [selector switch
        {
            "Members" => rule with { Members = [value] },
            "Types" => rule with { Types = [value] },
            "Paths" => rule with { Paths = [value] },
            _ => rule with { Tags = [value] }
        }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chip = component.Find($"[data-testid='role-edit-allow-rule-0'] [data-testid='rule-{selector.ToLowerInvariant()}-chip']");
        Assert.Contains("mud-chip-color-error", chip.ClassName);
        var tooltip = Assert.Single(component.FindComponent<RuleCard>().FindComponents<MudTooltip>());
        Assert.Equal(expectedError, tooltip.Instance.Text);
        Assert.False(component.Instance.IsValid);
    }

    [Fact]
    public void WhenATypeIsNotKnown_ThenItsChipWarnsWithoutMakingTheRoleInvalid()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Types = ["TestRoot", "HomeBlaze.Authorization.Role", "Testroot"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-types-chip']");
        Assert.DoesNotContain("mud-chip-color-warning", chips[0].ClassName);
        Assert.DoesNotContain("mud-chip-color-warning", chips[1].ClassName);
        Assert.Contains("mud-chip-color-warning", chips[2].ClassName);
        var tooltip = Assert.Single(component.FindComponent<RuleCard>().FindComponents<MudTooltip>());
        Assert.Equal("No known subject type or interface has this name; check its spelling and case.", tooltip.Instance.Text);
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public void WhenARuleCardIsShown_ThenTheTypesAndMembersSayTheyAreCaseSensitive()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var inputs = component.FindComponents<ChipListInput>();
        Assert.Contains("case-sensitive", inputs.Single(input => input.Instance.TestId == "rule-types").Instance.HelperText);
        Assert.Contains("case-sensitive", inputs.Single(input => input.Instance.TestId == "rule-members").Instance.HelperText);
        Assert.Equal("Set*", inputs.Single(input => input.Instance.TestId == "rule-members").Instance.Placeholder);
    }

    [Fact]
    public void WhenRulesAreShown_ThenEachRemoveButtonNamesItsRule()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }, new PermissionRule { Actions = [PermissionAction.Write] }];
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Write] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Equal(
            ["Remove allow rule 1", "Remove allow rule 2", "Remove deny rule 1"],
            component.FindAll("[data-testid='rule-remove']").Select(button => button.GetAttribute("aria-label")));
    }

    [Fact]
    public async Task WhenALoadedRuleHasAnUnknownAction_ThenItIsAnErrorChipAndTheRoleIsInvalidUntilItIsRemoved()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read, new PermissionAction("run")] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var card = component.FindComponent<RuleCard>();
        var chip = Assert.Single(card.FindComponents<MudChip<string>>());
        Assert.Equal("run", chip.Find(".mud-chip-content").TextContent.Trim());
        Assert.Contains("mud-chip-color-error", chip.Find(".mud-chip").ClassName);
        Assert.Equal("Unknown action", Assert.Single(card.FindComponents<MudTooltip>()).Instance.Text);
        Assert.False(component.Instance.IsValid);

        // Act
        await chip.Find(".mud-chip-close-button").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Equal([PermissionAction.Read], component.Instance.GetEditedRole().Allow[0].Actions.ToArray());
        Assert.True(component.Instance.IsValid);
    }

    [Fact]
    public void WhenLoadedTagsAreInAnotherCase_ThenTheyAreValid()
    {
        // Arrange
        var role = _graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Tags = ["STATE", "Secret"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var chips = component.FindAll("[data-testid='role-edit-allow-rule-0'] [data-testid='rule-tags-chip']");
        Assert.All(chips, chip => Assert.DoesNotContain("mud-chip-color-error", chip.ClassName));
        Assert.True(component.Instance.IsValid);
    }

    private IRenderedComponent<RoleEditComponent> RenderEditor(Role role) =>
        Render<RoleEditComponent>(parameters => parameters.Add(component => component.Subject, role));

    private static IReadOnlyList<AngleSharp.Dom.IElement> GetCards(IRenderedComponent<RoleEditComponent> component, string section) =>
        component.FindAll($"[data-testid^='role-edit-{section}-rule-']");

    private static IReadOnlyList<string> GetChips(AngleSharp.Dom.IElement card, string input) =>
        card.QuerySelectorAll($"[data-testid='{input}-chip']").Select(chip => chip.TextContent.Trim()).ToList();

    // The rule cards in the order of the allow rules followed by the deny rules; the boxes in the order read, write, execute, history.
    private static IReadOnlyList<bool> GetActionStates(IRenderedComponent<RoleEditComponent> component, int cardIndex) =>
        component.FindComponents<RuleCard>()[cardIndex]
            .FindAll("input[type='checkbox'][data-testid^='rule-action-']")
            .Select(box => ((AngleSharp.Html.Dom.IHtmlInputElement)box).IsChecked)
            .ToList();

    private static IReadOnlyList<IRenderedComponent<MudCheckBox<bool>>> GetActionBoxes(IRenderedComponent<RoleEditComponent> component, int cardIndex) =>
        component.FindComponents<RuleCard>()[cardIndex].FindComponents<MudCheckBox<bool>>();

    private static Task SetActionAsync(IRenderedComponent<RoleEditComponent> component, int cardIndex, string action, bool isChecked)
    {
        var box = GetActionBoxes(component, cardIndex).Single(box => box.Instance.Label == action);
        return component.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(isChecked));
    }

    private static Task TypeTextAsync(IRenderedComponent<RoleEditComponent> component, string card, string input, string text) =>
        component.Find($"[data-testid='{card}'] [data-testid='{input}'] input").InputAsync(new ChangeEventArgs { Value = text });

    private static Task RemoveChipAsync(IRenderedComponent<RoleEditComponent> component, string card, string input, int index) =>
        component.FindAll($"[data-testid='{card}'] [data-testid='{input}-chip']")[index]
            .QuerySelector(".mud-chip-close-button")!
            .ClickAsync(new MouseEventArgs());

    private static async Task EnterTextAsync(IRenderedComponent<RoleEditComponent> component, string card, string input, string text)
    {
        var selector = $"[data-testid='{card}'] [data-testid='{input}'] input";
        await component.Find(selector).InputAsync(new ChangeEventArgs { Value = text });
        await component.Find(selector).KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await component.Find(selector).KeyUpAsync(new KeyboardEventArgs { Key = "Enter" });
    }
}
