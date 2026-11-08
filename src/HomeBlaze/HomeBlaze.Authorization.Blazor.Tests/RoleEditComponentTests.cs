using Bunit;
using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor.Registry;
using HomeBlaze.Components.Abstractions;
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
        var role = Graph.SetRole("kitchen", includes: ["reader"], allow: [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read, AuthorizationActions.Execute] }], deny: [new PermissionRule { Tags = [MemberTags.Secret], Actions = [PermissionAction.Read] }]);

        // Act
        var component = RenderEditor(role);

        // Assert
        var usedBy = component.Find(".role-edit").FirstElementChild;
        Assert.Equal("role-edit-used-by", usedBy?.GetAttribute("data-testid"));
        Assert.Contains("role-edit-tabs", usedBy?.NextElementSibling?.ClassName);
        Assert.Equal(BuiltInRoles.ReaderName, component.Find("[data-testid='role-edit-include-reader']").TextContent.Trim());

        var allowCard = Assert.Single(GetCards(component, "allow"));
        Assert.Equal(["/Devices/**"], GetChips(allowCard, "rule-paths"));
        Assert.Equal("any", allowCard.QuerySelector("[data-testid='rule-types-any']")?.TextContent.Trim());
        Assert.Equal([true, false, true, false, false], GetActionStates(component, 0));

        var denyCard = Assert.Single(GetCards(component, "deny"));
        Assert.Equal([MemberTags.Secret], GetChips(denyCard, "rule-tags"));
        Assert.True(component.Instance.IsValid);
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public void WhenTheRoleIsLoaded_ThenTheBuiltInRolesAndTheSystemRulesAreDescribedWithTheIncludes()
    {
        // Act
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Assert
        Assert.Contains("The system rules apply to every role.", component.Find("[data-testid='role-edit-builtin-roles']").TextContent);
        Assert.Equal("reader: Reads every member and its history and executes queries.",
            component.Find("[data-testid='role-edit-builtin-role-reader']").TextContent.Trim());
        Assert.Equal("operator: Includes reader, writes every member and executes operations.",
            component.Find("[data-testid='role-edit-builtin-role-operator']").TextContent.Trim());
        Assert.Equal("everything: Allows every action on every member.",
            component.Find("[data-testid='role-edit-builtin-role-everything']").TextContent.Trim());
    }

    [Fact]
    public void WhenTheRoleIsLoaded_ThenTheTabsAreRolesAllowAndDenyWithTheirCountsAndPreview()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", allow: 
        [
            new PermissionRule { Actions = [PermissionAction.Read] },
            new PermissionRule { Actions = [PermissionAction.Write] }
        ], deny: [new PermissionRule { Actions = [PermissionAction.Read] }]);

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
        var role = Graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName, "ghost"]);

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
        var role = Graph.SetRole("kitchen", includes: ["pantry"]);
        var component = RenderEditor(role);

        // Act
        Graph.RoleStore.Apply(null, new AuthorizationRole { Name = "pantry" });

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Empty(component.FindAll("[data-testid='role-edit-tab-roles-error']")),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("mud-chip-color-error", component.Find("[data-testid='role-edit-include-pantry']").ClassName);
    }

    [Fact]
    public async Task WhenARuleBecomesInvalid_ThenOnlyTheTabOfItsListShowsTheErrorMarker()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read] }]);
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
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Assert
        Assert.Equal("No allow rules yet", component.Find("[data-testid='role-edit-allow-empty']").TextContent.Trim());
        Assert.Equal("No deny rules yet", component.Find("[data-testid='role-edit-deny-empty']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenARuleIsAddedToAnEmptyList_ThenOnlyItsTabNoLongerSaysItHasNoRules()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("kitchen"));

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
        var component = RenderEditor(Graph.GetRole("kitchen"));
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
        var role = Graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read] }]);
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
        var role = Graph.SetRole("kitchen", allow: 
        [
            new PermissionRule { Paths = ["/First"], Actions = [PermissionAction.Read] },
            new PermissionRule { Paths = ["/Second"], Actions = [PermissionAction.Read] }
        ]);
        var component = RenderEditor(role);

        // Act
        await GetCards(component, "allow")[0].QuerySelector("[data-testid='rule-remove']")!.ClickAsync(new MouseEventArgs());

        // Assert
        var card = Assert.Single(GetCards(component, "allow"));
        Assert.Equal(["/Second"], GetChips(card, "rule-paths"));
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenTheRoleIsInvalid_ThenSavingThrowsAndChangesNothing()
    {
        // Arrange
        var role = Graph.GetRole("kitchen");
        var oldAllow = role.Allow;
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));
        Assert.Same(oldAllow, role.Allow);
    }

    [Fact]
    public async Task WhenIncludesAreOffered_ThenTheRoleItselfIsNot()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("kitchen"));
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(select.Instance.OpenMenu);

        // Assert
        var options = PopoverProvider.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value).ToList();
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "garden"], options);
    }

    [Fact]
    public async Task WhenAnIncludeIsSelected_ThenTheEditedRoleHasItAndIsDirty()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("kitchen"));
        var select = component.FindComponent<RoleSelector>().FindComponent<MudSelect<string>>();

        // Act
        await component.InvokeAsync(() => select.Instance.SelectedValuesChanged.InvokeAsync(["garden"]));

        // Assert
        Assert.Equal(["garden"], component.Instance.GetEditedRole().Includes.ToArray());
        Assert.True(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenTheRoleHasAStatus_ThenItIsShownAsAnError()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", includes: ["ghost"]);
        await StatusAssert.NotNullAsync(() => role.Status);

        // Act
        var component = RenderEditor(role);

        // Assert
        var alert = component.Find("[data-testid='role-edit-status']");
        Assert.Contains("ghost", alert.TextContent);
        Assert.Contains("mud-alert-text-error", alert.ClassName);
    }

    [Fact]
    public async Task WhenAnEditRemovesTheCauseOfTheStatus_ThenTheErrorIsGoneBeforeSaving()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", includes: ["ghost"]);
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
        var role = Graph.SetRole("kitchen", includes: ["garden"]);
        var component = RenderEditor(role);

        // Act
        Graph.SetRole("garden", includes: ["ghost"]);

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Contains("ghost", component.Find("[data-testid='role-edit-status']").TextContent),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WhenTheRoleHasNoStatus_ThenNoAlertIsShown()
    {
        // Act
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Assert
        Assert.Empty(component.FindAll("[data-testid='role-edit-status']"));
    }

    [Fact]
    public async Task WhenTheRoleHasAWarning_ThenItIsShownAsAWarning()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", deny: [new PermissionRule { Paths = ["/Devices"], Actions = [PermissionAction.Read] }]);
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
        var role = Graph.SetRole("kitchen", deny: [new PermissionRule { Actions = [PermissionAction.Read] }]);
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
        Graph.Service.SetPrincipalRoles(new object(), ["kitchen", BuiltInRoles.ReaderName]);

        // Act
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Assert
        Assert.Equal("Used by 2 agents", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim());
        Assert.Null(component.Find("[data-testid='role-edit-tabs']").QuerySelector("[data-testid='role-edit-used-by']"));
    }

    [Fact]
    public async Task WhenAnAgentStartsUsingTheRoleWhileTheDialogIsOpen_ThenTheCountFollows()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Act
        Graph.Service.SetPrincipalRoles(new object(), ["kitchen"]);

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Equal("Used by 1 agent", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim()),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenAnotherRoleChangedAndThenAnAgentStartsUsingTheRole_ThenTheCountFollowsTheNewSubjectOfTheRole()
    {
        // Arrange: the store replaces every role subject on each change.
        var opened = Graph.GetRole("kitchen");
        var component = RenderEditor(opened);
        Graph.SetRole("garden", includes: [BuiltInRoles.ReaderName]);
        Assert.NotSame(opened, Graph.GetRole("kitchen"));

        // Act
        Graph.Service.SetPrincipalRoles(new object(), ["kitchen"]);

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Equal("Used by 1 agent", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim()),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenTheRoleWasSavedAndThenAnAgentStartsUsingIt_ThenTheCountFollowsTheSavedSubject()
    {
        // Arrange
        var opened = Graph.GetRole("kitchen");
        var component = RenderEditor(opened);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));
        Assert.NotSame(opened, Graph.GetRole("kitchen"));

        // Act
        Graph.Service.SetPrincipalRoles(new object(), ["kitchen"]);

        // Assert
        await component.WaitForAssertionAsync(
            () => Assert.Equal("Used by 1 agent", component.Find("[data-testid='role-edit-used-by']").TextContent.Trim()),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenTheRoleIsEdited_ThenTheEditedRoleHasTheUnsavedState()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", allow: [new PermissionRule { Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);

        // Act
        await SetActionAsync(component, 0, "history", true);

        // Assert
        var editedRole = component.Instance.GetEditedRole();
        Assert.Equal("kitchen", editedRole.Name);
        Assert.Equal([PermissionAction.Read, AuthorizationActions.History], editedRole.Allow[0].Actions.ToArray());
        Assert.Equal([PermissionAction.Read], role.Allow[0].Actions.ToArray());
    }

    [Fact]
    public async Task WhenSaved_ThenTheStoreReceivesTheEditedRules()
    {
        // Arrange
        var role = Graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName], allow: [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read] }]);
        var component = RenderEditor(role);
        await component.Find("[data-testid='role-edit-add-deny']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 1, "write", true);
        await EnterTextAsync(component, "role-edit-deny-rule-0", "rule-members", "Secret*");

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        var savedRole = Assert.Single(Graph.RoleStore.Saves).Role;
        Assert.Equal([BuiltInRoles.ReaderName], savedRole.Includes.ToArray());
        Assert.Equal([new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read] }], savedRole.Allow.ToArray());
        Assert.Equal([new PermissionRule { Members = ["Secret*"], Actions = [PermissionAction.Write] }], savedRole.Deny.ToArray());
        Assert.False(component.Instance.IsDirty);
    }

    [Fact]
    public async Task WhenNoAuthorizationHoldsTheRole_ThenSavingThrowsAndTheStoreReceivesNothing()
    {
        // Arrange: in the graph, so that the dialog opens, but outside an Authorization, so that it would be saved nowhere.
        var role = new Role();
        Graph.Root.Children = new Dictionary<string, IInterceptorSubject>(Graph.Root.Children) { ["Loose"] = role };
        var component = RenderEditor(role);

        // Act
        var exception = await Record.ExceptionAsync(() => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(Graph.RoleStore.Saves);
        Assert.Contains("not part of an Authorization subject", component.Find("[data-testid='role-edit-save-error']").TextContent);
    }

    [Fact]
    public async Task WhenTheRoleChangedSinceTheEditorOpened_ThenSavingIsRefusedWithoutTheStoreAndKeepsTheEdits()
    {
        // Arrange: the store replaces every role subject on each change.
        var component = RenderEditor(Graph.GetRole("kitchen"));
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        Graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);

        // Act
        var exception = await Record.ExceptionAsync(() => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(Graph.RoleStore.Saves);
        Assert.Contains("The role 'kitchen' changed since you opened it.", component.Find("[data-testid='role-edit-save-error']").TextContent);
        Assert.True(component.Instance.IsDirty);
        Assert.Equal([BuiltInRoles.ReaderName], Graph.GetRole("kitchen").Includes);
    }

    [Fact]
    public async Task WhenAnotherRoleChangesWhileTheEditorIsOpen_ThenItStaysEditableAndSavesThroughTheNewSubjectOfTheRole()
    {
        // Arrange: the store replaces every role subject on each change.
        var opened = Graph.GetRole("kitchen");
        var component = RenderEditor(opened);
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        Graph.SetRole("garden", includes: [BuiltInRoles.ReaderName]);
        component.Render();

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.NotSame(opened, Graph.GetRole("kitchen"));
        Assert.Equal("kitchen", Assert.Single(Graph.RoleStore.Saves).OriginalName);
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read] }], Graph.GetRole("kitchen").Allow);
        Assert.Empty(component.FindAll("[data-testid='role-edit-save-error']"));
    }

    [Fact]
    public void WhenTheRoleChangesWhileTheEditorIsOpen_ThenTheEditorSaysSoAndIsReadOnly()
    {
        // Arrange
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Act
        Graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);
        component.Render();

        // Assert
        Assert.Contains("The role 'kitchen' changed since you opened it.", component.Find("[data-testid='role-edit-save-error']").TextContent);
        Assert.All(
            component.FindAll("[data-testid='role-edit-tabs'] button[data-testid]").Where(IsOutsideThePreview),
            button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task WhenTheRoleIsSaved_ThenTheStoreReceivesItUnderItsNameWithItsStamp()
    {
        // Arrange
        Graph.RoleStore.Stamp = "stamp-1";
        var component = RenderEditor(Graph.GetRole("kitchen"));

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        var call = Assert.Single(Graph.RoleStore.Saves);
        Assert.Equal("kitchen", call.OriginalName);
        Assert.Equal("kitchen", call.Role.Name);
        Assert.Equal("stamp-1", call.Stamp);
    }

    [Fact]
    public async Task WhenTheStoreRefusesTheSave_ThenTheErrorIsShownAndSavingThrows()
    {
        // Arrange
        Graph.RoleStore.Result = new RoleChangeResult(false, "changed since you opened it");
        var component = RenderEditor(Graph.GetRole("kitchen"));
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);

        // Act
        var exception = await Record.ExceptionAsync(() => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("changed since you opened it", component.Find("[data-testid='role-edit-save-error']").TextContent);
        Assert.True(component.Instance.IsDirty);
        Assert.Empty(Graph.GetRole("kitchen").Allow);
    }

    [Fact]
    public async Task WhenTheHostSavesAndTheStoreRefuses_ThenTheSaveFailsWithTheError()
    {
        // Arrange
        Graph.RoleStore.Result = new RoleChangeResult(false, "changed since you opened it");
        var component = RenderEditor(Graph.GetRole("kitchen"));
        ISubjectEditComponent editComponent = component.Instance;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => component.InvokeAsync(() => editComponent.SaveAsync(CancellationToken.None)));
        Assert.Equal("changed since you opened it", exception.Message);
    }

    [Fact]
    public void WhenTheViewerMayNotAdministerTheEditOperationOfTheRole_ThenTheEditorIsReadOnly()
    {
        // Arrange
        var role = Graph.SetRole(
            "kitchen",
            includes: [BuiltInRoles.ReaderName],
            allow: [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Read] }],
            deny: [new PermissionRule { Members = ["Secret*"], Actions = [PermissionAction.Write] }]);
        Permissions.CannotAdminister.Add(GetEditOperationMember(role));

        // Act
        var component = RenderEditor(role);

        // Assert: the preview, which changes nothing, stays usable for a viewer who may administer the roles.
        var editInputs = component.FindAll("input").Where(IsOutsideThePreview).ToList();
        Assert.NotEmpty(editInputs);
        Assert.All(editInputs, input => Assert.True(input.HasAttribute("disabled") || input.HasAttribute("readonly")));
        Assert.All(
            component.FindAll("[data-testid='role-edit-tabs'] button[data-testid]").Where(IsOutsideThePreview),
            button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task WhenTheViewerMayNoLongerAdministerTheEditOperationOfTheRole_ThenSavingIsRefusedWithoutTheStore()
    {
        // Arrange
        var role = Graph.GetRole("kitchen");
        var component = RenderEditor(role);
        Permissions.CannotAdminister.Add(GetEditOperationMember(role));

        // Act
        var exception = await Record.ExceptionAsync(() => component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None)));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(Graph.RoleStore.Saves);
        Assert.Contains("full administrator", component.Find("[data-testid='role-edit-save-error']").TextContent);
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheEditOperationButNotTheRoles_ThenTheRoleIsSaved()
    {
        // Arrange
        Permissions.CannotAdminister.Add(Graph.Authorization.TryGetRegisteredSubject()!.TryGetProperty(nameof(Authorization.Roles))!);
        var component = RenderEditor(Graph.GetRole("kitchen"));
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);

        // Act
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read] }], Graph.GetRole("kitchen").Allow);
    }

    [Fact]
    public async Task WhenTheDialogSavesTwice_ThenTheSecondSaveGoesThroughTheRoleTheFirstSaved()
    {
        // Arrange
        Graph.RoleStore.Stamp = "stamp-1";
        Graph.RoleStore.StampAfterChange = "stamp-2";
        var component = RenderEditor(Graph.GetRole("kitchen"));
        await component.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(component, 0, "read", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Act
        await SetActionAsync(component, 0, "write", true);
        await component.InvokeAsync(() => component.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.Equal(["stamp-1", "stamp-2"], Graph.RoleStore.Saves.Select(call => call.Stamp));
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read, PermissionAction.Write] }], Graph.GetRole("kitchen").Allow);
    }

    private static bool IsOutsideThePreview(AngleSharp.Dom.IElement element) =>
        element.Closest("[data-testid^='role-preview']") is null;

    private static Namotion.Interceptor.Registry.Abstractions.RegisteredSubjectProperty GetEditOperationMember(Role role) =>
        role.TryGetRegisteredSubject()!.TryGetProperty("Edit")!;
}
