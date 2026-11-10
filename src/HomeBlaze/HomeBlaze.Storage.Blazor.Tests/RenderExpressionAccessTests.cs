using Bunit;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Storage.Blazor.Files;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Blazor.Tests;

public sealed class RenderExpressionAccessTests : BunitContext
{
    private readonly TestUserPermissions _permissions = new();

    public RenderExpressionAccessTests()
    {
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
    }

    [Fact]
    public async Task WhenThePageExpressionReadsAPropertyTheUserMayNotRead_ThenItRendersEmpty()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("{{ door/IsOpen }}");
        _permissions.CannotRead.Add(graph.DoorIsOpen);

        // Act
        var rendered = RenderExpression(graph);

        // Assert
        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public async Task WhenThePageExpressionReadsAPropertyTheUserMayRead_ThenItRenders()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("{{ door/IsOpen }}");

        // Act
        var rendered = RenderExpression(graph);

        // Assert
        Assert.Equal("Yes", rendered);
    }

    [Fact]
    public async Task WhenThePageExpressionReadsAPropertyTheUserMayNotRead_ThenItRendersAsForAPathThatLeadsNowhere()
    {
        // Arrange
        var readableGraph = await PageTestGraph.CreateAsync("{{ door/IsOpen | map:state }}");
        var hiddenGraph = await PageTestGraph.CreateAsync("{{ door/IsOpen | map:state }}");
        var missingGraph = await PageTestGraph.CreateAsync("{{ door/Nothing | map:state }}");
        _permissions.CannotRead.Add(hiddenGraph.DoorIsOpen);

        // Act
        var readable = RenderExpression(readableGraph);
        var hidden = RenderExpression(hiddenGraph);
        var missing = RenderExpression(missingGraph);

        // Assert
        Assert.Equal("open", readable);
        Assert.Equal("unknown", missing);
        Assert.Equal(missing, hidden);
    }

    [Fact]
    public async Task WhenACaseOfTheValueMapReadsAPropertyTheUserMayNotRead_ThenTheMapRendersAsForACaseWithAPathThatLeadsNowhere()
    {
        // Arrange
        var readableGraph = await PageTestGraph.CreateAsync("{{ door/IsOpen | map:lock }}");
        var hiddenGraph = await PageTestGraph.CreateAsync("{{ door/IsOpen | map:lock }}");
        var missingGraph = await PageTestGraph.CreateAsync("{{ door/IsOpen | map:wronglock }}");
        _permissions.CannotRead.Add(hiddenGraph.DoorIsLocked);

        // Act
        var readable = RenderExpression(readableGraph);
        var hidden = RenderExpression(hiddenGraph);
        var missing = RenderExpression(missingGraph);

        // Assert
        Assert.Equal("locked", readable);
        Assert.Equal(missing, hidden);
        Assert.DoesNotContain("locked", hidden);
    }

    [Theory]
    [InlineData("{{ door/IsOpen || door/IsLocked }}", "{{ door/Nothing || door/IsLocked }}")]
    [InlineData("{{ door/IsLocked || door/IsOpen }}", "{{ door/IsLocked || door/Nothing }}")]
    [InlineData("{{ door/IsOpen || not door/IsLocked }}", "{{ door/Nothing || not door/IsLocked }}")]
    [InlineData("{{ not door/IsLocked || door/IsOpen }}", "{{ not door/IsLocked || door/Nothing }}")]
    [InlineData("{{ door/IsOpen && door/IsLocked }}", "{{ door/Nothing && door/IsLocked }}")]
    [InlineData("{{ door/IsOpen == door/IsLocked }}", "{{ door/Nothing == door/IsLocked }}")]
    [InlineData("{{ door/IsOpen != door/IsLocked }}", "{{ door/Nothing != door/IsLocked }}")]
    public async Task WhenOneOperandOfAnExpressionIsAPropertyTheUserMayNotRead_ThenItRendersAsForThatOperandLeadingNowhere(string text, string missingText)
    {
        // Arrange
        var hiddenGraph = await PageTestGraph.CreateAsync(text);
        var missingGraph = await PageTestGraph.CreateAsync(missingText);
        _permissions.CannotRead.Add(hiddenGraph.DoorIsOpen);

        // Act
        var hidden = RenderExpression(hiddenGraph);
        var missing = RenderExpression(missingGraph);

        // Assert
        Assert.Equal(missing, hidden);
    }

    [Fact]
    public async Task WhenTheUserMayReadBothOperands_ThenTheExpressionRendersOtherwiseThanWithAnOperandThatLeadsNowhere()
    {
        // Arrange
        var readableGraph = await PageTestGraph.CreateAsync("{{ door/IsOpen && door/IsLocked }}");
        var missingGraph = await PageTestGraph.CreateAsync("{{ door/Nothing && door/IsLocked }}");

        // Act
        var readable = RenderExpression(readableGraph);
        var missing = RenderExpression(missingGraph);

        // Assert
        Assert.Equal("Yes", readable);
        Assert.NotEqual(missing, readable);
    }

    [Fact]
    public async Task WhenTheUserMayNoLongerReadTheProperty_ThenTheExpressionRendersEmpty()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync("{{ door/IsOpen }}");
        var rendered = Render<RenderExpressionComponent>(parameters => parameters
            .Add(component => component.Subject, PageTestGraph.GetExpression(graph.Page)));
        Assert.Equal("Yes", rendered.Markup.Trim());

        // Act
        _permissions.CannotRead.Add(graph.DoorIsOpen);
        _permissions.RaiseChanged();

        // Assert
        rendered.WaitForAssertion(() => Assert.Equal(string.Empty, rendered.Markup.Trim()));
    }

    [Fact]
    public async Task WhenAPageShowsAnExpressionTheUserMayNotRead_ThenThePageRendersAsForAPathThatLeadsNowhere()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync(string.Empty);
        var page = await graph.AddPageAsync("view.md", "Door: [{{ /page.md/door/IsOpen }}]");
        var missingPage = await graph.AddPageAsync("missing.md", "Door: [{{ /page.md/door/Nothing }}]");
        var readable = RenderContent(page);
        _permissions.CannotRead.Add(graph.DoorIsOpen);

        // Act
        var hidden = RenderContent(page);
        var missing = RenderContent(missingPage);

        // Assert
        Assert.Contains("Door: [Yes]", readable);
        Assert.Contains("Door: []", hidden);
        Assert.Equal(missing, hidden);
    }

    [Fact]
    public async Task WhenTheUserMayNoLongerReadThePropertyThatAPageShows_ThenThePageRendersItEmpty()
    {
        // Arrange
        var graph = await PageTestGraph.CreateAsync(string.Empty);
        var page = await graph.AddPageAsync("view.md", "Door: [{{ /page.md/door/IsOpen }}]");
        var rendered = Render<MarkdownContentView>(parameters => parameters
            .Add(component => component.Content, page)
            .Add(component => component.Children, page.Children));
        Assert.Contains("Door: [Yes]", rendered.Markup);

        // Act
        _permissions.CannotRead.Add(graph.DoorIsOpen);
        _permissions.RaiseChanged();

        // Assert
        rendered.WaitForAssertion(() => Assert.Contains("Door: []", rendered.Markup));
    }

    private string RenderExpression(PageTestGraph graph) =>
        Render<RenderExpressionComponent>(parameters => parameters
            .Add(component => component.Subject, PageTestGraph.GetExpression(graph.Page))).Markup.Trim();

    private string RenderContent(MarkdownFile page) =>
        Render<MarkdownContentView>(parameters => parameters
            .Add(component => component.Content, page)
            .Add(component => component.Children, page.Children)).Markup;
}
