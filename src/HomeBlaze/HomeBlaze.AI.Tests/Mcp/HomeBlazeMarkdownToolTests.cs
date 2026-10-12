using System.Text.Json;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public sealed class HomeBlazeMarkdownToolTests : IDisposable
{
    private const string NotesPath = "/Storage/Notes.md";
    private const string DocsPath = "/Storage/Docs";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-markdown-tools-");
    private readonly List<FluentStorageContainer> _storages = [];

    [Fact]
    public async Task WhenEditingAMarkdownFile_ThenTheUniquePassageIsReplaced()
    {
        // Arrange
        var (root, storage, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools["edit_markdown"], new { path = NotesPath, old_text = "cold", new_text = "too warm" });

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("# Notes\n\nThe freezer is too warm.", ReadFile("Notes.md"));
        Assert.Equal("# Notes\n\nThe freezer is too warm.", GetNotes(storage).Content);
    }

    [Fact]
    public async Task WhenWritingAMarkdownFile_ThenItsWholeContentIsReplaced()
    {
        // Arrange
        var (root, storage, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools["write_markdown"], new { path = NotesPath, content = "# Rewritten" });

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("# Rewritten", ReadFile("Notes.md"));
        Assert.Equal("# Rewritten", GetNotes(storage).Content);
    }

    [Theory]
    [InlineData(DocsPath, "Docs/New.md")]
    [InlineData("/Storage", "New.md")]
    public async Task WhenCreatingAMarkdownFile_ThenItIsWrittenAndAddedAsAPage(string folderPath, string expectedFile)
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools["create_markdown"], new { folder_path = folderPath, name = "New.md", content = "# New page" });
        var readBack = await CallAsync(tools["invoke_query"], new { path = $"{folderPath}/New.md", method = "ReadRawContent" });

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("# New page", ReadFile(expectedFile));
        Assert.Equal("# New page", readBack.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("edit_markdown")]
    [InlineData("write_markdown")]
    public async Task WhenTheTargetIsNotAMarkdownFile_ThenTheToolRefusesIt(string toolName)
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools[toolName], new { path = DocsPath, old_text = "a", new_text = "b", content = "c" });

        // Assert
        Assert.Equal($"'{DocsPath}' is not a Markdown file.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTheTargetOfCreateMarkdownIsNotAFolder_ThenTheToolRefusesIt()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools["create_markdown"], new { folder_path = NotesPath, name = "New.md", content = "# New" });

        // Assert
        Assert.Equal($"'{NotesPath}' is not a local storage folder.", result.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "New.md")));
    }

    [Fact]
    public async Task WhenTheStorageIsNotLocal_ThenTheToolsRefuseItsFilesAndFolders()
    {
        // Arrange
        var (root, storage, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);
        storage.StorageType = "azure-blob";

        // Act
        var edit = await CallAsync(tools["edit_markdown"], new { path = NotesPath, old_text = "cold", new_text = "warm" });
        var create = await CallAsync(tools["create_markdown"], new { folder_path = DocsPath, name = "New.md", content = "# New" });

        // Assert
        Assert.Equal($"'{NotesPath}' is not in local storage; only local Markdown files can be changed.", edit.GetProperty("error").GetString());
        Assert.Equal($"'{DocsPath}' is not a local storage folder.", create.GetProperty("error").GetString());
        Assert.Equal("# Notes\n\nThe freezer is cold.", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenThePathDoesNotExist_ThenTheToolReturnsAnError()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools["write_markdown"], new { path = "/Storage/Missing.md", content = "# New" });

        // Assert
        Assert.Equal("Subject not found.", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenAnArgumentIsMissing_ThenTheToolNamesIt()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var result = await CallAsync(tools["edit_markdown"], new { path = NotesPath, old_text = "cold" });

        // Assert
        Assert.Equal("Missing parameter 'new_text'.", result.GetProperty("error").GetString());
        Assert.Equal("# Notes\n\nThe freezer is cold.", ReadFile("Notes.md"));
    }

    [Fact]
    public async Task WhenAnAgentChangesOrAddsABlock_ThenTheToolsReturnTheBlockRuleAndTheFilesAreUnchanged()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);
        WriteFile("Notes.md", "# Notes\n\n:: HomeBlaze.Samples.Motor#motor TargetSpeed=1000\n::\n\nThe freezer is cold.");

        // Act
        var edit = await CallAsync(tools["edit_markdown"], new { path = NotesPath, old_text = "is cold.", new_text = "is cold.\n\n:: HomeBlaze.Samples.Motor#other\n::" });
        var write = await CallAsync(tools["write_markdown"], new { path = NotesPath, content = "# Notes\n\nNo motor any more." });
        var create = await CallAsync(tools["create_markdown"], new { folder_path = "/Storage", name = "New.md", content = ":: HomeBlaze.Components.Widget Path=/Storage\n::" });

        // Assert
        Assert.StartsWith("Blocks cannot be added, changed or removed", edit.GetProperty("error").GetString());
        Assert.StartsWith("Blocks cannot be added, changed or removed", write.GetProperty("error").GetString());
        Assert.StartsWith("Blocks cannot be added, changed or removed", create.GetProperty("error").GetString());
        Assert.Equal("# Notes\n\n:: HomeBlaze.Samples.Motor#motor TargetSpeed=1000\n::\n\nThe freezer is cold.", ReadFile("Notes.md"));
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "New.md")));
    }

    [Fact]
    public async Task WhenTheOperationRejectsAnArgument_ThenItsMessageIsReturned()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var tools = CreateTools(services, root);

        // Act
        var edit = await CallAsync(tools["edit_markdown"], new { path = NotesPath, old_text = "hot", new_text = "warm" });
        var create = await CallAsync(tools["create_markdown"], new { folder_path = "/Storage", name = "Notes.md", content = "# Other" });
        var badName = await CallAsync(tools["create_markdown"], new { folder_path = "/Storage", name = "Notes.json", content = "{}" });

        // Assert
        Assert.StartsWith("The text to replace was not found in Notes.md.", edit.GetProperty("error").GetString());
        Assert.StartsWith("'Notes.md' already exists in this folder.", create.GetProperty("error").GetString());
        Assert.StartsWith("'Notes.json' is not a valid Markdown file name", badName.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "Notes.json")));
    }

    [Fact]
    public async Task WhenAnOperationFilterRefusesTheOperation_ThenTheToolsRefuseItToo()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var provider = new HomeBlazeMarkdownToolProvider(
            () => root, new StateAttributePathProvider(), services, NullLogger<HomeBlazeMarkdownToolProvider>.Instance,
            isReadOnly: false, operationFilter: (_, method) => $"{method.PropertyName} is refused.");
        var tools = provider.GetTools().ToDictionary(tool => tool.Name);

        // Act
        var edit = await CallAsync(tools["edit_markdown"], new { path = NotesPath, old_text = "cold", new_text = "warm" });
        var write = await CallAsync(tools["write_markdown"], new { path = NotesPath, content = "# Rewritten" });
        var create = await CallAsync(tools["create_markdown"], new { folder_path = DocsPath, name = "New.md", content = "# New" });

        // Assert
        Assert.Equal("ReplaceText is refused.", edit.GetProperty("error").GetString());
        Assert.Equal("ReplaceContent is refused.", write.GetProperty("error").GetString());
        Assert.Equal("CreateMarkdownFile is refused.", create.GetProperty("error").GetString());
        Assert.Equal("# Notes\n\nThe freezer is cold.", ReadFile("Notes.md"));
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "Docs", "New.md")));
    }

    [Fact]
    public async Task WhenTheConfigurationIsReadOnly_ThenTheMarkdownToolsAreNotOffered()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();

        // Act
        var readOnlyTools = CreateTools(services, root, isReadOnly: true);
        var writableTools = CreateTools(services, root, isReadOnly: false);
        var externalTools = new McpToolFactory(root, HomeBlazeMcpConfigurationFactory.Create(services, () => root, isReadOnly: false))
            .CreateTools().ToDictionary(tool => tool.Name);

        // Assert
        string[] markdownTools = ["edit_markdown", "write_markdown", "create_markdown"];
        Assert.All(markdownTools, name => Assert.DoesNotContain(name, readOnlyTools.Keys));
        Assert.All(markdownTools, name => Assert.Same(McpToolAnnotations.Destructive, writableTools[name].Annotations));
        Assert.All(markdownTools, name => Assert.Contains(name, externalTools.Keys));
    }

    [Fact]
    public async Task WhenAnAgentUsesAMarkdownTool_ThenTheCallIsRecordedAndCountsAgainstTheBudget()
    {
        // Arrange
        var (root, _, services) = await CreateGraphAsync();
        var available = CreateTools(services, root, isReadOnly: AgentToolSet.IsReadOnly(["edit_markdown"])).Values.ToList();
        var toolSet = AgentToolSet.Create(available, ["edit_markdown"], CreateMemoryTool(), maxToolCalls: 1, progress: null);
        var edit = toolSet.Tools.Single(tool => tool.Name == "edit_markdown");

        // Act
        var first = await CallAsync(edit, new { path = NotesPath, old_text = "cold", new_text = "warm" });
        var second = await CallAsync(edit, new { path = NotesPath, old_text = "warm", new_text = "hot" });

        // Assert
        Assert.Equal(["edit_markdown", "update_memory"], toolSet.Tools.Select(tool => tool.Name));
        Assert.True(first.GetProperty("success").GetBoolean());
        Assert.Contains("budget", second.GetProperty("error").GetString());
        Assert.Equal("# Notes\n\nThe freezer is warm.", ReadFile("Notes.md"));
        Assert.Equal(["edit_markdown", "edit_markdown"], toolSet.Calls.Select(call => call.Name));
        Assert.Equal([false, true], toolSet.Calls.Select(call => call.IsError));
    }

    [Fact]
    public async Task WhenAnAgentCallsReplaceTextThroughInvokeOperation_ThenThePageIsChanged()
    {
        // Arrange
        var (root, storage, services) = await CreateGraphAsync();
        var available = CreateTools(services, root, isReadOnly: AgentToolSet.IsReadOnly(["invoke_operation"])).Values.ToList();
        var toolSet = AgentToolSet.Create(available, ["invoke_operation"], CreateMemoryTool(), maxToolCalls: 5, progress: null);
        var invokeOperation = toolSet.Tools.Single(tool => tool.Name == "invoke_operation");

        // Act
        var result = await CallAsync(invokeOperation, new
        {
            path = NotesPath,
            method = "ReplaceText",
            parameters = new { oldText = "cold", newText = "at -18 °C" }
        });

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("# Notes\n\nThe freezer is at -18 °C.", GetNotes(storage).Content);
        Assert.Equal("invoke_operation", Assert.Single(toolSet.Calls).Name);
    }

    private async Task<(AgentTestRoot Root, FluentStorageContainer Storage, IServiceProvider Services)> CreateGraphAsync()
    {
        WriteFile("Notes.md", "# Notes\n\nThe freezer is cold.");
        WriteFile("Docs/Readme.md", "# Readme");

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        AgentTestRoot? root = null;
        services.AddSingleton(new SubjectPathResolver(() => root));
        services.AddSingleton<MarkdownContentParser>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
        _storages.Add(storage);

        root = new AgentTestRoot(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage } };
        await storage.ConnectAsync(CancellationToken.None);
        return (root, storage, serviceProvider);
    }

    private static Dictionary<string, McpToolInfo> CreateTools(IServiceProvider services, AgentTestRoot root, bool isReadOnly = false)
    {
        var configuration = HomeBlazeMcpConfigurationFactory.CreateForAgents(services, () => root, isReadOnly);
        return new McpToolFactory(root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }

    private static async Task<JsonElement> CallAsync(McpToolInfo tool, object input) =>
        JsonSerializer.SerializeToElement(await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None));

    private static McpToolInfo CreateMemoryTool() => new()
    {
        Name = "update_memory",
        Description = "update_memory",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        Handler = (_, _) => Task.FromResult<object?>(null)
    };

    private static MarkdownFile GetNotes(FluentStorageContainer storage) => Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(_directory.FullName, relativePath));

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _directory.Delete(recursive: true);
    }
}
