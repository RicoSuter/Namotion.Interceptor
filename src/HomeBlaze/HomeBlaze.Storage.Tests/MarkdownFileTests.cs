using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Tests.Pages;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

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
    public async Task WhenReadingTheRawContent_ThenSecretValuesOfEmbeddedSubjectsAreHidden()
    {
        // Arrange
        var content = $$"""
            # Gateway

            ```subject(gateway)
            {
              "$type": "{{typeof(EmbeddedSecretDevice).FullName}}",
              "host": "192.168.1.10",
              "password": "s3cr3t"
            }
            ```

            ```subject(plain)
            {
              "$type": "{{typeof(Motor).FullName}}",
              "name": "Plain",
              "targetSpeed": 1000
            }
            ```

            ```subject(unknown)
            {
              "$type": "Unknown.Device",
              "token": "also secret"
            }
            ```
            """;
        var markdown = await CreatePageInGraphAsync(content);

        // Act
        var result = markdown.ReadRawContent();

        // Assert
        Assert.IsType<EmbeddedSecretDevice>(markdown.Children["gateway"]);
        Assert.DoesNotContain("s3cr3t", result);
        Assert.DoesNotContain("also secret", result);
        Assert.Contains("\"password\": \"***\"", result);
        Assert.Contains("\"host\": \"192.168.1.10\"", result);
        Assert.Contains($$"""
            ```subject(plain)
            {
              "$type": "{{typeof(Motor).FullName}}",
              "name": "Plain",
              "targetSpeed": 1000
            }
            ```
            """, result);
        Assert.Contains("\"$type\": \"Unknown.Device\"", result);
        Assert.Equal(content, markdown.Content);
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

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
