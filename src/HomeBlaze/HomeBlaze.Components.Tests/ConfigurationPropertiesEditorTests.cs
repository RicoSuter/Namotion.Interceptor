using Bunit;
using HomeBlaze.Components.Editors;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public sealed class ConfigurationPropertiesEditorTests : BunitContext, IAsyncLifetime
{
    public ConfigurationPropertiesEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Render<MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    [Fact]
    public void WhenAConfigurationPropertyHoldsSubjects_ThenTheEditorHasNoFieldForIt()
    {
        // Arrange
        var subject = new SubjectHoldingSubject(SubjectContextFactory.Create(new ServiceCollection())) { Label = "Kitchen" };

        // Act
        var component = Render<ConfigurationPropertiesEditor>(parameters => parameters.Add(editor => editor.Subject, subject));

        // Assert
        var labels = component.FindComponents<MudTextField<string>>().Select(field => field.Instance.Label).ToList();
        Assert.Equal(["Label"], labels);
    }
}
