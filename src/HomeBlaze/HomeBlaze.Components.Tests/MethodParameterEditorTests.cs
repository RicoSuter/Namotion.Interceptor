using Bunit;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
using HomeBlaze.Components.Operations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

/// <summary>
/// The parameter dialog enters a parameter that names a registered editor with that editor, and the value the editor
/// reports is the value the operation gets.
/// </summary>
public sealed class MethodParameterEditorTests : BunitContext, IAsyncLifetime
{
    private const string EditorName = "Test.Names";

    public MethodParameterEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public async Task WhenTheEditorOfAParameterIsRegistered_ThenTheDialogEntersItWithTheEditorAndExecutesWithItsValue()
    {
        // Arrange
        Services.AddMethodParameterEditor<NamesEditor>(EditorName);
        var provider = Render<MudDialogProvider>();
        var result = await ShowAsync(provider, CreateMethod(EditorName));

        // Act
        await provider.Find("[data-testid='names-editor']").ClickAsync(new MouseEventArgs());
        await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Execute").ClickAsync(new MouseEventArgs());

        // Assert
        Assert.Empty(provider.FindAll("textarea"));
        var parameters = Assert.IsType<object?[]>((await result)!.Data);
        Assert.Equal(["reader"], Assert.IsType<string[]>(parameters[0]));
    }

    [Fact]
    public async Task WhenTheEditorGetsItsParameter_ThenItGetsTheLabelAndTheInitialValue()
    {
        // Arrange
        Services.AddMethodParameterEditor<NamesEditor>(EditorName);
        var provider = Render<MudDialogProvider>();

        // Act
        await ShowAsync(provider, CreateMethod(EditorName), new Dictionary<string, object?> { ["roles"] = new[] { "operator" } });

        // Assert
        var editor = provider.FindComponent<NamesEditor>().Instance;
        Assert.Equal("Roles *", editor.Label);
        Assert.Equal("roles", editor.Parameter.Name);
        Assert.Equal(["operator"], Assert.IsType<string[]>(editor.Value));
    }

    [Fact]
    public async Task WhenTheEditorReportsNoValueForARequiredParameter_ThenExecuteIsDisabledAndTheEditorGetsTheError()
    {
        // Arrange
        Services.AddMethodParameterEditor<NamesEditor>(EditorName);
        var provider = Render<MudDialogProvider>();

        // Act
        await ShowAsync(provider, CreateMethod(EditorName));

        // Assert
        var execute = provider.FindAll("button").Single(button => button.TextContent.Trim() == "Execute");
        Assert.True(execute.HasAttribute("disabled"));
        Assert.Equal("This field is required", provider.FindComponent<NamesEditor>().Instance.ErrorText);
    }

    [Fact]
    public async Task WhenNoEditorOfTheNameIsRegistered_ThenTheParameterIsEnteredAsJson()
    {
        // Arrange
        Services.AddMethodParameterEditor<NamesEditor>("Other.Names");
        var provider = Render<MudDialogProvider>();

        // Act
        await ShowAsync(provider, CreateMethod(EditorName));

        // Assert
        Assert.Empty(provider.FindComponents<NamesEditor>());
        Assert.Single(provider.FindAll("textarea"));
    }

    private static MethodMetadata CreateMethod(string editorName) => new(_ => null)
    {
        Kind = MethodKind.Operation,
        Title = "Set Roles",
        Parameters = [new MethodParameter { Name = "roles", Type = typeof(string[]), Editor = editorName }]
    };

    private async Task<Task<DialogResult?>> ShowAsync(
        IRenderedComponent<MudDialogProvider> provider, MethodMetadata method, IReadOnlyDictionary<string, object?>? initialValues = null)
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await MethodParameterDialog.ShowAsync(dialogService, method, initialValues));
        return reference!.Result;
    }

    /// <summary>An editor that reports no value until clicked and then the names <c>["reader"]</c>.</summary>
    private sealed class NamesEditor : MethodParameterEditor
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "data-testid", "names-editor");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => ValueChanged.InvokeAsync(new[] { "reader" })));
            builder.AddContent(3, Label);
            builder.CloseElement();
        }
    }
}
