using HomeBlaze.Components.Inputs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class MarkdownFieldTests
{
    [Fact]
    public async Task WhenTheHostSetsATestId_ThenTheTextAreaHasIt()
    {
        // Arrange
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(MarkdownField.Value)] = "Some **bold** text",
            [nameof(MarkdownField.ToHtml)] = (Func<string, string?>)(_ => "<p>Some <strong>bold</strong> text</p>"),
            ["data-testid"] = "notes-input"
        });

        // Act
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MarkdownField>(parameters)).ToHtmlString());

        // Assert
        Assert.Contains("<textarea class=\"markdown-field-source\"", html);
        Assert.Matches("<textarea [^>]*data-testid=\"notes-input\"[^>]*>Some \\*\\*bold\\*\\* text</textarea>", html);
        Assert.Contains("<p>Some <strong>bold</strong> text</p>", html);
    }
}
