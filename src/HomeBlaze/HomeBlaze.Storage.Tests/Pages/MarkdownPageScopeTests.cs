using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownPageScopeTests
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";

    private static RenderExpression FindExpression(IEnumerable<IInterceptorSubject> subjects, string path) =>
        Flatten(subjects).OfType<RenderExpression>().Single(expression => expression.Path == path);

    private static IEnumerable<IInterceptorSubject> Flatten(IEnumerable<IInterceptorSubject> subjects)
    {
        foreach (var subject in subjects)
        {
            yield return subject;

            IInterceptorSubject?[] nested = subject switch
            {
                MarkdownFragment fragment => [.. fragment.Children.Values],
                TestPanel panel => [panel.Content, panel.Footer, .. panel.Rows, .. panel.Items],
                TestRow row => [row.Content],
                _ => []
            };

            foreach (var child in Flatten(nested.OfType<IInterceptorSubject>()))
            {
                yield return child;
            }
        }
    }

    private static TestReference FindReference(IPathScope page, string name) =>
        Assert.IsType<TestReference>(page.TryGetNamedSubject(name));

    private static List<string> RecordChangedProperties(IInterceptorSubject subject)
    {
        var propertyNames = new List<string>();
        ((INotifyPropertyChanged)subject).PropertyChanged += (_, arguments) => propertyNames.Add(arguments.PropertyName!);
        return propertyNames;
    }

    private static Task<MarkdownFile> AddPageAsync(MarkdownPageFixture fixture, string content, bool parseBeforeAttach) =>
        parseBeforeAttach
            ? fixture.AddPageParsedBeforeAttachAsync("page.md", content)
            : fixture.AddPageAsync("page.md", content);

    // One reference directly on the page, one in an open slot, and one in the content of a typed value of a list slot.
    private static string ReferencesAtEveryDepth(string path) => $$"""
        :::{{Namespace}}.TestReference#direct path={{path}}
        :::

        :::::{{Namespace}}.TestPanel#panel
        :::{{Namespace}}.TestReference#inFragment path={{path}}
        :::

        ::::Row label=a
        :::{{Namespace}}.TestReference#inRow path={{path}}
        :::
        ::::

        ::::Footer
        :::{{Namespace}}.TestLeaf#leaf count=1
        :::
        ::::
        :::::
        """;

    [Fact]
    public async Task WhenNamedSubjectIsNestedInASlot_ThenExpressionOnThePageResolvesItByName()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$$"""
            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestLeaf#leaf count=42
            :::
            ::::

            Count: {{ leaf/Count }}
            """);

        // Assert
        Assert.Equal(42, FindExpression(page.Children.Values, "leaf/Count").Value);
    }

    [Fact]
    public async Task WhenExpressionIsNestedInASlot_ThenItResolvesNamesAndPagePropertiesFromThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :::{{{Namespace}}}.TestLeaf#leaf name=Top
            :::

            :::{{{Namespace}}}.TestPanel#panel
            Nested {{ leaf/Name }} and {{ Title }}
            :::
            """);

        // Assert
        Assert.Equal("Top", FindExpression(page.Children.Values, "leaf/Name").Value);
        Assert.Equal("Page", FindExpression(page.Children.Values, "Title").Value);
    }

    [Fact]
    public async Task WhenSubjectIsNestedInASlot_ThenRelativePathResolvesFromThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var other = await fixture.AddPageAsync("other.md", "other");
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::{{Namespace}}.TestLeaf#leaf
            :::
            ::::
            """);
        var leaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var sibling = resolver.ResolveScopedSubject("../other.md", PathStyle.Canonical, leaf);
        var named = resolver.ResolveScopedSubject("panel", PathStyle.Canonical, leaf);
        var absolute = resolver.ResolveScopedSubject("/other.md", PathStyle.Canonical, leaf);

        // Assert
        Assert.Same(other, sibling);
        Assert.Same(page.Children["panel"], named);
        Assert.Same(other, absolute);
    }

    [Fact]
    public async Task WhenSubjectHasNoScope_ThenScopedResolutionBehavesLikeTheUnscopedOne()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "text");
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var scoped = resolver.ResolveScopedSubject("page.md", PathStyle.Canonical, fixture.Storage);
        var unscoped = resolver.ResolveSubject("page.md", PathStyle.Canonical, fixture.Storage);

        // Assert
        Assert.Same(page, unscoped);
        Assert.Same(unscoped, scoped);
    }

    [Fact]
    public async Task WhenEvaluatedContentIsRead_ThenNamesInsideBlocksAreResolved()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestLeaf#leaf count=7
            :::
            ::::

            Count: {{ leaf/Count }}
            """);

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.Contains("Count: 7", evaluated);
    }

    [Fact]
    public async Task WhenEvaluatedContentIsRead_ThenExpressionInsideCodeStaysAsWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :::{{{Namespace}}}.TestLeaf#leaf count=7
            :::

            Count: {{ leaf/Count }}

            ~~~~markdown
            Example: {{ leaf/Count }}
            ~~~~
            """);

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.Contains("Count: 7\n", evaluated);
        Assert.Contains("~~~~markdown\nExample: {{ leaf/Count }}\n~~~~", evaluated);
    }

    [Fact]
    public async Task WhenEvaluatedContentIsRead_ThenBracesInInlineCodeDoNotHideTheExpressionAfterThem()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :::{{{Namespace}}}.TestLeaf#leaf count=7
            :::

            Write `{{ leaf/Count }}` or only `{{` to show {{ leaf/Count }} here.
            """);

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.Contains("Write `{{ leaf/Count }}` or only `{{` to show 7 here.", evaluated);
    }

    [Theory]
    [InlineData("../other.md", false)]
    [InlineData("../other.md", true)]
    [InlineData("/other.md", false)]
    [InlineData("/other.md", true)]
    public async Task WhenPathIsUsedAtAnyDepth_ThenItResolvesFromThePage(string path, bool parseBeforeAttach)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var other = await fixture.AddPageAsync("other.md", "other");

        // Act
        var page = await AddPageAsync(fixture, ReferencesAtEveryDepth(path), parseBeforeAttach);

        // Assert
        Assert.Same(other, FindReference(page, "direct").Resolved);
        Assert.Same(other, FindReference(page, "inFragment").Resolved);
        Assert.Same(other, FindReference(page, "inRow").Resolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenNameIsUsedAtAnyDepth_ThenItResolvesTheNamedSubject(bool parseBeforeAttach)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await AddPageAsync(fixture, ReferencesAtEveryDepth("leaf"), parseBeforeAttach);

        // Assert
        var leaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();
        Assert.Same(leaf, FindReference(page, "direct").Resolved);
        Assert.Same(leaf, FindReference(page, "inFragment").Resolved);
        Assert.Same(leaf, FindReference(page, "inRow").Resolved);
    }

    [Theory]
    [InlineData("leaf/Count", 1)]
    [InlineData("Title", "Page")]
    [InlineData("../other.md/Title", "Other")]
    [InlineData("/other.md/Title", "Other")]
    public async Task WhenValueIsResolvedFromANestedSubject_ThenItsPathIsInPageScope(string path, object expected)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        await fixture.AddPageAsync("other.md", "other");
        var page = await fixture.AddPageAsync("page.md", ReferencesAtEveryDepth("leaf"));
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var value = resolver.ResolveScopedValue(path, PathStyle.Canonical, FindReference(page, "inRow"));

        // Assert
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("leaf/")]
    [InlineData("")]
    public async Task WhenValuePathNamesNoProperty_ThenTheValueIsNull(string path)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", ReferencesAtEveryDepth("leaf"));
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var value = resolver.ResolveScopedValue(path, PathStyle.Canonical, FindReference(page, "inRow"));

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public async Task WhenSubjectIsNotOnAPage_ThenItsScopedPathResolvesFromTheSubjectItself()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "text");
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();
        var reference = new TestReference(resolver) { Path = "../page.md" };

        // Act
        fixture.Storage.Children = new Dictionary<string, IInterceptorSubject>(fixture.Storage.Children) { ["reference"] = reference };

        // Assert
        Assert.Same(page, reference.Resolved);
        Assert.Equal("Page", resolver.ResolveScopedValue("../page.md/Title", PathStyle.Canonical, reference));
        Assert.Equal("Page", resolver.ResolveValue("../page.md/Title", PathStyle.Canonical, reference));
    }

    [Fact]
    public async Task WhenSubjectIsNotAttached_ThenItsScopedPathResolvesWithoutAScope()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var other = await fixture.AddPageAsync("other.md", "other");
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var relative = new TestReference(resolver) { Path = "../other.md" };
        var absolute = new TestReference(resolver) { Path = "/other.md" };

        // Assert
        Assert.Null(relative.Resolved);
        Assert.Same(other, absolute.Resolved);
    }

    [Fact]
    public async Task WhenSubjectHasSeveralParents_ThenItsScopeIsStillFound()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var other = await fixture.AddPageAsync("other.md", "other");
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{Namespace}}.TestPanel#panel
            :::
            """);
        var panel = (TestPanel)page.Children["panel"];
        var leaf = new TestLeaf();
        panel.Content = leaf;
        panel.Footer = leaf;
        var resolver = fixture.Services.GetRequiredService<SubjectPathResolver>();

        // Act
        var sibling = resolver.ResolveScopedSubject("../other.md", PathStyle.Canonical, leaf);
        var named = resolver.ResolveScopedSubject("panel", PathStyle.Canonical, leaf);

        // Assert
        Assert.Same(other, sibling);
        Assert.Same(panel, named);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenNamedSubjectIsReplacedOnReload_ThenValuesOnThePageThatResolvedItsNameAreRecalculated(bool parseBeforeAttach)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $$$"""
            :::{{{Namespace}}}.TestReference#reference path=leaf
            :::

            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestLeaf#leaf count=1
            :::
            ::::

            Top {{ leaf/Count }}
            """;
        var page = await AddPageAsync(fixture, content, parseBeforeAttach);
        var reference = FindReference(page, "reference");
        var expression = FindExpression(page.Children.Values, "leaf/Count");
        var firstLeaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();
        var referenceChanges = RecordChangedProperties(reference);
        var expressionChanges = RecordChangedProperties(expression);

        // Act
        // Removing an argument makes the reload create the subject again.
        await fixture.UpdatePageAsync(page, content.Replace("count=1", "name=New"));
        var secondLeaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();
        var expressionChangeCountAfterReload = expressionChanges.Count;
        secondLeaf.Count = 99;

        // Assert
        Assert.NotSame(firstLeaf, secondLeaf);
        Assert.Same(expression, FindExpression(page.Children.Values, "leaf/Count"));
        Assert.Same(secondLeaf, reference.Resolved);
        Assert.Contains(nameof(TestReference.Resolved), referenceChanges);
        Assert.Equal(99, expression.Value);
        Assert.True(expressionChangeCountAfterReload > 0, "The reload did not compute the value again.");
        Assert.True(expressionChanges.Count > expressionChangeCountAfterReload, "The value does not follow the new subject.");
    }

    [Fact]
    public async Task WhenNamedSubjectIsReplacedOnReload_ThenExpressionInASlotThatResolvedItsNameIsRecalculated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $$$"""
            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestLeaf#leaf count=1
            :::

            Nested {{ leaf/Count }}
            ::::
            """;

        // The page is in the graph while it is parsed, so the expression is evaluated while the page has a context.
        var page = await fixture.AddPageAsync("page.md", content);
        var expression = FindExpression(page.Children.Values, "leaf/Count");
        var expressionChanges = RecordChangedProperties(expression);

        // Act
        // Removing an argument makes the reload create the subject again.
        await fixture.UpdatePageAsync(page, content.Replace("count=1", "name=New"));
        var secondLeaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();
        var expressionChangeCountAfterReload = expressionChanges.Count;
        secondLeaf.Count = 99;

        // Assert
        Assert.Same(expression, FindExpression(page.Children.Values, "leaf/Count"));
        Assert.Equal(99, expression.Value);
        Assert.True(expressionChangeCountAfterReload > 0, "The reload did not compute the value again.");
        Assert.True(expressionChanges.Count > expressionChangeCountAfterReload, "The value does not follow the new subject.");
    }

    [Fact]
    public async Task WhenFileNameContainsBraces_ThenAnExpressionReachesItWithPercentEncodedSegments()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        await fixture.AddPageAsync("a{b}.md", "---\ntitle: Braces\n---\n\ntext");

        // Act
        var page = await fixture.AddPageAsync("page.md", "Title: {{ ../a%7Bb%7D.md/Title }}");

        // Assert
        Assert.Equal("Braces", FindExpression(page.Children.Values, "../a%7Bb%7D.md/Title").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenSlotContentWasEvaluatedBeforeItsPageWasReachable_ThenAReadStillReturnsTheCurrentValue(bool parseBeforeAttach)
    {
        // Arrange
        // The serializer creates the panel with the context, so what is put into its slots is attached and
        // evaluated at once, before the panel is on the page. With parseBeforeAttach the page itself has no
        // context yet at that moment. The dependencies recorded then are not those of the page scope, so a
        // change is not notified for these values. A read runs the getter and is what this test pins.
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await AddPageAsync(fixture, $$$"""
            ::::{{{Namespace}}}.TestPanel#panel
            :::{{{Namespace}}}.TestReference#byPath path=../later.md
            :::

            :::{{{Namespace}}}.TestReference#byName path=leaf
            :::

            :::{{{Namespace}}}.TestLeaf#leaf count=1
            :::

            Nested {{ leaf/Count }}
            ::::
            """, parseBeforeAttach);
        var leaf = Flatten(page.Children.Values).OfType<TestLeaf>().Single();

        // Act
        var later = await fixture.AddPageAsync("later.md", "later");
        leaf.Count = 5;

        // Assert
        Assert.Same(later, FindReference(page, "byPath").Resolved);
        Assert.Same(leaf, FindReference(page, "byName").Resolved);
        Assert.Equal(5, FindExpression(page.Children.Values, "leaf/Count").Value);
    }
}
