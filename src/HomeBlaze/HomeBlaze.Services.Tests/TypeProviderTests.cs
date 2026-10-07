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

    [Fact]
    public void WhenSameTypeIsAddedTwiceInOneCall_ThenItIsRegisteredOnce()
    {
        // Arrange
        var provider = new TypeProvider();

        // Act
        var skippedTypes = provider.AddTypes([typeof(string), typeof(string)]);

        // Assert
        Assert.Empty(skippedTypes);
        Assert.Single(provider.Types);
    }

    [Fact]
    public void WhenTwoDifferentTypesShareFullNameInOneCall_ThenTheSecondIsSkipped()
    {
        // Arrange
        var provider = new TypeProvider();
        var firstType = CreateDynamicType("FirstBatchAssembly", "Duplicate.Name.BatchDevice");
        var secondType = CreateDynamicType("SecondBatchAssembly", "Duplicate.Name.BatchDevice");

        // Act
        var skippedTypes = provider.AddTypes([firstType, secondType]);

        // Assert
        Assert.Equal([secondType], skippedTypes);
        Assert.Contains(firstType, provider.Types);
        Assert.DoesNotContain(secondType, provider.Types);
    }

    [Fact]
    public void WhenAllTypesInBatchAreSkipped_ThenTypesChangedIsNotRaisedAndTypesInstanceIsKept()
    {
        // Arrange
        var provider = new TypeProvider();
        var firstType = CreateDynamicType("FirstSkippedAssembly", "Duplicate.Name.SkippedDevice");
        var secondType = CreateDynamicType("SecondSkippedAssembly", "Duplicate.Name.SkippedDevice");
        provider.AddTypes([firstType]);
        var typesBefore = provider.Types;
        var raisedCount = 0;
        provider.TypesChanged += (_, _) => raisedCount++;

        // Act
        var skippedTypes = provider.AddTypes([secondType]);

        // Assert
        Assert.Equal([secondType], skippedTypes);
        Assert.Equal(0, raisedCount);
        Assert.Same(typesBefore, provider.Types);
    }

    [Fact]
    public void WhenHandlerReadsTypesDuringTypesChanged_ThenItSeesTheNewlyAddedType()
    {
        // Arrange
        var provider = new TypeProvider();
        IReadOnlyCollection<Type>? observedTypes = null;
        provider.TypesChanged += (_, _) => observedTypes = provider.Types;

        // Act
        provider.AddTypes([typeof(string)]);

        // Assert
        Assert.NotNull(observedTypes);
        Assert.Contains(typeof(string), observedTypes);
    }

    [Fact]
    public void WhenFirstHandlerThrows_ThenRemainingHandlersRunAndAggregateExceptionIsThrown()
    {
        // Arrange
        var provider = new TypeProvider();
        var secondHandlerRan = false;
        provider.TypesChanged += (_, _) => throw new InvalidOperationException("first handler failure");
        provider.TypesChanged += (_, _) => secondHandlerRan = true;

        // Act & Assert
        Assert.Throws<AggregateException>(() => provider.AddTypes([typeof(string)]));
        Assert.True(secondHandlerRan);
        Assert.Contains(typeof(string), provider.Types);
    }

    [Fact]
    public void WhenTypeHasNullFullName_ThenItIsSkippedAndNotAddedOrReported()
    {
        // Arrange
        var provider = new TypeProvider();
        var genericParameterType = typeof(List<>).GetGenericArguments()[0];

        // Act
        var skippedTypes = provider.AddTypes([genericParameterType]);

        // Assert
        Assert.Empty(skippedTypes);
        Assert.Empty(provider.Types);
    }

    [Fact]
    public void WhenTypeIsRegistered_ThenTryGetTypeFindsItByFullName()
    {
        // Arrange
        var provider = new TypeProvider();
        provider.AddTypes([typeof(TypeProvider)]);

        // Act
        var found = provider.TryGetType(typeof(TypeProvider).FullName!, out var type);

        // Assert
        Assert.True(found);
        Assert.Equal(typeof(TypeProvider), type);
    }

    [Fact]
    public void WhenFullNameIsUnknown_ThenTryGetTypeReturnsFalse()
    {
        // Arrange
        var provider = new TypeProvider();
        provider.AddTypes([typeof(TypeProvider)]);

        // Act
        var found = provider.TryGetType("Unknown.Namespace.MissingType", out var type);

        // Assert
        Assert.False(found);
        Assert.Null(type);
    }

    [Fact]
    public void WhenSecondTypeOfDuplicateFullNameIsSkipped_ThenTryGetTypeReturnsTheFirst()
    {
        // Arrange
        var provider = new TypeProvider();
        var firstType = CreateDynamicType("TryGetTypeFirstAssembly", "Duplicate.Name.TryGetTypeDevice");
        var secondType = CreateDynamicType("TryGetTypeSecondAssembly", "Duplicate.Name.TryGetTypeDevice");
        provider.AddTypes([firstType]);
        provider.AddTypes([secondType]);

        // Act
        var found = provider.TryGetType("Duplicate.Name.TryGetTypeDevice", out var type);

        // Assert
        Assert.True(found);
        Assert.Equal(firstType, type);
    }

    private static Type CreateDynamicType(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule(assemblyName).DefineType(typeName, TypeAttributes.Public).CreateType();
    }
}
