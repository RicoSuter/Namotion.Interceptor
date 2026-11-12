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
        // Act
        var html = await RenderAsync(new Dictionary<string, object?>
        {
            [nameof(MarkdownField.Value)] = "Some **bold** text",
            [nameof(MarkdownField.ToHtml)] = (Func<string, string?>)(text => text),
            ["data-testid"] = "notes-input"
        });

        // Assert
        Assert.Contains("<textarea class=\"markdown-field-source\"", html);
        Assert.Matches("<textarea [^>]*data-testid=\"notes-input\"[^>]*>Some \\*\\*bold\\*\\* text</textarea>", html);
    }

    [Fact]
    public async Task WhenTheTextIsRendered_ThenThePreviewShowsTheHtmlOfToHtml()
    {
        // Act
        var html = await RenderAsync(new Dictionary<string, object?>
        {
            [nameof(MarkdownField.Value)] = "Some **bold** text",
            [nameof(MarkdownField.ToHtml)] = (Func<string, string?>)(_ => "<p>Some <strong>bold</strong> text</p>")
        });

        // Assert
        Assert.Matches("data-testid=\"markdown-field-preview\"[^>]*><p>Some <strong>bold</strong> text</p>", html);
    }

    private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MarkdownField>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
}
