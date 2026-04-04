using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Namotion.NuGet.Plugins.Loading;

namespace Namotion.NuGet.Plugins.Tests.Loading;

public class PluginAssemblyLoadContextTests
{
    [Fact]
    public void WhenLoadingHostAssembly_ThenFallsBackToDefaultContext()
    {
        // Arrange
        var hostAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.Runtime" };
        var context = new PluginAssemblyLoadContext("TestPlugin", hostAssemblies, []);

        // Act
        var assembly = context.LoadFromAssemblyName(new AssemblyName("System.Runtime"));

        // Assert -- should get the same assembly as in the default context
        var defaultAssembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("System.Runtime"));
        Assert.Same(defaultAssembly, assembly);
    }

    [Fact]
    public void WhenContextIsCollectible_ThenCanUnload()
    {
        // Arrange
        var context = new PluginAssemblyLoadContext("TestPlugin", [], []);

        // Act & Assert -- should not throw
        context.Unload();
    }
}
