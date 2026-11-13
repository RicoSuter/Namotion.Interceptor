using System.Reflection;
using Namotion.Interceptor.Authorization.Internal;
using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class EvaluationStateTests
{
    [Fact]
    public void WhenPathsGrewTheStateAndARetryClearedIt_ThenReturnDropsTheState()
    {
        // Arrange
        var context = CreateContext();
        var current = new Person(context);
        for (var i = 0; i < 30; i++)
        {
            var next = new Person(context);
            current.Father = next;
            current = next;
        }

        // Six stacked diamonds give the leaf 64 paths of 36 links each.
        for (var i = 0; i < 6; i++)
        {
            var next = new Person(context);
            current.Father = next;
            current.Mother = next;
            current = next;
        }

        var state = EvaluationState.Rent();
        var paths = state.BuildPaths(Registered(current), DefaultPathProvider.Instance, maxPaths: 64);
        Assert.Equal(64, paths.Count);
        Assert.True(state.Paths.IsLargerThan(EvaluationState.MaxCachedCapacity, EvaluationState.MaxCachedLinks));
        state.Clear();

        // Act
        EvaluationState.Return(state);

        // Assert
        Assert.NotSame(state, EvaluationState.Rent());
    }

    [Fact]
    public void WhenAStateBuiltPathsAndIsReturned_ThenItIsCachedAndForgetsThatPathsWereBuilt()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var leaf = new Person();
        root.Father = leaf;
        var state = EvaluationState.Rent();
        var paths = state.BuildPaths(Registered(leaf), DefaultPathProvider.Instance, maxPaths: 64);
        var hasBuiltPathsBeforeReturn = HasBuiltPathsSinceRent(state);
        Assert.Equal(1, paths.Count);

        // Act
        EvaluationState.Return(state);
        var rented = EvaluationState.Rent();

        // Assert
        Assert.True(hasBuiltPathsBeforeReturn);
        Assert.Same(state, rented);
        Assert.False(HasBuiltPathsSinceRent(rented));
        Assert.Equal(0, rented.Paths.Count);
    }

    private static bool HasBuiltPathsSinceRent(EvaluationState state)
    {
        var field = typeof(EvaluationState).GetField("_hasBuiltPathsSinceRent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null, "EvaluationState no longer has a field _hasBuiltPathsSinceRent; update this test to the new name of the built-since-rent flag.");
        return (bool)field.GetValue(state)!;
    }
}
