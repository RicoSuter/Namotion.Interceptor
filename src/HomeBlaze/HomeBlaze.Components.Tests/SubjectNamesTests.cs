using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectNamesTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithRegistry();

    [Fact]
    public void WhenTypeIsNotGeneric_ThenTypeNameIsTheFullName()
    {
        // Act
        var name = SubjectNames.GetTypeName(typeof(Switch));

        // Assert
        Assert.Equal("HomeBlaze.Components.Switch", name);
    }

    [Fact]
    public void WhenTypeIsGeneric_ThenTypeNameShowsItsArgumentsWithoutArity()
    {
        // Act
        var name = SubjectNames.GetTypeName(typeof(Dictionary<string, List<int>>));

        // Assert
        Assert.Equal("System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>", name);
    }

    [Fact]
    public void WhenTypeIsANestedGenericType_ThenTypeNameStartsWithTheDeclaringType()
    {
        // Act
        var name = SubjectNames.GetTypeName(typeof(NestedGeneric<int>));

        // Assert
        Assert.Equal("HomeBlaze.Components.Tests.SubjectNamesTests+NestedGeneric<System.Int32>", name);
    }

    [Fact]
    public void WhenTypeIsAnOpenGenericType_ThenTypeNameShowsItsParameters()
    {
        // Act
        var name = SubjectNames.GetTypeName(typeof(List<>));

        // Assert
        Assert.Equal("System.Collections.Generic.List<T>", name);
    }

    [Fact]
    public void WhenSubjectHasATitle_ThenTitleIsReturned()
    {
        // Arrange
        var card = new Card(_context) { Title = "Overview" };

        // Act
        var title = SubjectNames.TryGetTitle(card);

        // Assert
        Assert.Equal("Overview", title);
    }

    [Fact]
    public void WhenSubjectHasALabelButNoTitle_ThenLabelIsReturned()
    {
        // Arrange
        var widget = new Switch(_context) { Label = "Fan" };

        // Act
        var title = SubjectNames.TryGetTitle(widget);

        // Assert
        Assert.Equal("Fan", title);
    }

    [Fact]
    public void WhenSubjectHasNeitherTitleNorLabel_ThenNoTitleIsReturned()
    {
        // Arrange
        var widget = new Switch(_context);
        var layout = new FlowLayout(_context);

        // Act
        var widgetTitle = SubjectNames.TryGetTitle(widget);
        var layoutTitle = SubjectNames.TryGetTitle(layout);

        // Assert
        Assert.Null(widgetTitle);
        Assert.Null(layoutTitle);
    }

    private sealed class NestedGeneric<T>;
}
