using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Markdig.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

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
/// per line. A line that ends with <see cref="AnyEnding"/> gives the start of a box only.
/// </remarks>
[Collection(MarkdownPageFixture.StorageCollection)]
public partial class DocumentationSamplesTests
{
    private const string DocumentationFolder = "src/HomeBlaze/HomeBlaze/Data/Files/Docs";
    private const string SampleLanguage = "markdown";
    private const string ExpectedErrorsLanguage = "text";
    private const string ErrorMarker = "<!-- sample: error -->";

    // The types of this file are in the test assembly, which every fixture knows.
    private const string SampleWidgetSource = "src/HomeBlaze/HomeBlaze.Storage.Tests/Pages/DocumentationSampleWidget.cs";
    private const string SampleWidgetDocumentation = DocumentationFolder + "/development/building-subjects.md";

    // For the part of a message that the reader of the JSON writes, which is not ours to pin.
    private const string AnyEnding = "...";

    private static readonly Lazy<IReadOnlyList<Sample>> AllSamples = new(ReadSamples);

    [GeneratedRegex("""<div class="markdown-block-error" role="alert">(Line \d+: .*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex ErrorBoxRegex();

    [GeneratedRegex(@"^Line (\d+): ")]
    private static partial Regex BoxLineRegex();

    public static TheoryData<string, int> ValidSamples() => ToTheoryData(sample => sample.ExpectedErrors is null);

    public static TheoryData<string, int> ErrorSamples() => ToTheoryData(sample => sample.ExpectedErrors is not null);

    public static TheoryData<string, int> ValidSamplesWithJsonBlock() => ToTheoryData(sample =>
        sample.ExpectedErrors is null &&
        MarkdownPageParser.Parse(sample.Source).AllBlocks.Any(block => block.ConfigurationJson is not null));

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
        Assert.Empty(FindErrorBoxes(page));

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
        var expectedErrors = sample.ExpectedErrors!;
        var errors = FindErrorBoxes(page);
        Assert.NotEmpty(expectedErrors);
        Assert.Equal(expectedErrors.Count, errors.Count);
        Assert.All(expectedErrors.Zip(errors), pair =>
        {
            if (pair.First.EndsWith(AnyEnding, StringComparison.Ordinal))
            {
                Assert.StartsWith(pair.First[..^AnyEnding.Length], pair.Second, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(pair.First, pair.Second);
            }
        });
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
                var isError = index > 0 &&
                    siblings[index - 1] is HtmlBlock comment &&
                    comment.Lines.ToString().Trim() == ErrorMarker;

                IReadOnlyList<string>? expectedErrors = null;
                if (isError)
                {
                    expectedErrors = index + 1 < siblings.Count && siblings[index + 1] is FencedCodeBlock { Info: ExpectedErrorsLanguage } boxes
                        ? SortByLine(boxes.Lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        : [];
                }

                samples.Add(new Sample(relativePath, code.Line + 1, code.Lines.ToString(), expectedErrors));
            }
        }

        return samples;
    }

    private static Task<MarkdownPageFixture> CreateFixtureAsync() =>
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

    /// <summary>Gets the texts of all error boxes of the page as the page shows them, ordered by their line.</summary>
    private static List<string> FindErrorBoxes(MarkdownFile page) =>
        SortByLine(
            Flatten(page.Children.Values)
                .OfType<HtmlSegment>()
                .SelectMany(segment => ErrorBoxRegex().Matches(segment.Html))
                .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)));

    // The boxes inside a widget are found slot by slot, which is not the order of the page.
    private static List<string> SortByLine(IEnumerable<string> boxes) =>
        boxes
            .OrderBy(box => BoxLineRegex().Match(box) is { Success: true } match ? int.Parse(match.Groups[1].Value) : 0)
            .ToList();

    private static IEnumerable<IInterceptorSubject> Flatten(IEnumerable<IInterceptorSubject?> subjects)
    {
        foreach (var subject in subjects.OfType<IInterceptorSubject>())
        {
            yield return subject;

            var nested = subject is MarkdownFragment fragment ? fragment.Children.Values : GetSlotValues(subject);
            foreach (var child in Flatten(nested))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<IInterceptorSubject?> GetSlotValues(IInterceptorSubject subject) =>
        subject.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<SlotAttribute>(true) is not null)
            .SelectMany(property => property.GetValue(subject) switch
            {
                IInterceptorSubject value => [value],
                IEnumerable<IInterceptorSubject?> values => values,
                _ => []
            });

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
    private sealed record Sample(string RelativePath, int Line, string Source, IReadOnlyList<string>? ExpectedErrors);
}
