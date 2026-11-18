using HomeBlaze.Host.Services.Navigation;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class SubjectChainLinksTests
{
    private readonly ChainTestNode _root = new();
    private readonly ChainTestNode _child = new();
    private readonly ChainTestNode _grandchild = new();

    private SubjectChainLinks CreateLinks() => SubjectChainLinks.Create(
        [(Subject: _root, PropertyName: ""), (Subject: _child, PropertyName: nameof(ChainTestNode.Child)), (Subject: _grandchild, PropertyName: nameof(ChainTestNode.Other))],
        static item => item.Subject,
        static item => item.PropertyName);

    [Fact]
    public void WhenPropertyReachesTheNextSubjectOfTheChain_ThenItIsALink()
    {
        // Arrange
        var links = CreateLinks();

        // Act & Assert
        Assert.True(links.Contains(new PropertyReference(_root, nameof(ChainTestNode.Child))));
        Assert.True(links.Contains(new PropertyReference(_child, nameof(ChainTestNode.Other))));
    }

    [Fact]
    public void WhenPropertyOfASubjectOfTheChainReachesNoSubjectOfIt_ThenItIsNoLink()
    {
        // Arrange
        var links = CreateLinks();

        // Act & Assert
        Assert.False(links.Contains(new PropertyReference(_root, nameof(ChainTestNode.Other))));
        Assert.False(links.Contains(new PropertyReference(_child, nameof(ChainTestNode.Child))));
        Assert.False(links.Contains(new PropertyReference(_root, nameof(ChainTestNode.Name))));
    }

    [Fact]
    public void WhenPropertyIsOnTheLastSubjectOfTheChain_ThenItIsNoLink()
    {
        // Arrange
        var links = CreateLinks();

        // Act & Assert
        Assert.False(links.Contains(new PropertyReference(_grandchild, nameof(ChainTestNode.Child))));
        Assert.False(links.Contains(new PropertyReference(_grandchild, nameof(ChainTestNode.Other))));
    }

    [Fact]
    public void WhenSubjectIsNotInTheChain_ThenItsPropertyOfALinkingNameIsNoLink()
    {
        // Arrange
        var links = CreateLinks();
        var other = new ChainTestNode();

        // Act & Assert
        Assert.False(links.Contains(new PropertyReference(other, nameof(ChainTestNode.Child))));
    }

    [Fact]
    public void WhenChainHasOneSubject_ThenItHasNoLinks()
    {
        // Act
        var links = SubjectChainLinks.Create([_root], static subject => subject, static _ => "");

        // Assert
        Assert.Same(SubjectChainLinks.Empty, links);
        Assert.False(links.Contains(new PropertyReference(_root, nameof(ChainTestNode.Child))));
    }
}

[InterceptorSubject]
public partial class ChainTestNode
{
    public partial string? Name { get; set; }

    public partial ChainTestNode? Child { get; set; }

    public partial ChainTestNode? Other { get; set; }
}
