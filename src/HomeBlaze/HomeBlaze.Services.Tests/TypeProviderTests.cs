using System.Reflection;
using System.Reflection.Emit;
using HomeBlaze.Services;

namespace HomeBlaze.Services.Tests;

public class TypeProviderTests
{
    [Fact]
    public void Types_ReturnsEmptyInitially()
    {
        // Arrange
        var provider = new TypeProvider();

        // Act
        var types = provider.Types;

        // Assert
        Assert.Empty(types);
    }

    [Fact]
    public void AddAssembly_AddsTypesFromAssembly()
    {
        // Arrange
        var provider = new TypeProvider();

        // Act
        provider.AddAssembly(typeof(TypeProvider).Assembly);
        var types = provider.Types;

        // Assert
        Assert.NotEmpty(types);
        Assert.Contains(types, t => t == typeof(TypeProvider));
    }

    [Fact]
    public void WhenTypesAreAdded_ThenTypesChangedIsRaisedOnce()
    {
        // Arrange
        var provider = new TypeProvider();
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        provider.AddTypes([typeof(string), typeof(int)]);

        // Assert
        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void WhenAllTypesAreAlreadyRegistered_ThenTypesChangedIsNotRaisedAndTypesInstanceIsKept()
    {
        // Arrange
        var provider = new TypeProvider();
        provider.AddTypes([typeof(string)]);
        var typesBefore = provider.Types;
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        provider.AddTypes([typeof(string)]);

        // Assert
        Assert.Equal(0, raisedCount);
        Assert.Same(typesBefore, provider.Types);
        Assert.Single(provider.Types);
    }

    [Fact]
    public void WhenTypesAreAdded_ThenTypesReturnsNewInstance()
    {
        // Arrange
        var provider = new TypeProvider();
        var typesBefore = provider.Types;

        // Act
        provider.AddTypes([typeof(string)]);

        // Assert
        Assert.NotSame(typesBefore, provider.Types);
    }

    [Fact]
    public void WhenDifferentTypeHasSameFullName_ThenItIsSkippedAndReturned()
    {
        // Arrange
        var provider = new TypeProvider();
        var firstType = CreateDynamicType("FirstAssembly", "Duplicate.Name.Device");
        var secondType = CreateDynamicType("SecondAssembly", "Duplicate.Name.Device");
        provider.AddTypes([firstType]);

        // Act
        var skippedTypes = provider.AddTypes([secondType]);

        // Assert
        Assert.Equal([secondType], skippedTypes);
        Assert.Contains(firstType, provider.Types);
        Assert.DoesNotContain(secondType, provider.Types);
    }

    [Fact]
    public void WhenAssembliesAreAdded_ThenTypesChangedIsRaisedOnce()
    {
        // Arrange
        var provider = new TypeProvider();
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        provider.AddAssemblies([typeof(TypeProvider).Assembly, typeof(TypeProviderTests).Assembly]);

        // Assert
        Assert.Equal(1, raisedCount);
        Assert.Contains(typeof(TypeProvider), provider.Types);
        Assert.Contains(typeof(TypeProviderTests), provider.Types);
    }

    private static Type CreateDynamicType(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule(assemblyName).DefineType(typeName, TypeAttributes.Public).CreateType();
    }
}
