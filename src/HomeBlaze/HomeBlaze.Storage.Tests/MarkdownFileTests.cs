using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using HomeBlaze.Storage.Tests.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

// Writes to an in-memory storage, see the remarks of StorageTestBase.
[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownFileTests
{
    [Fact]
    public void FrontmatterParser_WithNavigationTitle_ReturnsNavigationTitle()
    {
        var content = """
            ---
            navTitle: My Nav Title
            title: Regular Title
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("My Nav Title", result.NavTitle);
    }

    [Fact]
    public void FrontmatterParser_WithNavTitle_ReturnsNavTitle()
    {
        var content = """
            ---
            nav_title: My Nav Title
            title: Regular Title
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("My Nav Title", result.NavTitle);
    }

    [Fact]
    public void FrontmatterParser_WithOnlyTitle_ReturnsTitle()
    {
        var content = """
            ---
            title: Regular Title
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("Regular Title", result.Title);
    }

    [Fact]
    public void FrontmatterParser_WithQuotedTitle_RemovesQuotes()
    {
        var content = """
            ---
            title: "Quoted Title"
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("Quoted Title", result.Title);
    }

    [Fact]
    public void FrontmatterParser_WithPosition_ReturnsPosition()
    {
        var content = """
            ---
            title: Test
            position: 5
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal(5, result.Position);
    }

    [Fact]
    public void FrontmatterParser_WithIcon_ReturnsIcon()
    {
        var content = """
            ---
            title: Test
            icon: mdi-home
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("mdi-home", result.Icon);
    }

    [Fact]
    public void FrontmatterParser_WithoutFrontMatter_ReturnsNull()
    {
        var content = "# Just Content\nNo front matter here";

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.Null(result);
    }

    [Fact]
    public void FrontmatterParser_WithEmptyFrontMatter_ReturnsNull()
    {
        var content = """
            ---
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.Null(result);
    }

    [Fact]
    public void FrontmatterParser_WithEmptyContent_ReturnsNull()
    {
        var result = FrontmatterParser.Parse<MarkdownFrontmatter>("");

        Assert.Null(result);
    }

    [Fact]
    public async Task MarkdownFile_Title_WithFrontmatter_ReturnsFrontmatterTitle()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Frontmatter Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal("Frontmatter Title", markdown.Title);
    }

    [Fact]
    public async Task MarkdownFile_Title_WithoutFrontmatter_ReturnsFormattedFilename()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "my-test-document.md", "# Content");

        var markdown = CreateMarkdownFile(serviceProvider, storage, "my-test-document.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal("My Test Document", markdown.Title);
    }

    [Fact]
    public async Task MarkdownFile_NavigationTitle_WithFrontmatter_ReturnsNavTitle()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Regular Title
            navTitle: Nav Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal("Nav Title", markdown.NavigationTitle);
    }

    [Fact]
    public async Task MarkdownFile_PagePosition_WithFrontmatter_ReturnsPosition()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Test
            position: 3
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal(3, markdown.PagePosition);
    }

    [Fact]
    public async Task MarkdownFile_Icon_WithFrontmatter_ReturnsCustomIcon()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Test
            icon: mdi-custom
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal("mdi-custom", markdown.NavigationIconName);
    }

    [Fact]
    public async Task MarkdownFile_Icon_WithoutFrontmatter_ReturnsDefaultIcon()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "test.md", "# Content");

        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal("Article", markdown.IconName);
    }

    [Fact]
    public async Task MarkdownFile_Refresh_UpdatesFrontmatter()
    {
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var content1 = """
            ---
            title: First Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content1);

        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);
        Assert.Equal("First Title", markdown.Title);

        var content2 = """
            ---
            title: Second Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content2);
        await markdown.OnFileChangedAsync(CancellationToken.None);

        Assert.Equal("Second Title", markdown.Title);
    }

    [Fact]
    public async Task MarkdownFile_ReadEvaluatedContent_WithUnresolvableExpression_ReturnsEmptyForExpression()
    {
        // Arrange
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "test.md", "Missing: {{ does/not/exist }}");
        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        // Act
        var result = markdown.ReadEvaluatedContent();

        // Assert
        Assert.Equal("Missing: ", result);
    }

    [Fact]
    public async Task MarkdownFile_ReadEvaluatedContent_WithoutExpressions_ReturnsContentUnchanged()
    {
        // Arrange
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "test.md", "# Heading\n\nPlain body, no expressions.");
        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        // Act
        var result = markdown.ReadEvaluatedContent();

        // Assert
        Assert.Equal("# Heading\n\nPlain body, no expressions.", result);
    }

    [Fact]
    public async Task MarkdownFile_ReadEvaluatedContent_WithEmptyContent_ReturnsEmpty()
    {
        // Arrange
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "test.md", string.Empty);
        var markdown = CreateMarkdownFile(serviceProvider, storage, "test.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        // Act
        var result = markdown.ReadEvaluatedContent();

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(true, "Relative: 750 (/Devices/motor/CurrentSpeed), absolute: 750 (/Devices/motor/CurrentSpeed), missing: ")]
    [InlineData(false, "Relative: 750, absolute: 750, missing: ")]
    [InlineData(null, "Relative: 750, absolute: 750, missing: ")]
    public async Task WhenReadingEvaluatedContent_ThenSourcePathsFollowTheResolvedValuesOnlyWhenRequested(bool? includeSourcePaths, string expected)
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(
            "Relative: {{ ../../Devices/motor/CurrentSpeed }}, absolute: {{ /Devices/motor/CurrentSpeed }}, missing: {{ does/not/exist }}");

        // Act
        var result = includeSourcePaths is { } include ? markdown.ReadEvaluatedContent(include) : markdown.ReadEvaluatedContent();

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task WhenAPageIsInTheGraph_ThenReadEvaluatedContentTakesAnOptionalSourcePathsParameter()
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync("Text");

        // Act
        var method = (MethodMetadata)markdown.TryGetRegisteredSubject()!.TryGetProperty("ReadEvaluatedContent")!.GetValue()!;

        // Assert
        Assert.Equal(MethodKind.Query, method.Kind);
        var parameter = Assert.Single(method.Parameters, parameter => parameter.RequiresInput);
        Assert.Equal("includeSourcePaths", parameter.Name);
        Assert.True(parameter.IsNullable);
        Assert.Equal("Text", await method.InvokeAsync([null], serviceProvider: null, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, "Secret: , public: visible")]
    [InlineData(true, "Secret: , public: visible (/Devices/motor/Host)")]
    public async Task WhenAPageReadsASecretConfigurationProperty_ThenItIsNotRendered(bool includeSourcePaths, string expected)
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(
            "Secret: {{ /Devices/motor/Password }}, public: {{ ../../Devices/motor/Host }}",
            new EmbeddedSecretDevice { Password = "s3cr3t", Host = "visible" });

        // Act
        var result = markdown.ReadEvaluatedContent(includeSourcePaths);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task WhenThePageViewShowsASecretConfigurationProperty_ThenItsExpressionHasNoValue()
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(
            "Secret: {{ /Devices/motor/Password }}, public: {{ /Devices/motor/Host }}",
            new EmbeddedSecretDevice { Password = "s3cr3t", Host = "visible" });

        // Act
        var expressions = markdown.Children.Values.OfType<RenderExpression>().ToDictionary(expression => expression.Path);

        // Assert
        Assert.Null(expressions["/Devices/motor/Password"].Value);
        Assert.Equal("visible", expressions["/Devices/motor/Host"].Value);
    }

    [Fact]
    public async Task WhenReadingTheRawContent_ThenSecretValuesInTheBlocksAreHidden()
    {
        // Arrange: secrets as arguments and in json blocks, of a widget block, of a widget inside a card and of a
        // typed slot block, next to blocks without secrets and blocks whose type is unknown.
        var device = typeof(EmbeddedSecretDevice).FullName;
        var content = $$$"""
            # Gateway

            :: {{{device}}}#gateway Host=192.168.1.10 Password=s3cr3t
            ::

            :: {{{device}}}#jsongateway
            ```json
            {
              "Host": "10.0.0.2",
              "Password": "json-secret"
            }
            ```
            ::

            :: {{{typeof(Motor).FullName}}}#plain Name=Plain TargetSpeed=1000
            ::

            :: HomeBlaze.Components.Card#card Title="Card Title"
            ::: {{{device}}}#cardgateway Host=card-host Password="card secret"
            :::
            ::

            :: {{{typeof(SecretSlotHost).FullName}}}#host
            ::: Gateway Host=slot-host Password=slot-secret
            :::
            ::

            :: Unknown.Device#unknown Token="also secret"
            ```json
            { "Key": "json also secret" }
            ```
            ::

            Gateway at {{ gateway/Host }}, password {{ gateway/Password }}.
            """;
        var markdown = await CreatePageInGraphAsync(content);

        // Act
        var raw = markdown.ReadRawContent();
        var evaluated = markdown.ReadEvaluatedContent();

        // Assert
        Assert.IsType<EmbeddedSecretDevice>(markdown.Children["gateway"]);
        Assert.IsType<EmbeddedSecretDevice>(markdown.Children["jsongateway"]);
        var cardContent = Assert.IsType<MarkdownFragment>(Assert.IsType<HomeBlaze.Components.Card>(markdown.Children["card"]).Content);
        Assert.Single(cardContent.Children.Values.OfType<EmbeddedSecretDevice>());
        Assert.Single(Assert.IsType<SecretSlotHost>(markdown.Children["host"]).Gateways);
        foreach (var result in new[] { raw, evaluated })
        {
            Assert.NotNull(result);
            Assert.DoesNotContain("s3cr3t", result);
            Assert.DoesNotContain("json-secret", result);
            Assert.DoesNotContain("card secret", result);
            Assert.DoesNotContain("slot-secret", result);
            Assert.DoesNotContain("also secret", result);
            Assert.Contains($":: {device}#gateway Host=192.168.1.10 Password=***\n", result);
            Assert.Contains("\"Host\": \"10.0.0.2\",\n  \"Password\": \"***\"", result);
            Assert.Contains($"::: {device}#cardgateway Host=card-host Password=***\n", result);
            Assert.Contains("::: Gateway Host=slot-host Password=***\n", result);
            Assert.Contains(":: Unknown.Device#unknown Token=***\n```json\n***\n```\n", result);
            Assert.Contains($":: {typeof(Motor).FullName}#plain Name=Plain TargetSpeed=1000\n", result);
            Assert.Contains(":: HomeBlaze.Components.Card#card Title=\"Card Title\"\n", result);
        }

        Assert.EndsWith("Gateway at {{ gateway/Host }}, password {{ gateway/Password }}.", raw);
        Assert.EndsWith("Gateway at 192.168.1.10, password .", evaluated);
        Assert.Equal(content, markdown.Content);
    }

    private static readonly string PageWithSecretBlock =
        $":: {typeof(EmbeddedSecretDevice).FullName}#gateway Host=gateway-host Password=s3cr3t\n::\n\nThe gateway is at {{{{ gateway/Host }}}}.";

    [Theory]
    [InlineData("Password=s")]
    [InlineData("Password=s3cr3t")]
    [InlineData("s3cr3t")]
    [InlineData("Password=***")]
    public async Task WhenAPassageProbesASecretValue_ThenItIsNotFoundLikeAWrongGuess(string probe)
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(PageWithSecretBlock);
        var wrongGuess = probe.Replace("s3cr3t", "x9y8z7").Replace("Password=s", "Password=x").Replace("***", "xyz");

        // Act
        var probeException = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync(probe, probe, CancellationToken.None));
        var wrongGuessException = await Assert.ThrowsAsync<ArgumentException>(
            () => markdown.ReplaceTextAsync(wrongGuess, wrongGuess, CancellationToken.None));

        // Assert
        Assert.Equal(wrongGuessException.Message, probeException.Message);
        Assert.StartsWith("The text to replace was not found in page.md.", probeException.Message);
        Assert.Equal(PageWithSecretBlock, await ReadStoredPageAsync(markdown));
    }

    [Theory]
    [InlineData("s3cr3t")]
    [InlineData("wrong-guess")]
    public async Task WhenTheContentRepeatsABlockWithARealOrAGuessedSecretValue_ThenBothAreRejectedAlike(string password)
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(PageWithSecretBlock);
        var content = PageWithSecretBlock.Replace("Password=s3cr3t", "Password=" + password) + "\n\nAdded text.";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => markdown.ReplaceContentAsync(content, CancellationToken.None));
        Assert.StartsWith("Blocks cannot be added, changed or removed", exception.Message);
        Assert.Equal(PageWithSecretBlock, await ReadStoredPageAsync(markdown));
    }

    [Fact]
    public async Task WhenTheContentRepeatsTheBlocksAsReadRawContentShowsThem_ThenTheSecretValuesAreKept()
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(PageWithSecretBlock);
        var content = markdown.ReadRawContent()!.Replace("The gateway", "The main gateway") + "\n\nAdded text.";

        // Act
        await markdown.ReplaceContentAsync(content, CancellationToken.None);
        await markdown.ReplaceTextAsync("Added text.", "Added and changed text.", CancellationToken.None);

        // Assert
        Assert.Contains("Password=***", content);
        Assert.Equal(
            PageWithSecretBlock.Replace("The gateway", "The main gateway") + "\n\nAdded and changed text.",
            await ReadStoredPageAsync(markdown));
    }

    [Fact]
    public async Task WhenAnAgentMayEditPageLayout_ThenLayoutAroundABlockWithASecretKeepsTheSecretAndTheBlock()
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(PageWithSecretBlock);
        var view = markdown.ReadRawContent()!;
        using var permission = MarkdownPageLayoutPermission.Enter(true);

        // Act
        await markdown.ReplaceContentAsync(
            "# Gateway\n\n:: HomeBlaze.Components.Card Title=Note\nA layout card.\n::\n\n" + view, CancellationToken.None);
        await markdown.OnFileChangedAsync(CancellationToken.None);
        var wrapped = await Record.ExceptionAsync(() => markdown.ReplaceContentAsync(
            ":: HomeBlaze.Components.Card Title=Around\n" + view.Replace("::\n", "::::\n").Replace(":: ", ":::: ") + "\n::",
            CancellationToken.None));
        var guessed = await Record.ExceptionAsync(() => markdown.ReplaceContentAsync(
            "# Gateway\n\n" + PageWithSecretBlock, CancellationToken.None));

        // Assert
        Assert.Contains("Password=***", view);
        Assert.Equal(
            "# Gateway\n\n:: HomeBlaze.Components.Card Title=Note\nA layout card.\n::\n\n" + PageWithSecretBlock,
            await ReadStoredPageAsync(markdown));
        Assert.IsType<ArgumentException>(wrapped);
        Assert.IsType<ArgumentException>(guessed);
    }

    [Theory]
    [InlineData("/Devices/motor/Password")]
    [InlineData("/Devices/motor/PlainNote")]
    [InlineData("/Docs/page.md/Content")]
    public async Task WhenAFormattedExpressionOrAValueWidgetReadsAPropertyThatIsNotDisplayable_ThenItShowsNothing(string path)
    {
        // Arrange
        var content =
            $"Raw: {{{{ {path} | raw }}}}, display: {{{{ {path} | display }}}}, public: {{{{ /Devices/motor/Host | raw }}}}.\n\n" +
            $":: HomeBlaze.Components.ValueTile#tile Path=\"../..{path}\"\n::\n\n" +
            $":: HomeBlaze.Components.Indicator#indicator Path=\"../..{path}\"\n::\n";
        var markdown = await CreatePageInGraphAsync(content, new EmbeddedSecretDevice { Password = "s3cr3t", Host = "visible" });
        var resolver = new SubjectPathResolver(() => null);
        var scope = (IPathScope)markdown;

        // Act
        var evaluated = markdown.ReadEvaluatedContent();
        var withSourcePaths = markdown.ReadEvaluatedContent(includeSourcePaths: true);
        var expressions = markdown.Children.Values.OfType<RenderExpression>().ToList();
        var tile = Assert.IsType<HomeBlaze.Components.ValueTile>(scope.TryGetNamedSubject("tile"));
        var indicator = Assert.IsType<HomeBlaze.Components.Indicator>(scope.TryGetNamedSubject("indicator"));
        var hostReading = HomeBlaze.Components.ValueReader.Read(resolver, tile, "../../Devices/motor/Host", null, null);

        // Assert
        Assert.StartsWith("Raw: , display: , public: visible.", evaluated);
        Assert.StartsWith("Raw: , display: , public: visible (/Devices/motor/Host).", withSourcePaths);
        Assert.All(expressions.Where(expression => expression.Path == path), expression =>
        {
            Assert.Null(expression.Value);
            Assert.Equal(string.Empty, expression.GetText(timeZone: null));
        });
        Assert.True(HomeBlaze.Components.ValueReader.Read(resolver, tile, tile.Path, null, null).IsMissing);
        Assert.Null(HomeBlaze.Components.ValueReader.ReadValue(resolver, indicator, indicator.Path));
        Assert.Equal("visible", hostReading.Value);
        Assert.DoesNotContain("s3cr3t", evaluated);
    }

    [Fact]
    public async Task WhenAnExpressionReadsTheRawSourceOfAPage_ThenItHasNoValue()
    {
        // Arrange: the source holds a secret value, which the content property has as written.
        var content =
            $":: {typeof(EmbeddedSecretDevice).FullName}#gateway Password=s3cr3t\n::\n\n" +
            "Own: {{ Content }}, relative: {{ ../page.md/Content }}, absolute: {{ /Docs/page.md/Content }}, title: {{ Title }}.";
        var markdown = await CreatePageInGraphAsync(content);

        // Act
        var evaluated = markdown.ReadEvaluatedContent();
        var expressions = markdown.Children.Values.OfType<RenderExpression>().ToDictionary(expression => expression.Path);

        // Assert
        Assert.EndsWith("Own: , relative: , absolute: , title: Page.", evaluated);
        Assert.DoesNotContain("s3cr3t", evaluated);
        Assert.Null(expressions["Content"].Value);
        Assert.Null(expressions["../page.md/Content"].Value);
        Assert.Null(expressions["/Docs/page.md/Content"].Value);
        Assert.Equal("Page", expressions["Title"].Value);
    }

    [Fact]
    public async Task WhenTheContentIsAtTheLimitAndTheHiddenValuesMakeTheFileLarger_ThenItIsWrittenWithThem()
    {
        // Arrange: the limit applies to the content as the caller writes it, so it tells nothing about the hidden values.
        var page = $":: {typeof(EmbeddedSecretDevice).FullName}#gateway Password={new string('s', 1000)}\n::\n\nText.";
        var markdown = await CreatePageInGraphAsync(page);
        var view = markdown.ReadRawContent()!;
        var content = view + new string('a', MarkdownFile.MaxContentSize - Encoding.UTF8.GetByteCount(view));

        // Act
        await markdown.ReplaceContentAsync(content, CancellationToken.None);

        // Assert
        var stored = await ReadStoredPageAsync(markdown);
        Assert.Equal(page + new string('a', MarkdownFile.MaxContentSize - Encoding.UTF8.GetByteCount(view)), stored);
        Assert.True(Encoding.UTF8.GetByteCount(stored) > MarkdownFile.MaxContentSize);
    }

    [Fact]
    public async Task WhenThePageEndsItsLinesWithCarriageReturnsAlone_ThenItIsRedactedAndAnEditKeepsTheSecret()
    {
        // Arrange
        var page = $"# Gateway\r\r:: {typeof(EmbeddedSecretDevice).FullName}#gateway Host=h Password=s3cr3t\r::\r\rText.";
        var markdown = await CreatePageInGraphAsync(page);

        // Act
        var view = markdown.ReadRawContent();
        await markdown.ReplaceTextAsync("Text.", "Changed text.", CancellationToken.None);

        // Assert
        Assert.DoesNotContain("s3cr3t", view);
        Assert.Equal(page.Replace("Text.", "Changed text."), await ReadStoredPageAsync(markdown));
    }

    [Fact]
    public async Task WhenABlockShowsASecretConfigurationPropertyInItsContent_ThenItsExpressionHasNoValue()
    {
        // Arrange
        var markdown = await CreatePageInGraphAsync(
            """
            :: HomeBlaze.Components.Card#card Title=Device
            Secret: {{ /Devices/motor/Password }}, public: {{ /Devices/motor/Host }}
            ::
            """,
            new EmbeddedSecretDevice { Password = "s3cr3t", Host = "visible" });
        var card = Assert.IsType<HomeBlaze.Components.Card>(markdown.Children["card"]);

        // Act
        var expressions = Assert.IsType<MarkdownFragment>(card.Content).Children.Values
            .OfType<RenderExpression>()
            .ToDictionary(expression => expression.Path);

        // Assert
        Assert.Null(expressions["/Devices/motor/Password"].Value);
        Assert.Equal("visible", expressions["/Devices/motor/Host"].Value);
    }

    [Fact]
    public async Task WhenPageIsReloadedWithUnrelatedChange_ThenEmbeddedSubjectIsKept()
    {
        // Arrange
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "embedded-unrelated.md", CreatePageWithMotor("# First heading", targetSpeed: 1000));
        var markdown = CreateMarkdownFile(serviceProvider, storage, "embedded-unrelated.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        var motor = Assert.IsType<Motor>(markdown.Children["mymotor"]);
        motor.CurrentSpeed = 750;

        // Act
        await WriteFileAsync(storage, "embedded-unrelated.md", CreatePageWithMotor("# Second heading", targetSpeed: 1000));
        await markdown.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Same(motor, markdown.Children["mymotor"]);
        Assert.Equal(750, motor.CurrentSpeed);
        Assert.Contains(markdown.Children.Values.OfType<HtmlSegment>(), segment => segment.Html.Contains("Second heading"));
    }

    [Fact]
    public async Task WhenPageIsReloadedWithChangedBlockProperty_ThenEmbeddedSubjectIsUpdatedInPlace()
    {
        // Arrange
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "embedded-property.md", CreatePageWithMotor("# Heading", targetSpeed: 1000));
        var markdown = CreateMarkdownFile(serviceProvider, storage, "embedded-property.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        var motor = Assert.IsType<Motor>(markdown.Children["mymotor"]);
        motor.CurrentSpeed = 750;

        // Act
        await WriteFileAsync(storage, "embedded-property.md", CreatePageWithMotor("# Heading", targetSpeed: 2000));
        await markdown.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Same(motor, markdown.Children["mymotor"]);
        Assert.Equal(2000, motor.TargetSpeed);
        Assert.Equal("MyMotor", motor.Name);
        Assert.Equal(750, motor.CurrentSpeed);
    }

    [Fact]
    public async Task WhenPageIsReloadedWithDifferentBlockType_ThenEmbeddedSubjectIsReplaced()
    {
        // Arrange: the block has no arguments, because a removed argument replaces the subject as well,
        // and the type must be the only reason here.
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var motorContent = $"""
            # Heading

            :: {typeof(Motor).FullName}#mymotor
            ::
            """;
        await WriteFileAsync(storage, "embedded-type.md", motorContent);
        var markdown = CreateMarkdownFile(serviceProvider, storage, "embedded-type.md");
        await markdown.OnFileChangedAsync(CancellationToken.None);

        var motor = Assert.IsType<Motor>(markdown.Children["mymotor"]);

        // Act
        var content = $"""
            # Heading

            :: {typeof(FluentStorageContainer).FullName}#mymotor StorageType=inmemory
            ::
            """;
        await WriteFileAsync(storage, "embedded-type.md", content);
        await markdown.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.NotSame(motor, markdown.Children["mymotor"]);
        Assert.IsType<FluentStorageContainer>(markdown.Children["mymotor"]);
    }

    private static string CreatePageWithMotor(string heading, int targetSpeed) => $"""
        {heading}

        :: {typeof(Motor).FullName}#mymotor Name=MyMotor TargetSpeed={targetSpeed}
        ::
        """;

    private static async Task<(FluentStorageContainer storage, IServiceProvider serviceProvider)> CreateInMemoryStorageAsync()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(Motor).Assembly);
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(EmbeddedSecretDevice).Assembly);
        typeProvider.AddAssembly(typeof(HomeBlaze.Components.Card).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        // The context of the application: an embedded subject is updated through the configuration
        // properties of its registry entry when its page is reloaded.
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);

        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();

        var serviceProvider = services.BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>();

        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);
        storage.StorageType = "inmemory";
        await storage.ConnectAsync(CancellationToken.None);
        return (storage, serviceProvider);
    }

    /// <summary>
    /// Creates the graph /Devices/motor (the given device, by default a motor with CurrentSpeed 750) and /Docs/page.md
    /// with the given page content.
    /// </summary>
    private static async Task<MarkdownFile> CreatePageInGraphAsync(string content, IInterceptorSubject? device = null)
    {
        device ??= new Motor { CurrentSpeed = 750 };
        var (storage, serviceProvider) = await CreateInMemoryStorageAsync();
        var context = serviceProvider.GetRequiredService<IInterceptorSubjectContext>();
        await WriteFileAsync(storage, "Docs/page.md", content);

        VirtualFolder? root = null;
        var pathResolver = new SubjectPathResolver(() => root);
        var parser = new MarkdownContentParser(
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), pathResolver, serviceProvider.GetRequiredService<TypeProvider>());
        var markdown = new MarkdownFile(storage, "Docs/page.md", parser, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>());

        root = new VirtualFolder(storage, "");
        ((IInterceptorSubject)root).Context.AddFallbackContext(context);
        root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Devices"] = new VirtualFolder(storage, "Devices/")
            {
                Children = new Dictionary<string, IInterceptorSubject> { ["motor"] = device }
            },
            ["Docs"] = new VirtualFolder(storage, "Docs/")
            {
                Children = new Dictionary<string, IInterceptorSubject> { ["page.md"] = markdown }
            }
        };

        await markdown.OnFileChangedAsync(CancellationToken.None);
        return markdown;
    }

    private static MarkdownFile CreateMarkdownFile(IServiceProvider serviceProvider, FluentStorageContainer storage, string path)
    {
        var parser = serviceProvider.GetRequiredService<MarkdownContentParser>();
        var serializer = serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>();
        return new MarkdownFile(storage, path, parser, serializer);
    }

    private static async Task<string> ReadStoredPageAsync(MarkdownFile markdown)
    {
        await using var stream = await markdown.ReadAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteFileAsync(FluentStorageContainer storage, string path, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, CancellationToken.None);
    }
}

[InterceptorSubject]
public partial class EmbeddedSecretDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    /// <summary>A plain property, which a page shows nothing of.</summary>
    public string PlainNote => "plain " + Password;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class SecretSlotHost : IConfigurable
{
    public SecretSlotHost()
    {
        Gateways = [];
    }

    [Slot("Gateway")]
    public partial EmbeddedSecretDevice[] Gateways { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
