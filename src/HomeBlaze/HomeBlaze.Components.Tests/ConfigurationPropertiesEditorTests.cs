using HomeBlaze.Components.Editors;
using HomeBlaze.Components.Tests.Models;
using Xunit;

namespace HomeBlaze.Components.Tests;

public sealed class ConfigurationPropertiesEditorTests : IDisposable
{
    private readonly PersistenceTestGraph _graph = new();

    [Fact]
    public void WhenSubjectHasNestedConfigurationSubject_ThenItGetsNoField()
    {
        // Act
        var properties = ConfigurationPropertiesEditor.GetEditableProperties(_graph.Device);

        // Assert
        var names = properties.Select(property => property.Name).ToList();
        Assert.Contains(nameof(PersistedDevice.Speed), names);
        Assert.Contains(nameof(PersistedDevice.Mode), names);
        Assert.DoesNotContain(nameof(PersistedDevice.Settings), names);
    }

    public void Dispose() => _graph.Dispose();
}
