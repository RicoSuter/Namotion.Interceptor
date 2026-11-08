using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// Renders the role editor over a graph with the custom roles Kitchen and Garden, with the services it needs, a fake role store, a viewer who may do everything until a test restricts them, and helpers
/// to drive its rule cards.
/// </summary>
public abstract class RoleEditorTestContext : BunitContext, IAsyncLifetime
{
    private readonly ServiceProvider _serializerServices = new ServiceCollection().BuildServiceProvider();
    private readonly RootManager _rootManager;

    private protected TestAuthorizationGraph Graph { get; } = TestAuthorizationGraph.Create("kitchen", "garden");

    protected IRenderedComponent<MudPopoverProvider> PopoverProvider { get; }

    /// <summary>The permissions of the viewer, who may do everything until a test restricts them.</summary>
    private protected TestUserPermissions Permissions { get; } = new();

    protected RoleEditorTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(Graph.Service);
        Services.AddSingleton(Graph.PathResolver);
        Services.AddSingleton<IRoleStore>(Graph.RoleStore);
        Services.AddSingleton<IUserPermissions>(Permissions);

        // The path picker lists the subjects of the root manager, which has none here; a path is typed into it instead.
        var pickerTypes = new TypeProvider();
        _rootManager = new RootManager(
            new SubjectTypeRegistry(pickerTypes),
            new ConfigurableSubjectSerializer(pickerTypes, _serializerServices),
            InterceptorSubjectContext.Create(),
            Graph.PathResolver);
        Services.AddSingleton(_rootManager);

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Role), typeof(TestRoot)]);
        Services.AddSingleton(new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider)));
        Services.AddSingleton(new SubjectComponentRegistry(new TypeProvider().AddAssembly(typeof(RoleEditComponent).Assembly)));

        PopoverProvider = Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _rootManager.Dispose();
        await _serializerServices.DisposeAsync();
        Graph.Dispose();
    }

    protected IRenderedComponent<RoleEditComponent> RenderEditor(Role role) =>
        Render<RoleEditComponent>(parameters => parameters.Add(component => component.Subject, role));

    protected static IReadOnlyList<AngleSharp.Dom.IElement> GetCards(IRenderedComponent<RoleEditComponent> component, string section) =>
        component.FindAll($"[data-testid^='role-edit-{section}-rule-']");

    protected static IReadOnlyList<string> GetChips(AngleSharp.Dom.IElement card, string input) =>
        card.QuerySelectorAll($"[data-testid='{input}-chip']").Select(chip => chip.TextContent.Trim()).ToList();

    // The rule cards in the order of the allow rules followed by the deny rules; the boxes in the order read, write, execute, history.
    protected static IReadOnlyList<bool> GetActionStates(IRenderedComponent<RoleEditComponent> component, int cardIndex) =>
        component.FindComponents<RuleCard>()[cardIndex]
            .FindAll("input[type='checkbox'][data-testid^='rule-action-']")
            .Select(box => ((AngleSharp.Html.Dom.IHtmlInputElement)box).IsChecked)
            .ToList();

    protected static IReadOnlyList<IRenderedComponent<MudCheckBox<bool>>> GetActionBoxes(IRenderedComponent<RoleEditComponent> component, int cardIndex) =>
        component.FindComponents<RuleCard>()[cardIndex].FindComponents<MudCheckBox<bool>>();

    protected static Task SetActionAsync(IRenderedComponent<RoleEditComponent> component, int cardIndex, string action, bool isChecked)
    {
        var box = GetActionBoxes(component, cardIndex).Single(box => box.Instance.Label == action);
        return component.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(isChecked));
    }

    protected static Task TypeTextAsync(IRenderedComponent<RoleEditComponent> component, string card, string input, string text) =>
        component.Find($"[data-testid='{card}'] [data-testid='{input}'] input").InputAsync(new ChangeEventArgs { Value = text });

    protected static Task RemoveChipAsync(IRenderedComponent<RoleEditComponent> component, string card, string input, int index) =>
        component.FindAll($"[data-testid='{card}'] [data-testid='{input}-chip']")[index]
            .QuerySelector(".mud-chip-close-button")!
            .ClickAsync(new MouseEventArgs());

    protected static async Task EnterTextAsync(IRenderedComponent<RoleEditComponent> component, string card, string input, string text)
    {
        var selector = $"[data-testid='{card}'] [data-testid='{input}'] input";
        await component.Find(selector).InputAsync(new ChangeEventArgs { Value = text });
        await component.Find(selector).KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await component.Find(selector).KeyUpAsync(new KeyboardEventArgs { Key = "Enter" });
    }
}
