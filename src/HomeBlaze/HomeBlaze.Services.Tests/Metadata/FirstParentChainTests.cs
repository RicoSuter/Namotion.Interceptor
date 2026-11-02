using HomeBlaze.Services.Metadata;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests.Metadata;

public class FirstParentChainTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static List<IInterceptorSubject> Enumerate(IInterceptorSubject subject)
    {
        var subjects = new List<IInterceptorSubject>();
        foreach (var current in new FirstParentChain(subject.TryGetRegisteredSubject()!))
        {
            subjects.Add(current.Subject);
        }

        return subjects;
    }

    [Fact]
    public void WhenSubjectsAreNested_ThenTheChainIsTheSubjectAndTheFirstParentOfEachUpToTheRoot()
    {
        // Arrange
        var leaf = new TestContainer { Name = "Leaf" };
        var middle = new TestContainer { Name = "Middle", Child = leaf };
        var root = new TestContainer(CreateContext()) { Name = "Root", Child = middle };
        root.Children["Other"] = leaf;

        // Act
        var chain = Enumerate(leaf);

        // Assert
        Assert.Equal([leaf, middle, root], chain);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    public void WhenFirstParentsFormACycle_ThenTheChainEndsBeforeASubjectComesAgain(int length)
    {
        // Arrange: subjects that only reference each other, held in the graph by an anchor on one of them, have
        // each other as their only parents, so their first parents form a cycle.
        var nodes = Enumerable.Range(0, length).Select(index => new TestContainer { Name = $"Node {index}" }).ToArray();
        for (var index = 0; index < length - 1; index++)
        {
            nodes[index].Child = nodes[index + 1];
        }

        nodes[^1].Child = nodes[0];
        ((IInterceptorSubject)nodes[0]).AttachToContext(CreateContext());

        // Act
        var chain = Enumerate(nodes[^1]);

        // Assert
        Assert.Equal(nodes.Reverse(), chain);
    }
}
