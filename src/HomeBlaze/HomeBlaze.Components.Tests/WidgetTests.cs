using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class WidgetTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle();

    private static Widget CreateWidget() => new(new SubjectPathResolver(() => null));

    [Fact]
    public void WhenSubjectIsTheWidgetItself_ThenItIsSelfOrAncestor()
    {
        // Arrange
        var widget = CreateWidget();
        _ = new Card(_context) { Content = widget };

        // Act
        var result = widget.IsSelfOrAncestor(widget);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenSubjectContainsTheWidgetAtAnyDepth_ThenItIsSelfOrAncestor()
    {
        // Arrange
        var widget = CreateWidget();
        var inner = new Card(_context) { Footer = widget };
        var outer = new Card(_context) { Content = inner };

        // Act
        var innerResult = widget.IsSelfOrAncestor(inner);
        var outerResult = widget.IsSelfOrAncestor(outer);

        // Assert
        Assert.True(innerResult);
        Assert.True(outerResult);
    }

    [Fact]
    public void WhenSubjectDoesNotContainTheWidget_ThenItIsNotSelfOrAncestor()
    {
        // Arrange
        var widget = CreateWidget();
        var sibling = new Card(_context);
        var outer = new Card(_context)
        {
            Content = new Card(_context) { Content = widget },
            Footer = sibling
        };

        // Act
        var result = widget.IsSelfOrAncestor(sibling);

        // Assert
        Assert.False(result);
        Assert.True(widget.IsSelfOrAncestor(outer));
    }

    [Fact]
    public void WhenAnAncestorHasSeveralParents_ThenEveryBranchIsSearched()
    {
        // Arrange
        var widget = CreateWidget();
        var shared = new Card(_context) { Content = widget };
        var first = new Card(_context) { Content = shared };
        var second = new Card(_context) { Footer = shared };
        var root = new Card(_context) { Content = second };
        var unrelated = new Card(_context);

        // Act
        var firstResult = widget.IsSelfOrAncestor(first);
        var secondResult = widget.IsSelfOrAncestor(second);
        var rootResult = widget.IsSelfOrAncestor(root);
        var unrelatedResult = widget.IsSelfOrAncestor(unrelated);

        // Assert
        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.True(rootResult);
        Assert.False(unrelatedResult);
    }

    [Fact]
    public void WhenParentsFormACycle_ThenTheSearchEnds()
    {
        // Arrange
        var widget = CreateWidget();
        var first = new Card(_context) { Content = widget };
        var second = new Card(_context) { Content = first };
        first.Footer = second;
        var unrelated = new Card(_context);

        // Act
        var result = widget.IsSelfOrAncestor(unrelated);

        // Assert
        Assert.False(result);
        Assert.True(widget.IsSelfOrAncestor(second));
    }

    [Fact]
    public void WhenTwoWidgetsShowTheCardOfEachOther_ThenNeitherCardContainsTheOtherWidget()
    {
        // Arrange
        var first = new Card(_context);
        var second = new Card(_context);
        var root = new Card(_context) { Content = first, Footer = second };
        var resolver = new SubjectPathResolver(() => root);
        var firstWidget = new Widget(resolver);
        var secondWidget = new Widget(resolver);
        first.Content = firstWidget;
        second.Content = secondWidget;

        // Set inside the graph, so that both references are calculated again and published as changes.
        firstWidget.Path = "/Footer";
        secondWidget.Path = "/Content";

        // Act
        var otherCardResult = firstWidget.IsSelfOrAncestor(second);
        var otherWidgetResult = firstWidget.IsSelfOrAncestor(secondWidget);
        var ownCardResult = firstWidget.IsSelfOrAncestor(first);
        var rootResult = firstWidget.IsSelfOrAncestor(root);

        // Assert: showing a subject does not make a widget its parent, so the cycle of the two references
        // is not in the parents and is left to the component that renders them.
        Assert.Same(second, firstWidget.ResolvedSubject);
        Assert.Same(first, secondWidget.ResolvedSubject);
        Assert.DoesNotContain(first.GetParents(), parent => parent.Property.Subject is Widget);
        Assert.DoesNotContain(second.GetParents(), parent => parent.Property.Subject is Widget);
        Assert.False(otherCardResult);
        Assert.False(otherWidgetResult);
        Assert.True(ownCardResult);
        Assert.True(rootResult);
    }

    [Fact]
    public void WhenWidgetHasNoParent_ThenOnlyTheWidgetItselfIsSelfOrAncestor()
    {
        // Arrange
        var widget = CreateWidget();
        var card = new Card(_context);

        // Act
        var cardResult = widget.IsSelfOrAncestor(card);
        var selfResult = widget.IsSelfOrAncestor(widget);

        // Assert
        Assert.False(cardResult);
        Assert.True(selfResult);
    }
}
