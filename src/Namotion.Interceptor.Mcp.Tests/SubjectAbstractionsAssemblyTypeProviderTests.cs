using Namotion.Interceptor.Mcp.Implementations;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests;

public class SubjectAbstractionsAssemblyTypeProviderTests
{
    [Fact]
    public void GetTypes_returns_interfaces_from_marked_assemblies()
    {
        var provider = new SubjectAbstractionsAssemblyTypeProvider();
        var types = provider.GetTypes().ToList();

        // The test assembly is marked with [SubjectAbstractionsAssembly]
        // and defines ITestSensor below — it should appear in results
        Assert.Contains(types, t => t.Name == typeof(ITestSensor).FullName);
        Assert.All(types, t => Assert.True(t.IsInterface));
    }

    [Fact]
    public void GetTypes_excludes_non_interface_types()
    {
        var provider = new SubjectAbstractionsAssemblyTypeProvider();
        var types = provider.GetTypes().ToList();

        // Concrete classes should not appear
        Assert.DoesNotContain(types, t => t.Name == typeof(SubjectAbstractionsAssemblyTypeProviderTests).FullName);
    }
}

/// <summary>
/// Test interface in the marked assembly for type provider discovery tests.
/// </summary>
public interface ITestSensor
{
    decimal Temperature { get; }
}
