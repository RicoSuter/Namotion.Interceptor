using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Markdig.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Loads every sample of page source in the documentation as a page with the widget types that ship
/// and with the widget that the documentation builds, so that a sample which the code no longer
/// accepts is found here and not by the reader who copies it.
/// </summary>
/// <remarks>
/// A sample of page source is a fenced code block with the language <c>markdown</c>. A sample that is
/// meant to show an error has the comment <see cref="ErrorMarker"/> on the line above it, and the fenced
/// code block with the language <c>text</c> after it lists the error boxes as the page shows them, one
/// per line. A sample that the page shows at work has the comment <see cref="LiveMarker"/> on the line
/// above it, and the lines after it are the same lines again as blocks of the page.
/// </remarks>
[Collection(MarkdownPageFixture.StorageCollection)]
public partial class DocumentationSamplesTests
{
    private const string DocumentationFolder = "src/HomeBlaze/HomeBlaze/Data/Files/Docs";
    private const string SampleLanguage = "markdown";
    private const string ExpectedErrorsLanguage = "text";
    private const string ErrorMarker = "<!-- sample: error -->";
    private const string LiveMarker = "<!-- sample: live -->";

    // Every sample of this page is shown at work or shows an error, so each one needs one of the two markers.
    private const string PageWidgetsPage = DocumentationFolder + "/administration/page-widgets.md";

    // The types of this file are in the test assembly, which every fixture knows.
    private const string SampleWidgetSource = "src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/DocumentationSampleWidget.cs";
    private const string SampleWidgetDocumentation = DocumentationFolder + "/development/building-subjects.md";

    private static readonly Lazy<IReadOnlyList<Sample>> AllSamples = new(ReadSamples);

    [GeneratedRegex(@"^Line (\d+): ")]
    private static partial Regex BoxLineRegex();

    public static TheoryData<string, int> ValidSamples() => ToTheoryData(sample => sample.ExpectedErrors is null);

    public static TheoryData<string, int> ErrorSamples() => ToTheoryData(sample => sample.ExpectedErrors is not null);

    public static TheoryData<string, int> LiveSamples() => ToTheoryData(sample => sample.IsLive);

    public static TheoryData<string, int> ValidSamplesWithJsonBlock() => ToTheoryData(sample =>
        sample.ExpectedErrors is null &&
        MarkdownPageParser.Parse(sample.Source).AllBlocks.Any(block => block.ConfigurationJson is not null));

    public static TheoryData<string, int> ValidSamplesWithPathsThatStartAtThePage() => ToTheoryData(sample =>
        sample.ExpectedErrors is null && FindPathsThatStartAtThePage(MarkdownPageParser.Parse(sample.Source)).Any());

    [Fact]
    public void WhenDocumentationIsRead_ThenEveryFenceOfPageSourceIsFoundAsASample()
    {
        // Arrange
        var root = RepositoryPagesTests.FindRepositoryRoot();
        var fenceLineCount = Directory
            .EnumerateFiles(Path.Combine(root, DocumentationFolder), "*.md", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines)
            .Count(line => line.TrimStart().StartsWith("~~~~" + SampleLanguage, StringComparison.Ordinal));

        // Act
        var samples = AllSamples.Value;

        // Assert
        Assert.Equal(fenceLineCount, samples.Count);
        Assert.Contains(samples, sample => sample.ExpectedErrors is null);
        Assert.Contains(samples, sample => sample.ExpectedErrors is not null);
    }

    [Fact]
    public void WhenDocumentationShowsTheSampleWidget_ThenItIsTheCodeThatIsCompiledHere()
    {
        // Arrange
        var source = RepositoryPagesTests.ReadPage(SampleWidgetSource);
        var code = source[(source.IndexOf("\nusing ", StringComparison.Ordinal) + 1)..].TrimEnd('\n');

        // Act
        var documentation = RepositoryPagesTests.ReadPage(SampleWidgetDocumentation);

        // Assert
        Assert.Contains($"```csharp\n{code}\n```", documentation);
    }

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public async Task WhenSampleIsLoadedAsAPage_ThenItShowsNoErrorAndEveryWidgetBlockCreatesItsSubject(string relativePath, int line)
    {
        // Arrange
        var sample = FindSample(relativePath, line);
        var fixture = await CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("Sample.md", sample.Source);

        // Assert
        Assert.Empty(PageErrorBoxes.FindSortedByLine(page));

        // A sample that the page reads as plain text has no error box either.
        var widgetBlocks = MarkdownPageParser.Parse(sample.Source).AllBlocks.Where(block => block.Kind == MarkdownBlockKind.Widget);
        Assert.All(widgetBlocks, block => Assert.NotNull(page.GetBlockSubject(block.Index)));
    }

    [Theory]
    [MemberData(nameof(ValidSamplesWithJsonBlock))]
    public async Task WhenSampleHasAJsonBlock_ThenEveryNameInItIsReadIntoTheSubject(string relativePath, int line)
    {
        // Arrange
        var sample = FindSample(relativePath, line);
        var fixture = await CreateFixtureAsync();
        var serializer = fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>();

        // Act
        var page = await fixture.AddPageAsync("Sample.md", sample.Source);

        // Assert
        // Below the top level of a json block a name that is no property is skipped without an error box,
        // so the names are compared with what the subject gives back.
        var namesNotRead = new List<string>();
        foreach (var block in MarkdownPageParser.Parse(sample.Source).AllBlocks.Where(block => block.ConfigurationJson is not null))
        {
            var subject = page.GetBlockSubject(block.Index);
            Assert.NotNull(subject);

            var read = JsonNode.Parse(serializer.Serialize(subject, ConfigurationNaming.PropertyName));
            namesNotRead.AddRange(FindNamesNotRead(JsonNode.Parse(block.ConfigurationJson!), read, $"line {block.Line}: "));
        }

        Assert.Empty(namesNotRead);
    }

    [Theory]
    [MemberData(nameof(ErrorSamples))]
    public async Task WhenSampleThatIsMarkedAsAnErrorIsLoadedAsAPage_ThenItShowsTheErrorBoxesThatTheDocumentationLists(string relativePath, int line)
    {
        // Arrange
        var sample = FindSample(relativePath, line);
        var fixture = await CreateFixtureAsync();

        // Act
        var page = await fixture.AddPageAsync("Sample.md", sample.Source);

        // Assert
        Assert.NotEmpty(sample.ExpectedErrors!);
        Assert.Equal(sample.ExpectedErrors, PageErrorBoxes.FindSortedByLine(page));
    }

    [Theory]
    [MemberData(nameof(LiveSamples))]
    public void WhenSampleIsMarkedAsLive_ThenTheLinesAfterItRepeatItExactly(string relativePath, int line)
    {
        // Arrange
        var pageLines = RepositoryPagesTests.ReadPage(relativePath).Split('\n');

        // Act
        var difference = FindLiveBlockDifference(relativePath, pageLines, line);

        // Assert
        Assert.True(difference is null, difference);
    }

    // Text of the page follows every live block. Without the closing line it would be inside the block.
    [Theory]
    [MemberData(nameof(LiveSamples))]
    public void WhenSampleIsMarkedAsLive_ThenItsLastBlockEndsWithItsClosingLine(string relativePath, int line)
    {
        // Arrange
        var sample = FindSample(relativePath, line);

        // Act
        var blocks = MarkdownPageParser.Parse(sample.Source).Blocks;

        // Assert
        Assert.NotEmpty(blocks);
        Assert.True(blocks[^1].HasClosingLine, $"The block at line {blocks[^1].Line} of the sample has no closing line.");
    }

    [Fact]
    public void WhenPageWidgetsPageHasASample_ThenTheSampleIsMarkedAsLiveOrAsAnError()
    {
        // Arrange
        var samples = AllSamples.Value.Where(sample => sample.RelativePath == PageWidgetsPage).ToList();

        // Act
        var samplesWithoutMarker = samples
            .Where(sample => !sample.IsLive && sample.ExpectedErrors is null)
            .Select(sample => $"line {sample.Line}");

        // Assert
        Assert.NotEmpty(samples);
        Assert.Empty(samplesWithoutMarker);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("")]
    public void WhenLiveBlockRepeatsItsSample_ThenNoDifferenceIsFound(string separator)
    {
        // Arrange
        var pageLines = $"~~~~markdown\n:: Sample.Card\nText\n::\n~~~~\n{separator}:: Sample.Card\nText\n::\n\nMore text".Split('\n');

        // Act
        var difference = FindLiveBlockDifference("Docs/page.md", pageLines, sampleLine: 1);

        // Assert
        Assert.Null(difference);
    }

    [Fact]
    public void WhenLiveBlockDiffersFromItsSample_ThenThePageTheLineAndTheFirstDifferingLineAreNamed()
    {
        // Arrange
        var pageLines = "Text\n\n~~~~markdown\n:: Sample.Card\nText\nMore\n::\n~~~~\n\n:: Sample.Card\nOther text\nOther\n::".Split('\n');

        // Act
        var difference = FindLiveBlockDifference("Docs/page.md", pageLines, sampleLine: 3);

        // Assert
        Assert.Equal("Docs/page.md line 11: expected 'Text', which is line 5 of the page, but found 'Other text'.", difference);
    }

    [Fact]
    public void WhenLiveBlockHasALineMoreThanItsSample_ThenTheLineIsNamed()
    {
        // Arrange
        var pageLines = "~~~~markdown\n:: Sample.Card\nText\n~~~~\n\n:: Sample.Card\nText\n::\n\nMore text".Split('\n');

        // Act
        var difference = FindLiveBlockDifference("Docs/page.md", pageLines, sampleLine: 1);

        // Assert
        Assert.Equal("Docs/page.md line 8: expected an empty line after the live block, but found '::'.", difference);
    }

    [Fact]
    public void WhenPageEndsInsideTheLiveBlock_ThenTheMissingLineIsNamed()
    {
        // Arrange
        var pageLines = "~~~~markdown\n:: Sample.Card\nText\n::\n~~~~\n\n:: Sample.Card\nText".Split('\n');

        // Act
        var difference = FindLiveBlockDifference("Docs/page.md", pageLines, sampleLine: 1);

        // Assert
        Assert.Equal("Docs/page.md line 9: expected '::', which is line 4 of the page, but the page ends there.", difference);
    }

    // A path with a prefix leads out of the sample, to the files next to the page or to the root, so a
    // sample alone cannot say whether it is right. Every other path starts at the page: at a block that the
    // sample names, or at a property of the page. A live sample may also use a name of another live sample
    // of its page, so its paths are looked up on that page, where its lines stand as blocks.
    // A valid sample that shows a path to nothing on purpose would fail here, and there is no marker to exempt one.
    [Theory]
    [MemberData(nameof(ValidSamplesWithPathsThatStartAtThePage))]
    public async Task WhenSampleHasAPathThatStartsAtThePage_ThenThePathLeadsToSomething(string relativePath, int line)
    {
        // Arrange
        var sample = FindSample(relativePath, line);
        var fixture = await CreateFixtureAsync();
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var page = await fixture.AddPageAsync(
            "Sample.md", sample.IsLive ? RepositoryPagesTests.ReadPage(sample.RelativePath) : sample.Source);

        // Assert
        var pathsToNothing = FindPathsThatStartAtThePage(MarkdownPageParser.Parse(sample.Source))
            .Where(path => !LeadsToSomething(path, page, resolver))
            .Select(path => path.Text);

        Assert.Empty(pathsToNothing);
    }

    private static TheoryData<string, int> ToTheoryData(Func<Sample, bool> filter)
    {
        var data = new TheoryData<string, int>();
        foreach (var sample in AllSamples.Value.Where(filter))
        {
            data.Add(sample.RelativePath, sample.Line);
        }

        return data;
    }

    private static Sample FindSample(string relativePath, int line) =>
        AllSamples.Value.Single(sample => sample.RelativePath == relativePath && sample.Line == line);

    private static IReadOnlyList<Sample> ReadSamples()
    {
        var root = RepositoryPagesTests.FindRepositoryRoot();
        var samples = new List<Sample>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, DocumentationFolder), "*.md", SearchOption.AllDirectories).Order())
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');

            // The documentation pages are pages themselves, so their code blocks are found the way a page finds them.
            var document = MarkdownPageParser.Parse(RepositoryPagesTests.ReadPage(relativePath)).Document;
            foreach (var code in document.Descendants<FencedCodeBlock>().Where(code => code.Info == SampleLanguage))
            {
                var siblings = code.Parent!;
                var index = siblings.IndexOf(code);
                var marker = index > 0 && siblings[index - 1] is HtmlBlock comment ? comment.Lines.ToString().Trim() : null;
                var isError = marker == ErrorMarker;

                IReadOnlyList<string>? expectedErrors = null;
                if (isError)
                {
                    expectedErrors = index + 1 < siblings.Count && siblings[index + 1] is FencedCodeBlock { Info: ExpectedErrorsLanguage } boxes
                        ? SortByLine(boxes.Lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        : [];
                }

                samples.Add(new Sample(relativePath, code.Line + 1, code.Lines.ToString(), expectedErrors, marker == LiveMarker));
            }
        }

        return samples;
    }

    /// <summary>
    /// Gets what differs between a live sample and the lines after its closing fence, where one empty line
    /// may stand between them, or null when those lines repeat the sample exactly.
    /// </summary>
    /// <param name="relativePath">The page, for the message.</param>
    /// <param name="pageLines">The lines of the page.</param>
    /// <param name="sampleLine">The 1-based line of the opening fence of the sample.</param>
    private static string? FindLiveBlockDifference(string relativePath, string[] pageLines, int sampleLine)
    {
        var closingFence = pageLines[sampleLine - 1].TrimStart()[..^SampleLanguage.Length];
        var closingIndex = Array.IndexOf(pageLines, closingFence, sampleLine);
        if (closingIndex < 0)
        {
            return $"{relativePath} line {sampleLine}: the sample has no closing fence '{closingFence}'.";
        }

        var liveIndex = closingIndex + 1;
        if (liveIndex < pageLines.Length && pageLines[liveIndex].Length == 0)
        {
            liveIndex++;
        }

        for (var offset = 0; sampleLine + offset < closingIndex; offset++)
        {
            var expected = $"expected '{pageLines[sampleLine + offset]}', which is line {sampleLine + offset + 1} of the page";
            if (liveIndex + offset >= pageLines.Length)
            {
                return $"{relativePath} line {liveIndex + offset + 1}: {expected}, but the page ends there.";
            }

            if (pageLines[liveIndex + offset] != pageLines[sampleLine + offset])
            {
                return $"{relativePath} line {liveIndex + offset + 1}: {expected}, but found '{pageLines[liveIndex + offset]}'.";
            }
        }

        // A line that follows the repeated lines directly would be a line of the live block that the sample does not show.
        var endIndex = liveIndex + closingIndex - sampleLine;
        return endIndex < pageLines.Length && pageLines[endIndex].Length > 0
            ? $"{relativePath} line {endIndex + 1}: expected an empty line after the live block, but found '{pageLines[endIndex]}'."
            : null;
    }

    /// <summary>Creates a fixture that knows the widget types that ship and the sample subjects.</summary>
    internal static Task<MarkdownPageFixture> CreateFixtureAsync() =>
        MarkdownPageFixture.CreateAsync(services =>
        {
            // The type registry reads the provider on first use, so the assemblies are added before anything is created.
            var typeProvider = (TypeProvider)services
                .Single(service => service.ServiceType == typeof(TypeProvider))
                .ImplementationInstance!;

            typeProvider
                .AddAssembly(typeof(Card).Assembly)
                .AddAssembly(typeof(Motor).Assembly);
        });

    // The page is asked for its boxes by line, so the order in which the documentation lists them does not matter.
    private static List<string> SortByLine(IEnumerable<string> boxes) =>
        boxes
            .OrderBy(box => BoxLineRegex().Match(box) is { Success: true } match ? int.Parse(match.Groups[1].Value) : 0)
            .ToList();

    /// <summary>
    /// Gets the paths of the sample that have no prefix: those of its expressions, whose last segment is a
    /// property, and those of its <see cref="Widget"/> blocks and of the widgets in its json blocks, which
    /// lead to a subject.
    /// </summary>
    private static IEnumerable<SamplePath> FindPathsThatStartAtThePage(MarkdownPage page)
    {
        var expressions = MarkdownSegmentBuilder.FindExpressions(page)
            .Select(expression => new SamplePath(expression.Path, IsExpression: true));

        var widgetBlocks = page.AllBlocks
            .Where(block => block.Head?.Name == typeof(Widget).FullName)
            .SelectMany(block => block.Head!.Arguments)
            .Where(argument => argument.Key == nameof(Widget.Path))
            .Select(argument => argument.Value);

        var nestedWidgets = page.AllBlocks
            .Where(block => block.ConfigurationJson is not null)
            .SelectMany(block => FindNestedWidgetPaths(JsonNode.Parse(block.ConfigurationJson!)));

        return expressions
            .Concat(widgetBlocks.Concat(nestedWidgets).Select(path => new SamplePath(path, IsExpression: false)))
            .Where(path => !path.Text.StartsWith('/') && path.Text.Split('/')[0] is not ("." or ".."));
    }

    /// <summary>Gets the path of every <see cref="Widget"/> that the JSON has at any depth.</summary>
    private static IEnumerable<string> FindNestedWidgetPaths(JsonNode? node)
    {
        if (node is JsonObject json &&
            json[ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName]?.GetValueKind() == JsonValueKind.String &&
            json[ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName]!.GetValue<string>() == typeof(Widget).FullName &&
            json[nameof(Widget.Path)]?.GetValueKind() == JsonValueKind.String)
        {
            yield return json[nameof(Widget.Path)]!.GetValue<string>();
        }

        var children = node switch
        {
            JsonObject parent => parent.Select(property => property.Value),
            JsonArray items => items,
            _ => []
        };

        foreach (var path in children.SelectMany(FindNestedWidgetPaths))
        {
            yield return path;
        }
    }

    // The same lookup as the page does for an expression and a Widget for its path. A property counts as
    // found when it exists, whatever its value is.
    private static bool LeadsToSomething(SamplePath path, MarkdownFile page, SubjectPathResolver resolver)
    {
        if (!path.IsExpression)
        {
            return resolver.ResolveScopedSubject(path.Text, PathStyle.Canonical, page) is not null;
        }

        var lastSlash = path.Text.LastIndexOf('/');
        IInterceptorSubject? owner = lastSlash < 0 ? page : resolver.ResolveScopedSubject(path.Text[..lastSlash], PathStyle.Canonical, page);
        return owner?.TryGetRegisteredProperty(path.Text[(lastSlash + 1)..]) is not null;
    }

    /// <summary>Gets the names in <paramref name="written"/>, at any depth, that <paramref name="read"/> does not have at the same place.</summary>
    private static IEnumerable<string> FindNamesNotRead(JsonNode? written, JsonNode? read, string path)
    {
        if (written is JsonObject writtenObject)
        {
            foreach (var (name, value) in writtenObject)
            {
                if (name == ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName || value is null)
                {
                    continue;
                }

                var namePath = $"{path}/{name}";
                if (read is JsonObject readObject && readObject.TryGetPropertyValue(name, out var readValue))
                {
                    foreach (var nameNotRead in FindNamesNotRead(value, readValue, namePath))
                    {
                        yield return nameNotRead;
                    }
                }
                else
                {
                    yield return namePath;
                }
            }
        }
        else if (written is JsonArray writtenArray)
        {
            var readArray = read as JsonArray;
            for (var index = 0; index < writtenArray.Count; index++)
            {
                var readItem = readArray is not null && index < readArray.Count ? readArray[index] : null;
                foreach (var nameNotRead in FindNamesNotRead(writtenArray[index], readItem, $"{path}[{index}]"))
                {
                    yield return nameNotRead;
                }
            }
        }
    }

    /// <summary>One sample of page source. <see cref="Line"/> is the 1-based line of its opening fence.</summary>
    private sealed record Sample(string RelativePath, int Line, string Source, IReadOnlyList<string>? ExpectedErrors, bool IsLive);

    /// <summary>One path in a sample. The last segment of the path of an expression is the name of a property.</summary>
    private readonly record struct SamplePath(string Text, bool IsExpression);
}
