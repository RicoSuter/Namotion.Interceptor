using System.Reflection;
using Bunit;
using Excubo.Blazor.Diagrams;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// Every widget component of HomeBlaze.Components that derives from <see cref="PermissionsComponentBase"/> renders
/// again when the permissions of the viewer change, and stops listening once it is disposed. The widget is rendered
/// without a parent that renders again itself, so a new render can only come from the widget. Controls inside a widget
/// may listen as well, so the disposed tree must leave no listener at all.
/// </summary>
public sealed class PermissionsComponentTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();
    private readonly WidgetTestGraph _graph;

    public PermissionsComponentTests()
    {
        _graph = WidgetTestGraph.Create(this, "Text");
        Services.AddSingleton<IUserPermissions>(_permissions);

        // The diagram of a canvas measures itself through its script after it rendered, which a test has not.
        ComponentFactories.Add<Diagram>(() => new UnmeasuredDiagram());
    }

    public static TheoryData<Type> WidgetComponentTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var componentType in DiscoverWidgetComponentTypes().Keys)
        {
            data.Add(componentType);
        }

        return data;
    }

    [Fact]
    public void WhenTheWidgetComponentsAreDiscovered_ThenSomeAreFound()
    {
        // Act
        var componentTypes = DiscoverWidgetComponentTypes();

        // Assert
        Assert.NotEmpty(componentTypes);
    }

    [Theory]
    [MemberData(nameof(WidgetComponentTypes))]
    public void WhenThePermissionsChange_ThenTheWidgetRendersAgain(Type componentType)
    {
        // Arrange
        var widget = RenderWidget(componentType);
        var renderCount = widget.RenderCount;

        // Act
        _permissions.RaiseChanged();

        // Assert
        widget.WaitForAssertion(() => Assert.True(widget.RenderCount > renderCount, componentType.Name));
    }

    [Theory]
    [MemberData(nameof(WidgetComponentTypes))]
    public async Task WhenTheWidgetIsDisposed_ThenItNoLongerListensForChanges(Type componentType)
    {
        // Arrange
        RenderWidget(componentType);
        Assert.NotEqual(0, _permissions.ChangedSubscriberCount);

        // Act
        await DisposeComponentsAsync();

        // Assert
        Assert.Equal(0, _permissions.ChangedSubscriberCount);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _graph.DisposeAsync();
    }

    private IRenderedComponent<IComponent> RenderWidget(Type componentType)
    {
        var subject = _graph.CreateWidget(DiscoverWidgetComponentTypes()[componentType], WidgetTestGraph.MissingPath);
        var host = Render<DynamicComponent>(parameters => parameters
            .Add(component => component.Type, componentType)
            .Add(component => component.Parameters, new Dictionary<string, object> { ["Subject"] = subject }));

        return host.FindComponents<IComponent>().Single(component => component.Instance.GetType() == componentType);
    }

    // The widget components that derive from the base, each with the subject type it renders.
    private static Dictionary<Type, Type> DiscoverWidgetComponentTypes()
    {
        var types = new Dictionary<Type, Type>();
        foreach (var componentType in typeof(Widget).Assembly.GetTypes())
        {
            if (!componentType.IsSubclassOf(typeof(PermissionsComponentBase)))
            {
                continue;
            }

            foreach (var attribute in componentType.GetCustomAttributes<SubjectComponentAttribute>(inherit: false))
            {
                if (attribute.ComponentType == SubjectComponentType.Widget)
                {
                    types[componentType] = attribute.SubjectType;
                }
            }
        }

        return types;
    }

    private sealed class UnmeasuredDiagram : Diagram
    {
        protected override Task OnAfterRenderAsync(bool first_render) => Task.CompletedTask;
    }
}
