using System.Reflection;
using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Pages whose blocks have no closing line, loaded with the widget types that ship: such a page must
/// show what the same page with every closing line shows.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownShortFormTests
{
    private const string LayoutWithEveryClosingLine = """
        :: HomeBlaze.Components.TabLayout
        ::: Tab#pumps Title="Pumps" Icon=WaterDrop
        :::: HomeBlaze.Components.FlowLayout MinWidth=320
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
        ::::::: HomeBlaze.Components.Widget Path=../WaterPump
        :::::::
        ::::::
        :::::
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
        ::::::: HomeBlaze.Components.Widget Path=../Compressor
        :::::::
        ::::::
        :::::
        ::::
        :::
        ::: Tab#belt Title="Conveyor Belt" Icon=Settings
        The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
        :::
        ::
        """;

    private const string LayoutInTheShortForm = """
        :: HomeBlaze.Components.TabLayout
        ::: Tab#pumps Title="Pumps" Icon=WaterDrop
        :::: HomeBlaze.Components.FlowLayout MinWidth=320
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
        ::::::: HomeBlaze.Components.Widget Path=../WaterPump
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
        ::::::: HomeBlaze.Components.Widget Path=../Compressor
        ::: Tab#belt Title="Conveyor Belt" Icon=Settings
        The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
        """;

    private const string CardWithEveryClosingLine = """
        Text before.

        :: HomeBlaze.Components.Card#overview Title="Motor Card" Icon=Settings
        The motor runs at {{ cardmotor/CurrentSpeed }} RPM.

        ::: HomeBlaze.Samples.Motor#cardmotor Name="Card Motor" TargetSpeed=1200
        :::

        ::: Footer
        Target speed: {{ cardmotor/TargetSpeed }} RPM
        :::
        ::
        """;

    private const string CardInTheShortForm = """
        Text before.

        :: HomeBlaze.Components.Card#overview Title="Motor Card" Icon=Settings
        The motor runs at {{ cardmotor/CurrentSpeed }} RPM.

        ::: HomeBlaze.Samples.Motor#cardmotor Name="Card Motor" TargetSpeed=1200

        ::: Footer
        Target speed: {{ cardmotor/TargetSpeed }} RPM
        """;

    [Theory]
    [InlineData(LayoutWithEveryClosingLine, LayoutInTheShortForm)]
    [InlineData(CardWithEveryClosingLine, CardInTheShortForm)]
    public async Task WhenPageIsWrittenInTheShortForm_ThenItShowsWhatTheSamePageWithEveryClosingLineShows(
        string withEveryClosingLine, string shortForm)
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();
        var serializer = fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>();
        var expectedPage = await fixture.AddPageAsync("Expected.md", withEveryClosingLine);

        // Act
        var page = await fixture.AddPageAsync("Short.md", shortForm);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        Assert.Equal(Describe(expectedPage.Children.Values, serializer), Describe(page.Children.Values, serializer));
        Assert.Contains(PageErrorBoxes.Flatten(page.Children.Values), subject => subject is Card);
    }

    [Fact]
    public async Task WhenLayoutIsWrittenInTheShortForm_ThenEveryBlockFillsTheSlotItIsIn()
    {
        // Arrange
        var fixture = await DocumentationSamplesTests.CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("Short.md", LayoutInTheShortForm);

        // Assert
        var layout = Assert.IsType<TabLayout>(Assert.Single(page.Children.Values, child => child is not HtmlSegment));
        Assert.Equal(["Pumps", "Conveyor Belt"], layout.Tabs.Select(tab => tab.Title));

        var pumps = Assert.IsType<MarkdownFragment>(layout.Tabs[0].Content);
        var flow = Assert.IsType<FlowLayout>(Assert.Single(pumps.Children.Values, child => child is not HtmlSegment));
        Assert.Equal(320, flow.MinWidth);
        Assert.Equal(2, flow.Items.Length);

        string[] expectedTitles = ["Water Pump", "Compressor"];
        string[] expectedPaths = ["../WaterPump", "../Compressor"];
        for (var index = 0; index < flow.Items.Length; index++)
        {
            var item = Assert.IsType<MarkdownFragment>(flow.Items[index]);
            var card = Assert.IsType<Card>(Assert.Single(item.Children.Values, child => child is not HtmlSegment));
            Assert.Equal(expectedTitles[index], card.Title);
            Assert.Null(card.Footer);

            var content = Assert.IsType<MarkdownFragment>(card.Content);
            var widget = Assert.IsType<Widget>(Assert.Single(content.Children.Values, child => child is not HtmlSegment));
            Assert.Equal(expectedPaths[index], widget.Path);
        }

        var belt = Assert.IsType<MarkdownFragment>(layout.Tabs[1].Content);
        Assert.Equal("../Conveyor/CurrentSpeed", Assert.Single(belt.Children.Values.OfType<RenderExpression>()).Path);
        Assert.Same(layout.Tabs[1], ((IPathScope)page).TryGetNamedSubject("belt"));
    }

    [Fact]
    public async Task WhenConfigurationOfABlockInTheShortFormIsWritten_ThenOnlyItsLinesChangeAndThePageKeepsTheShortForm()
    {
        // Arrange
        const string testNamespace = "HomeBlaze.Storage.Tests.Pages";
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $":: {testNamespace}.TestPanel#panel Title=Old\n" +
            "::: Item\n" +
            $":::: {testNamespace}.TestPanel\n" +
            $"::::: {testNamespace}.TestLeaf#leaf Count=1\n" +
            "::: Item\n" +
            $":::: {testNamespace}.TestLeaf#other Count=2\n";
        var page = await fixture.AddPageAsync("page.md", content);
        IPathScope scope = page;
        var leaf = Assert.IsType<TestLeaf>(scope.TryGetNamedSubject("leaf"));
        var headLine = $"::::: {testNamespace}.TestLeaf#leaf";

        // Act
        leaf.Count = 9;
        var isHeadLineWritten = await page.WriteConfigurationAsync(leaf, CancellationToken.None);
        var afterHeadLine = await fixture.ReadAsync("page.md");

        leaf.Tags = ["a"];
        var isJsonBlockWritten = await page.WriteConfigurationAsync(leaf, CancellationToken.None);
        var afterJsonBlock = await fixture.ReadAsync("page.md");
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.True(isHeadLineWritten);
        Assert.Equal(
            content.Replace($"{headLine} Count=1", $"{headLine} Count=9 Enabled=false Interval=00:00:00"),
            afterHeadLine);

        Assert.True(isJsonBlockWritten);
        Assert.Equal(
            content.Replace(
                $"{headLine} Count=1",
                $"{headLine} Count=9 Enabled=false Interval=00:00:00\n```json\n{{\n  \"Tags\": [\n    \"a\"\n  ]\n}}\n```"),
            afterJsonBlock);

        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));
        Assert.Same(leaf, scope.TryGetNamedSubject("leaf"));
        Assert.Equal(2, Assert.IsType<TestLeaf>(scope.TryGetNamedSubject("other")).Count);
        Assert.Equal(2, Assert.IsType<TestPanel>(scope.TryGetNamedSubject("panel")).Items.Length);
    }

    /// <summary>
    /// Gets everything the subjects show, at every depth: the HTML as it is, the path of an expression,
    /// and for every other subject its type, its configuration and what is in each of its slots.
    /// </summary>
    private static string Describe(IEnumerable<IInterceptorSubject?> subjects, ConfigurableSubjectSerializer serializer, int depth = 0)
    {
        var description = new StringBuilder();
        var indentation = new string(' ', depth * 2);
        foreach (var subject in subjects)
        {
            switch (subject)
            {
                case HtmlSegment html:
                    description.Append(indentation).Append("html ").Append(html.Html.Replace("\n", "\\n")).Append('\n');
                    break;

                case RenderExpression expression:
                    description.Append(indentation).Append("expression ").Append(expression.Path).Append('\n');
                    break;

                case MarkdownFragment fragment:
                    description.Append(Describe(fragment.Children.Values, serializer, depth));
                    break;

                case null:
                    break;

                default:
                    description.Append(indentation).Append(serializer.Serialize(subject, ConfigurationNaming.PropertyName).ReplaceLineEndings(" ")).Append('\n');
                    foreach (var slot in subject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (slot.GetCustomAttribute<SlotAttribute>(true) is null)
                        {
                            continue;
                        }

                        IEnumerable<IInterceptorSubject?> values = slot.GetValue(subject) switch
                        {
                            IInterceptorSubject value => [value],
                            IEnumerable<IInterceptorSubject?> list => list,
                            _ => []
                        };

                        foreach (var value in values)
                        {
                            description.Append(indentation).Append("  slot ").Append(slot.Name).Append('\n');
                            description.Append(Describe([value], serializer, depth + 2));
                        }
                    }

                    break;
            }
        }

        return description.ToString();
    }
}
