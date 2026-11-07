using Bunit;
using HomeBlaze.Authorization.Tests;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Tracking;
using System.Reactive.Concurrency;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RoleEditComponentTests : RoleEditorTestContext
{
    [Fact]
    public void WhenTheRoleIsLoaded_ThenItsIncludesAndRulesAreShownWithoutANameLine()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["reader"];
        role.Allow = [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read, AuthorizationActions.Execute] }];
        role.Deny = [new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var usedBy = component.Find(".role-edit").FirstElementChild;
        Assert.Equal("role-edit-used-by", usedBy?.GetAttribute("data-testid"));
        Assert.Contains("role-edit-tabs", usedBy?.NextElementSibling?.ClassName);
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
    public void WhenTheRoleIsLoaded_ThenTheBuiltInRolesAndTheSystemRulesAreDescribedWithTheIncludes()
    {
        // Act
        var component = RenderEditor(Graph.GetRole("Kitchen"));

        // Assert
        Assert.Contains("The system rules apply to every role.", component.Find("[data-testid='role-edit-builtin-roles']").TextContent);
        Assert.Equal("Reader: Reads every member and its history and executes queries.",
            component.Find("[data-testid='role-edit-builtin-role-Reader']").TextContent.Trim());
        Assert.Equal("Operator: Includes Reader, writes every member and executes operations.",
            component.Find("[data-testid='role-edit-builtin-role-Operator']").TextContent.Trim());
        Assert.Equal("Everything: Allows every action on every member.",
            component.Find("[data-testid='role-edit-builtin-role-Everything']").TextContent.Trim());
    }

    [Fact]
    public void WhenTheRoleIsLoaded_ThenTheTabsAreRolesAllowAndDenyWithTheirCountsAndPreview()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow =
        [
            new PermissionRule { Actions = [PermissionAction.Read] },
            new PermissionRule { Actions = [PermissionAction.Write] }
        ];
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Equal("Roles", component.Find("[data-testid='role-edit-tab-roles']").TextContent.Trim());
        Assert.Equal("Allow (2)", component.Find("[data-testid='role-edit-tab-allow']").TextContent.Trim());
        Assert.Equal("Deny (1)", component.Find("[data-testid='role-edit-tab-deny']").TextContent.Trim());
        Assert.Equal("Preview", component.Find("[data-testid='role-edit-tab-preview']").TextContent.Trim());
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-allow-error']"));
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-deny-error']"));
    }

    [Fact]
    public void WhenAnIncludedRoleCannotBeUsed_ThenOnlyTheRolesTabShowsTheErrorMarker()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.ReaderName, "Ghost"];

        // Act
        var component = RenderEditor(role);

        // Assert
        var marker = component.Find("[data-testid='role-edit-tab-roles-error']");
        Assert.Equal("Includes a role that cannot be used", marker.GetAttribute("aria-label"));
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-allow-error']"));
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-deny-error']"));
    }

    [Fact]
    public async Task WhenAMissingIncludedRoleIsAddedWhileTheDialogIsOpen_ThenTheRolesTabMarkerAndTheErrorChipGo()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["Pantry"];
        var component = RenderEditor(role);

        // Act
        Graph.Authorization.Roles = new Dictionary<string, Role>(Graph.Authorization.Roles) { ["Pantry"] = new Role() };

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Empty(component.FindAll("[data-testid='role-edit-tab-roles-error']")),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("mud-chip-color-error", component.Find("[data-testid='role-edit-include-Pantry']").ClassName);
    }

    [Fact]
    public async Task WhenARuleBecomesInvalid_ThenOnlyTheTabOfItsListShowsTheErrorMarker()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await SetActionAsync(component, 0, "read", false);

        // Assert
        Assert.Single(component.FindAll("[data-testid='role-edit-tab-allow-error']"));
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-deny-error']"));
    }

    [Fact]
    public void WhenAListHasNoRules_ThenItsTabSaysSo()
    {
        // Act
        var component = RenderEditor(Graph.GetRole("Kitchen"));

        // Assert
        Assert.Equal("No allow rules yet", component.Find("[data-testid='role-edit-allow-empty']").TextContent.Trim());
        Assert.Equal("No deny rules yet", component.Find("[data-testid='role-edit-deny-empty']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenARuleIsAddedToAnEmptyList_ThenOnlyItsTabNoLongerSaysItHasNoRules()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("Kitchen"));

        // Act
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-allow-empty']"));
        Assert.Single(component.FindAll("[data-testid='role-edit-deny-empty']"));
        Assert.Equal("Allow (1)", component.Find("[data-testid='role-edit-tab-allow']").TextContent.Trim());
        Assert.Single(component.FindAll("[data-testid='role-edit-tab-allow-error']"));
        Assert.Empty(component.FindAll("[data-testid='role-edit-tab-deny-error']"));
    }

    [Fact]
    public async Task WhenARuleIsAdded_ThenItHasNoActionAndTheRoleIsInvalidUntilOneIsChecked()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("Kitchen"));
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
        var role = Graph.GetRole("Kitchen");
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
        var role = Graph.GetRole("Kitchen");
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
    public async Task WhenSaved_ThenTheRoleGetsNewListsWithTheEditedRules()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
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
        var role = Graph.GetRole("Kitchen");
        var oldAllow = role.Allow;
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Same(oldAllow, role.Allow);
    }

    [Fact]
    public void WhenARuleAllowsExecuteWithoutRead_ThenTheRoleAboveTheTabsAndTheRuleSayTheMethodsCannotBeUsed()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow =
        [
            new PermissionRule { Paths = ["/Devices/**"], Actions = [AuthorizationActions.Execute] },
            new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] }
        ];

        // Act
        var component = RenderEditor(role);

        // Assert
        var roleHint = Assert.Single(component.FindAll("[data-testid='role-edit-execute-without-read']")).TextContent;
        Assert.Null(component.Find(".role-edit-tabs").QuerySelector("[data-testid='role-edit-execute-without-read']"));
        Assert.Contains("Execute is allowed without read on 2 methods", roleHint);
        Assert.Contains("such as /Devices/Child/Start,", roleHint);
        Assert.Contains("listed and run only when it is readable", roleHint);
        var cardHints = component.FindAll("[data-testid='rule-execute-without-read-hint']");
        var cardHint = Assert.Single(cardHints);
        Assert.Contains("such as /Devices/Child/Start,", cardHint.TextContent);
        Assert.NotNull(GetCards(component, "allow")[0].QuerySelector("[data-testid='rule-execute-without-read-hint']"));
    }

    [Fact]
    public void WhenAnIncludedRoleAllowsExecuteWithoutRead_ThenOnlyTheRoleSaysTheMethodsCannotBeUsed()
    {
        // Arrange
        Graph.GetRole("Garden").Allow = [new PermissionRule { Paths = ["/Devices/**"], Actions = [AuthorizationActions.Execute] }];
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["Garden"];
        role.Allow = [new PermissionRule { Members = ["Temperature"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var roleHint = Assert.Single(component.FindAll("[data-testid='role-edit-execute-without-read']")).TextContent;
        Assert.Null(component.Find(".role-edit-tabs").QuerySelector("[data-testid='role-edit-execute-without-read']"));
        Assert.Contains("Execute is allowed without read on 2 methods", roleHint);
        Assert.Empty(component.FindAll("[data-testid='rule-execute-without-read-hint']"));
    }

    [Fact]
    public void WhenTheRoleDeniesReadOnAMethodAnIncludedBuiltInRoleAllowsToExecute_ThenTheRoleSaysItCannotBeUsed()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.OperatorName];
        role.Deny = [new PermissionRule { Paths = ["/Devices/Child/**"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var roleHint = component.Find("[data-testid='role-edit-execute-without-read']").TextContent;
        Assert.Contains("on 1 method, such as /Devices/Child/Start,", roleHint);
    }

    [Fact]
    public void WhenAnotherRoleAllowsReadingTheMethods_ThenExecuteWithoutReadIsNoHint()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = [BuiltInRoles.ReaderName];
        role.Allow = [new PermissionRule { Paths = ["/Devices/**"], Actions = [AuthorizationActions.Execute] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-execute-without-read']"));
        Assert.Empty(component.FindAll("[data-testid='rule-execute-without-read-hint']"));
    }

    [Fact]
    public void WhenADenyTakesReadAwayFromAnExecutableMethod_ThenOnlyTheRoleSaysItCannotBeUsed()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Actions = [PermissionAction.Read, AuthorizationActions.Execute] }];
        role.Deny = [new PermissionRule { Paths = ["/Devices/Child/**"], Actions = [PermissionAction.Read] }];

        // Act
        var component = RenderEditor(role);

        // Assert
        var roleHint = component.Find("[data-testid='role-edit-execute-without-read']").TextContent;
        Assert.Contains("on 1 method,", roleHint);
        Assert.Contains("such as /Devices/Child/Start", roleHint);
        Assert.Empty(component.FindAll("[data-testid='rule-execute-without-read-hint']"));
    }

    [Fact]
    public async Task WhenAnIncludedRoleStopsAllowingRead_ThenTheExecuteWithoutReadHintAppears()
    {
        // Arrange
        var garden = Graph.GetRole("Garden");
        garden.Allow = [new PermissionRule { Members = ["Start"], Actions = [PermissionAction.Read] }];
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["Garden"];
        role.Allow = [new PermissionRule { Members = ["Start"], Actions = [AuthorizationActions.Execute] }];
        var component = RenderEditor(role);

        // Act
        garden.Allow = [];

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Single(component.FindAll("[data-testid='role-edit-execute-without-read']")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenReadIsAddedToTheRule_ThenTheExecuteWithoutReadHintsGo()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Allow = [new PermissionRule { Paths = ["/Devices/**"], Actions = [AuthorizationActions.Execute] }];
        var component = RenderEditor(role);
        Assert.NotEmpty(component.FindAll("[data-testid='role-edit-execute-without-read']"));

        // Act
        await SetActionAsync(component, 0, "read", true);

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-execute-without-read']"));
        Assert.Empty(component.FindAll("[data-testid='rule-execute-without-read-hint']"));
    }

    [Fact]
    public async Task WhenIncludesAreOffered_ThenTheRoleItselfIsNot()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("Kitchen"));
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(select.Instance.OpenMenu);

        // Assert
        var options = PopoverProvider.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value).ToList();
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "Garden"], options);
    }

    [Fact]
    public async Task WhenAnIncludeIsSelected_ThenTheEditedRoleHasItAndIsDirty()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("Kitchen"));
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync(["Garden"]));

        // Assert
        Assert.Equal(["Garden"], component.Instance.GetEditedRole().Includes.ToArray());
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenTheRoleHasAStatus_ThenItIsShownAsAnError()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["Ghost"];
        await StatusAssert.NotNullAsync(() => role.Status);

        // Act
        var component = RenderEditor(role);

        // Assert
        var alert = component.Find("[data-testid='role-edit-status']");
        Assert.Contains("Ghost", alert.TextContent);
        Assert.Contains("mud-alert-text-error", alert.ClassName);
    }

    [Fact]
    public async Task WhenAnEditRemovesTheCauseOfTheStatus_ThenTheErrorIsGoneBeforeSaving()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["Ghost"];
        await StatusAssert.NotNullAsync(() => role.Status);
        var component = RenderEditor(role);
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync([]));

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-status']"));
        Assert.NotNull(role.Status);
    }

    [Fact]
    public async Task WhenAnIncludedRoleStopsCompilingWhileTheDialogIsOpen_ThenTheErrorIsShown()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Includes = ["Garden"];
        var component = RenderEditor(role);

        // Act
        Graph.GetRole("Garden").Includes = ["Ghost"];

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Contains("Ghost", component.Find("[data-testid='role-edit-status']").TextContent),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WhenTheRoleHasNoStatus_ThenNoAlertIsShown()
    {
        // Act
        var component = RenderEditor(Graph.GetRole("Kitchen"));

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-status']"));
    }

    [Fact]
    public async Task WhenTheRoleHasAWarning_ThenItIsShownAsAWarning()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Deny = [new PermissionRule { Paths = ["/Devices"], Actions = [PermissionAction.Read] }];
        await StatusAssert.NotNullAsync(() => role.Warning);

        // Act
        var component = RenderEditor(role);

        // Assert
        var alert = component.Find("[data-testid='role-edit-warning']");
        Assert.Contains("Path pattern '/Devices' names a subject, not a member", alert.TextContent);
        Assert.Contains("mud-alert-text-warning", alert.ClassName);
        Assert.Empty(component.FindAll("[data-testid='role-edit-status']"));
    }

    [Fact]
    public async Task WhenAPathNamingAnEntryIsAdded_ThenTheWarningIsShownBeforeSaving()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        role.Deny = [new PermissionRule { Actions = [PermissionAction.Read] }];
        var component = RenderEditor(role);

        // Act
        await EnterTextAsync(component, "role-edit-deny-rule-0", "rule-paths", "/Devices");

        // Assert
        Assert.StartsWith("Path pattern '/Devices' names a subject, not a member",
            component.Find("[data-testid='role-edit-warning']").TextContent.Trim());
        Assert.Null(role.Warning);
    }

    [Fact]
    public void WhenAgentsUseTheRole_ThenTheirCountIsShownAboveTheTabs()
    {
        // Arrange
        Graph.Service.SetPrincipalRoles(new object(), ["kitchen"]);
        Graph.Service.SetPrincipalRoles(new object(), ["Kitchen", BuiltInRoles.ReaderName]);

        // Act
        var component = RenderEditor(Graph.GetRole("Kitchen"));

        // Assert
        Assert.Equal("Used by 2 agents", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim());
        Assert.Null(component.Find("[data-testid='role-edit-tabs']").QuerySelector("[data-testid='role-edit-used-by']"));
    }

    [Fact]
    public async Task WhenAnAgentStartsUsingTheRoleWhileTheDialogIsOpen_ThenTheCountFollows()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("Kitchen"));

        // Act
        Graph.Service.SetPrincipalRoles(new object(), ["Kitchen"]);

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Equal("Used by 1 agent", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim()),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenTheRoleIsEdited_ThenTheEditedRoleHasTheUnsavedState()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
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
        var role = Graph.GetRole("Kitchen");
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        var reloadedRole = new Role();
        Graph.Authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase)
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
    public async Task WhenNoAuthorizationHoldsTheRole_ThenSavingThrowsAndChangesNothing()
    {
        // Arrange: in the graph, so that the dialog opens, but outside an Authorization, so that it would be saved nowhere.
        var role = new Role();
        Graph.Root.Children = new Dictionary<string, IInterceptorSubject>(Graph.Root.Children) { ["Loose"] = role };
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Equal("The roles changed while this dialog was open. Close it and open it again.", exception.Message);
        Assert.Empty(role.Allow);
    }

    [Fact]
    public async Task WhenAnotherDialogSavedTheRoleWhileThisOneWasOpen_ThenSavingThrowsAndKeepsTheOtherSave()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var first = RenderEditor(role);
        var second = RenderEditor(role);
        await first.Find("[data-testid='role-edit-add-deny']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(first, 0, "read", true);
        await first.InvokeAsync(() => first.Instance.SaveAsync(CancellationToken.None));

        // The save makes the second dialog render again from a background thread, so the button is found and clicked
        // in one dispatch that no render can come between.
        await second.InvokeAsync(() => second.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs()));
        await SetActionAsync(second, 0, "read", true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.InvokeAsync(() => second.Instance.SaveAsync(CancellationToken.None)));
        Assert.Equal("The roles changed while this dialog was open. Close it and open it again.", exception.Message);
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read] }], role.Deny);
        Assert.Empty(role.Allow);
    }

    [Fact]
    public async Task WhenAnotherSaveLandsRightAfterThisOne_ThenItIsNotTakenAsTheListsThisDialogLoaded()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var otherDeny = new List<PermissionRule> { new() { Actions = [PermissionAction.Write] } };
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);

        // The other save is made as soon as this one assigned its deny rules, the last of its lists.
        using var otherSave = ((IInterceptorSubject)role).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name == nameof(Role.Deny) && !ReferenceEquals(role.Deny, otherDeny))
                {
                    role.Deny = otherDeny;
                }
            });
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));
        otherSave.Dispose();

        // Act & Assert
        await SetActionAsync(component, 0, "write", true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Same(otherDeny, role.Deny);
    }

    [Fact]
    public async Task WhenTheRoleIsSaved_ThenItsListsAreAssignedUnderTheEditLock()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        var isLockHeldPerAssignment = new List<bool>();
        using var subscription = ((IInterceptorSubject)role).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name is nameof(Role.Includes) or nameof(Role.Allow) or nameof(Role.Deny))
                {
                    isLockHeldPerAssignment.Add(AuthorizationEditLock.Instance.IsHeldByCurrentThread);
                }
            });

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal([true, true, true], isLockHeldPerAssignment);
    }

    [Fact]
    public async Task WhenTheDialogSavesTwice_ThenTheSecondSaveIsNoConcurrentChange()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Act
        await SetActionAsync(component, 0, "write", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read, PermissionAction.Write] }], role.Allow);
    }

    [Fact]
    public async Task WhenTheRoleMovesToAnotherAuthorizationWhileTheDialogIsOpen_ThenSavingThrows()
    {
        // Arrange
        var role = Graph.GetRole("Kitchen");
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        Graph.Root.Children = new Dictionary<string, IInterceptorSubject>(Graph.Root.Children)
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
        var role = Graph.GetRole("Kitchen");
        var garden = Graph.GetRole("Garden");
        var component = RenderEditor(role);

        // Act: renamed as the Rename operation does, left out once so that it is attached again under its new key.
        Graph.Authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["Garden"] = garden };
        Graph.Authorization.Roles = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase) { ["Pantry"] = role, ["Garden"] = garden };
        component.Render();

        // Assert
        Assert.Equal("Pantry", component.Instance.GetEditedRole().Name);
        Assert.Equal(["Pantry"], component.FindComponent<RoleSelector>().Instance.Excluded!);

        // Act: the renames make the dialog render again from a background thread, see the test of another dialog's save.
        await component.InvokeAsync(() => component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs()));
        await SetActionAsync(component, 0, "read", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read] }], role.Allow);
    }
}
