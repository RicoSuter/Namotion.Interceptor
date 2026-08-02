using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

public class ContextFunctionCacheTests
{
    private static readonly MethodInfo ExerciseFunctionCachesMethod = typeof(ContextFunctionCacheTests)
        .GetMethod(nameof(ExerciseFunctionCaches), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{nameof(ExerciseFunctionCaches)} was renamed, this test needs updating.");

    [Fact]
    public async Task WhenManyPropertyTypesGrowFunctionArraysConcurrently_ThenEveryEntryCanBeRebuilt()
    {
        // Arrange: nested generic types provide distinct runtime property types without adding a
        // source-generated property for every slot. They race both arrays on one state.
        const int propertyTypeCount = 32;
        var propertyTypes = CreatePropertyTypes(propertyTypeCount);
        var subject = new ContextProbeSubject();
        var executor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)subject).Context);

        using var ready = new CountdownEvent(propertyTypeCount);
        using var start = new ManualResetEventSlim(false);
        var builders = propertyTypes
            .Select(propertyType => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                start.Wait();
                ExerciseFunctionCachesMethod.MakeGenericMethod(propertyType).Invoke(null, [executor]);
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        // Act: a growth may copy an array while another thread fills an element in the old one. A
        // lost element is allowed, so repeat each access after the race to force its rebuild.
        ready.Wait();
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => builders.All(builder => builder.IsCompleted),
            message: "Concurrent property function cache builders did not finish");
        await Task.WhenAll(builders);

        foreach (var propertyType in propertyTypes)
        {
            ExerciseFunctionCachesMethod.MakeGenericMethod(propertyType).Invoke(null, [executor]);
        }

        // Assert
        var readFunctions = GetReadFunctions(executor);
        var writeFunctions = GetWriteFunctions(executor);
        Assert.NotNull(readFunctions);
        Assert.NotNull(writeFunctions);

        foreach (var propertyType in propertyTypes)
        {
            var propertyTypeIndex = GetPropertyTypeIndex(propertyType);
            Assert.NotNull(readFunctions[propertyTypeIndex]);
            Assert.NotNull(writeFunctions[propertyTypeIndex]);
        }
    }

    [Fact]
    public void WhenFreshContextFirstSeesHighPropertyTypeIndex_ThenItsArrayCoversThatIndex()
    {
        // Arrange: assign a run of process-wide indices, then let a fresh context see only the last
        // one. Its array is intentionally indexed by the largest global index, not its local count.
        const int propertyTypeCount = 16;
        var propertyTypes = CreateHighIndexPropertyTypes(propertyTypeCount);
        var propertyTypeIndices = propertyTypes.Select(GetPropertyTypeIndex).ToArray();
        var highPropertyType = propertyTypes[^1];
        var highPropertyTypeIndex = propertyTypeIndices[^1];
        var subject = new ContextProbeSubject();
        var executor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)subject).Context);

        // Act
        ExerciseFunctionCachesMethod.MakeGenericMethod(highPropertyType).Invoke(null, [executor]);

        // Assert
        var readFunctions = Assert.IsType<Delegate?[]>(GetReadFunctions(executor));
        var writeFunctions = Assert.IsType<Delegate?[]>(GetWriteFunctions(executor));
        Assert.True(readFunctions.Length > highPropertyTypeIndex);
        Assert.True(writeFunctions.Length > highPropertyTypeIndex);
        Assert.Single(readFunctions, function => function is not null);
        Assert.Single(writeFunctions, function => function is not null);
    }

    [Fact]
    public void WhenMethodInvocationFunctionIsAlreadySet_ThenTheCanonicalFunctionIsReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        Action first = static () => { };
        Action second = static () => { };

        // Act
        var firstResult = GetOrSetMethodInvocationFunction(context, first);
        var secondResult = GetOrSetMethodInvocationFunction(context, second);

        // Assert
        Assert.Same(first, firstResult);
        Assert.Same(first, secondResult);
    }

    private static Type[] CreatePropertyTypes(int count)
    {
        return CreatePropertyTypes(count, typeof(PropertyType<>), typeof(PropertyTypeRoot));
    }

    private static Type[] CreateHighIndexPropertyTypes(int count)
    {
        return CreatePropertyTypes(count, typeof(HighIndexPropertyType<>), typeof(HighIndexPropertyTypeRoot));
    }

    private static Type[] CreatePropertyTypes(int count, Type openGenericType, Type rootType)
    {
        var result = new Type[count];
        var propertyType = rootType;
        for (var index = 0; index < count; index++)
        {
            propertyType = openGenericType.MakeGenericType(propertyType);
            result[index] = propertyType;
        }

        return result;
    }

    private static void ExerciseFunctionCaches<TProperty>(InterceptorExecutor executor)
    {
        _ = executor.GetPropertyValue<TProperty>(nameof(ContextProbeSubject.Value), static _ => default!);
        executor.SetPropertyValue<TProperty>(
            nameof(ContextProbeSubject.Value),
            default!,
            default!,
            static (_, _) => { });
    }

    private sealed class PropertyType<TProperty>;

    private sealed class PropertyTypeRoot;

    private sealed class HighIndexPropertyType<TProperty>;

    private sealed class HighIndexPropertyTypeRoot;
}
