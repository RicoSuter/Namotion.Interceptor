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
}
