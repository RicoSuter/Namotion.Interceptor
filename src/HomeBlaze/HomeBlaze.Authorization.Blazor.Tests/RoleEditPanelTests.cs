using Bunit;
using HomeBlaze.Components.Editors;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// The edit panel saves a role through the role editor also after the store replaced the role subject it was opened
/// with, and reports a change of the role itself as such rather than as a refusal.
/// </summary>
public sealed class RoleEditPanelTests : RoleEditorTestContext
{
    private const string Refusal = "Your roles do not allow this change.";

    private IEnumerable<string?> SnackbarMessages => Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(snackbar => snackbar.Message);

    [Fact]
    public async Task WhenAnotherRoleChangesWhileThePanelIsOpen_ThenThePanelSavesTheRole()
    {
        // Arrange
        var panel = RenderPanel(Graph.GetRole("kitchen"));
        var editor = panel.FindComponent<RoleEditComponent>();
        await editor.Find("[data-testid='role-edit-add-allow']").ClickAsync(new MouseEventArgs());
        await SetActionAsync(editor, 0, "read", true);
        Graph.SetRole("garden", includes: [BuiltInRoles.ReaderName]);

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.True(isSaved);
        Assert.Equal([new PermissionRule { Actions = [PermissionAction.Read] }], Graph.GetRole("kitchen").Allow);
        Assert.DoesNotContain(Refusal, SnackbarMessages);
        Assert.Contains("Configuration saved.", SnackbarMessages);
    }

    [Fact]
    public async Task WhenTheRoleChangesWhileThePanelIsOpen_ThenThePanelSaysSoInsteadOfRefusing()
    {
        // Arrange
        var panel = RenderPanel(Graph.GetRole("kitchen"));
        Graph.SetRole("kitchen", includes: [BuiltInRoles.ReaderName]);

        // Act
        var isSaved = await panel.InvokeAsync(() => panel.Instance.SaveAsync(CancellationToken.None));

        // Assert
        Assert.False(isSaved);
        Assert.Empty(Graph.RoleStore.Saves);
        Assert.DoesNotContain(Refusal, SnackbarMessages);
        Assert.Contains(SnackbarMessages, message => message?.Contains("The role 'kitchen' changed since you opened it.", StringComparison.Ordinal) == true);
    }

    private IRenderedComponent<SubjectEditPanel> RenderPanel(Role role) =>
        Render<SubjectEditPanel>(parameters => parameters.Add(panel => panel.Subject, role));
}
