using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Agents.ClaudeCli;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public sealed class HomeBlazeMetadataToolTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-metadata-tools-");
    private readonly List<FluentStorageContainer> _storages = [];

    private const string FolderPropertyMessage = "A folder and the root of a storage have metadata of their own, but their properties have none.";

    private const string NoMetadataMessage =
        "Only a storage, its folders, the subjects of its JSON files and the subjects and properties inside them have metadata.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenMetadataIsSetThroughTheStorage_ThenBrowseAndGetPropertyShowIt(bool isAgent)
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        var (tools, _) = await CreateToolsAsync(isAgent);

        // Act
        var subjectResult = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage",
            method = "SetMetadata",
            parameters = new { path = "Docs/Pump.json", values = new { navTitle = "Pump" } }
        });
        var propertyResult = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage",
            method = "SetMetadata",
            parameters = new { path = "/Docs/Pump.json", property = "FileSize", values = new { comment = "grows" } }
        });
        var browseResult = await CallAsync(tools["browse"], new
        {
            path = "/Storage/Docs/Pump.json", depth = 0, includeProperties = true, includeAttributes = true, format = "json"
        });
        var subjectMetadata = await CallAsync(tools["get_property"], new { path = "/Storage/Docs/Pump.json/HomeBlaze:Metadata" });
        var propertyMetadata = await CallAsync(tools["get_property"], new { path = "/Storage/Docs/Pump.json/FileSize@HomeBlaze:Metadata" });

        // Assert
        Assert.True(subjectResult.GetProperty("success").GetBoolean());
        Assert.True(propertyResult.GetProperty("success").GetBoolean());
        var properties = browseResult.GetProperty("result").GetProperty("properties");
        Assert.Equal("Pump", properties.GetProperty("HomeBlaze:Metadata").GetProperty("value").GetProperty("navTitle").GetString());
        var attribute = Assert.Single(properties.GetProperty("FileSize").GetProperty("attributes").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "HomeBlaze:Metadata");
        Assert.Equal("grows", attribute.GetProperty("value").GetProperty("comment").GetString());
        Assert.Equal("Pump", subjectMetadata.GetProperty("value").GetProperty("navTitle").GetString());
        Assert.Equal("grows", propertyMetadata.GetProperty("value").GetProperty("comment").GetString());
    }

    [Fact]
    public async Task WhenAReaderSetsMetadata_ThenTheOperationIsNotPermittedAndNothingIsWritten()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        var (tools, _) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(BuiltInRoles.ReaderName));

        // Act
        var result = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Docs/Pump.json", values = new { navTitle = "Pump" } }
        });

        // Assert
        Assert.Equal("Operation 'SetMetadata' is not permitted. The access policy does not allow it.", result.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "Docs", "Pump.metadata.json")));
    }

    [Theory]
    [InlineData(null, "/Storage/Docs/Pump.json/HomeBlaze:Metadata", "Metadata of 'Docs/Pump.json' is not writable. The access policy does not allow it.")]
    [InlineData("FileSize", "/Storage/Docs/Pump.json/FileSize@HomeBlaze:Metadata", "Metadata of property 'FileSize' of 'Docs/Pump.json' is not writable. The access policy does not allow it.")]
    public async Task WhenARoleMayRunSetMetadataButNotWriteMetadata_ThenItIsRefusedAndTheTargetStillHasNoMetadata(
        string? property, string metadataPath, string expectedError)
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        var role = new AuthorizationRole
        {
            Name = "NoMetadataWrites",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Tags = [MemberTags.Metadata], Actions = [PermissionAction.Write] }]
        };
        var logger = new RecordingLogger();
        var accessPolicy = new AuthorizationMcpAccessPolicy(TestAccessPolicies.Create(role.Name, role).Policy, logger, "Writer");
        var (tools, storage) = await CreateToolsAsync(accessPolicy: accessPolicy);

        // Act
        var result = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Docs/Pump.json", property, values = new { comment = "x" } }
        });
        var metadata = await CallAsync(tools["get_property"], new { path = metadataPath });

        // Assert
        Assert.Equal(expectedError, result.GetProperty("error").GetString());
        Assert.StartsWith("Path not found", metadata.GetProperty("error").GetString());
        var pump = ((VirtualFolder)storage.Children["Docs"]).Children["Pump.json"];
        Assert.Null(pump.GetMetadata());
        Assert.Null(pump.TryGetRegisteredSubject()!.TryGetProperty("FileSize")!.GetMetadata());
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "Docs", "Pump.metadata.json")));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task WhenARoleMayNotWriteMetadataThatExists_ThenItIsKept()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "navTitle": "Pump" }""");
        var role = new AuthorizationRole
        {
            Name = "NoMetadataWrites",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Members = [KnownAttributes.Metadata], Actions = [PermissionAction.Write] }]
        };
        var (tools, _) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(role.Name, role));

        // Act
        var result = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Docs/Pump.json", values = new { navTitle = "Other" } }
        });
        var metadata = await CallAsync(tools["get_property"], new { path = "/Storage/Docs/Pump.json/HomeBlaze:Metadata" });

        // Assert
        Assert.Equal("Metadata of 'Docs/Pump.json' is not writable. The access policy does not allow it.", result.GetProperty("error").GetString());
        Assert.Equal("Pump", metadata.GetProperty("value").GetProperty("navTitle").GetString());
    }

    [Fact]
    public async Task WhenARoleHidesABranch_ThenSetMetadataRefusesItsSubjectsAsMissingOnes()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        var role = new AuthorizationRole
        {
            Name = "NoDocs",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Paths = ["/Storage/Docs/**"], Actions = [PermissionAction.Read] }]
        };
        var (tools, storage) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(role.Name, role));

        // Act
        var result = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Docs/Pump.json", values = new { navTitle = "Pump" } }
        });

        // Assert
        Assert.Equal("No subject at 'Docs/Pump.json' in this storage.", result.GetProperty("error").GetString());
        Assert.Null(((VirtualFolder)storage.Children["Docs"]).Children["Pump.json"].TryGetRegisteredSubject()!.TryGetProperty(KnownAttributes.Metadata));
    }

    [Fact]
    public async Task WhenMetadataWasRemoved_ThenBrowseLeavesItOut()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "navTitle": "Pump", "$properties": { "FileSize": { "comment": "grows" } } }""");
        var (tools, storage) = await CreateToolsAsync();

        // Act
        await storage.SetMetadataAsync("Docs/Pump.json", null, JsonElement.Parse("""{ "navTitle": null }"""), CancellationToken.None);
        await storage.SetMetadataAsync("Docs/Pump.json", "FileSize", JsonElement.Parse("""{ "comment": null }"""), CancellationToken.None);
        var browseResult = await CallAsync(tools["browse"], new
        {
            path = "/Storage/Docs/Pump.json", depth = 0, includeProperties = true, includeAttributes = true, format = "json"
        });

        // Assert
        var properties = browseResult.GetProperty("result").GetProperty("properties");
        Assert.False(properties.TryGetProperty("HomeBlaze:Metadata", out _));
        Assert.DoesNotContain(
            properties.GetProperty("FileSize").TryGetProperty("attributes", out var attributes) ? attributes.EnumerateArray().ToArray() : [],
            item => item.GetProperty("name").GetString() == "HomeBlaze:Metadata");
    }

    [Fact]
    public async Task WhenSetPropertyWritesMetadata_ThenItIsRefused()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "navTitle": "Pump" }""");
        var (tools, _) = await CreateToolsAsync();

        // Act
        var result = await CallAsync(tools["set_property"], new { path = "/Storage/Docs/Pump.json/HomeBlaze:Metadata", value = new { navTitle = "Other" } });

        // Assert
        Assert.Contains("not writable", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTheOrderIsSetAndRead_ThenTheNamesComeFirst()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Logs/info.md", "# Info");
        WriteFile("Logs/debug.md", "# Debug");
        var (tools, _) = await CreateToolsAsync();

        // Act
        var setRootOrder = await CallAsync(tools["invoke_operation"], new { path = "/Storage", method = "SetOrder", parameters = new { names = new[] { "Logs" } } });
        var rootOrder = await CallAsync(tools["invoke_query"], new { path = "/Storage", method = "GetOrder" });
        var setFolderOrder = await CallAsync(tools["invoke_operation"], new { path = "/Storage/Logs", method = "SetOrder", parameters = new { names = new[] { "info.md" } } });
        var folderOrder = await CallAsync(tools["invoke_query"], new { path = "/Storage/Logs", method = "GetOrder" });

        // Assert
        Assert.True(setRootOrder.GetProperty("success").GetBoolean());
        Assert.Equal(["Logs", "Docs"], rootOrder.GetProperty("result").EnumerateArray().Select(name => name.GetString()));
        Assert.True(setFolderOrder.GetProperty("success").GetBoolean());
        Assert.Equal(["info.md", "debug.md"], folderOrder.GetProperty("result").EnumerateArray().Select(name => name.GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenARoleMayNotWriteMetadata_ThenSetOrderIsRefused(bool deniesMetadataWrites)
    {
        // Arrange
        WriteFile("Logs/info.md", "# Info");
        WriteFile("Logs/debug.md", "# Debug");
        var role = new AuthorizationRole
        {
            Name = "NoMetadataWrites",
            Includes = [BuiltInRoles.OperatorName],
            Deny = deniesMetadataWrites ? [new PermissionRule { Tags = [MemberTags.Metadata], Actions = [PermissionAction.Write] }] : []
        };
        var (tools, _) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(role.Name, role));

        // Act
        var result = await CallAsync(tools["invoke_operation"], new { path = "/Storage/Logs", method = "SetOrder", parameters = new { names = new[] { "info.md" } } });
        var order = await CallAsync(tools["invoke_query"], new { path = "/Storage/Logs", method = "GetOrder" });

        // Assert
        if (deniesMetadataWrites)
        {
            Assert.Equal("Metadata of '/Storage/Logs' is not writable. The access policy does not allow it.", result.GetProperty("error").GetString());
            Assert.Equal(["debug.md", "info.md"], order.GetProperty("result").EnumerateArray().Select(name => name.GetString()));
            Assert.False(File.Exists(Path.Combine(_directory.FullName, "Logs", ".metadata.json")));
        }
        else
        {
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Equal(["info.md", "debug.md"], order.GetProperty("result").EnumerateArray().Select(name => name.GetString()));
        }
    }

    [Fact]
    public async Task WhenAFolderHasAnAgentOrAHiddenBranch_ThenGetOrderLeavesThemOut()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Secret/Valve.json", """{ "speed": 3 }""");
        WriteFile("Agent.json", $$"""{ "$type": "{{typeof(ClaudeCliAgent).FullName}}" }""");
        var (tools, storage) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(HiddenSecretRole.Name, HiddenSecretRole));

        // Act
        var result = await CallAsync(tools["invoke_query"], new { path = "/Storage", method = "GetOrder" });

        // Assert
        Assert.IsType<ClaudeCliAgent>(storage.Children["Agent"]);
        Assert.Equal(["Agent", "Docs", "Secret"], storage.GetOrder().Order(StringComparer.Ordinal));
        Assert.Equal(["Docs"], result.GetProperty("result").EnumerateArray().Select(name => name.GetString()));
    }

    [Theory]
    [InlineData("Secret")]
    [InlineData("Missing")]
    public async Task WhenAnOrderNamesAHiddenChild_ThenItIsRefusedAsAnUnknownName(string name)
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Secret/Valve.json", """{ "speed": 3 }""");
        var (tools, _) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(HiddenSecretRole.Name, HiddenSecretRole));
        var expectedError = $"Names that are not children of the folder: '{name}'. Its children: 'Docs'.";

        // Act
        var setOrder = await CallAsync(tools["invoke_operation"], new { path = "/Storage", method = "SetOrder", parameters = new { names = new[] { name } } });
        var setMetadata = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "", values = new { order = new[] { name } } }
        });

        // Assert
        Assert.Equal(expectedError, setOrder.GetProperty("error").GetString());
        Assert.Equal(expectedError, setMetadata.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_directory.FullName, ".metadata.json")));
    }

    private static readonly AuthorizationRole HiddenSecretRole = new()
    {
        Name = "NoSecret",
        Includes = [BuiltInRoles.OperatorName],
        Deny = [new PermissionRule { Paths = ["/Storage/Secret/**"], Actions = [PermissionAction.Read] }]
    };

    [Fact]
    public async Task WhenAWriteIsRefused_ThenTheAgentIsToldWhy()
    {
        // Arrange
        WriteFile("Docs/Readme.md", "# Readme");
        WriteFile("Docs/.metadata.json", "not json");
        var (tools, _) = await CreateToolsAsync();

        // Act
        var unreadable = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Docs", values = new { title = "Docs" } }
        });
        var unknownPath = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Gone", values = new { title = "Docs" } }
        });
        var unknownName = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetOrder", parameters = new { names = new[] { "gone.md" } }
        });

        // Assert
        Assert.Contains("could not be read", unreadable.GetProperty("error").GetString());
        Assert.Contains("No subject at 'Gone'", unknownPath.GetProperty("error").GetString());
        Assert.Contains("'gone.md'", unknownName.GetProperty("error").GetString());
        Assert.Equal("not json", await File.ReadAllTextAsync(Path.Combine(_directory.FullName, "Docs", ".metadata.json")));
    }

    [Theory]
    [InlineData("Docs/Pump.json", "Secret", "The subject has no property 'Secret'.")]
    [InlineData("Docs/Pump.json", "Missing", "The subject has no property 'Missing'.")]
    [InlineData("Docs", "Secret", FolderPropertyMessage)]
    [InlineData("Docs", "Missing", FolderPropertyMessage)]
    [InlineData("Docs/Readme.md", "Secret", NoMetadataMessage)]
    [InlineData("Docs/Readme.md", "Missing", NoMetadataMessage)]
    public async Task WhenAnAgentSetsTheMetadataOfAPropertyHiddenFromAgents_ThenItIsRefusedAsAMissingOne(
        string path, string property, string expectedError)
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Readme.md", "# Readme");
        var (tools, storage) = await CreateToolsAsync(isAgent: true);
        var folder = (VirtualFolder)storage.Children["Docs"];
        var target = path == "Docs" ? folder : folder.Children[Path.GetFileName(path)];
        target.TryGetRegisteredSubject()!.AddProperty("Secret", typeof(int), _ => 1, null);

        // Act
        var result = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path, property, values = new { comment = "x" } }
        });

        // Assert
        Assert.Equal(expectedError, result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenSetMetadataHasNoPathOrNoJsonObject_ThenTheCallerIsToldWhy()
    {
        // Arrange
        WriteFile("Docs/Readme.md", "# Readme");
        var (tools, storage) = await CreateToolsAsync();

        // Act
        var nullPath = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = (string?)null, values = new { title = "Docs" } }
        });
        var noObject = await CallAsync(tools["invoke_operation"], new
        {
            path = "/Storage", method = "SetMetadata", parameters = new { path = "Docs", values = new[] { "Docs" } }
        });
        var noValues = await Assert.ThrowsAsync<ArgumentException>(() => storage.SetMetadataAsync("Docs", null, default, CancellationToken.None));

        // Assert
        Assert.Contains("path", nullPath.GetProperty("error").GetString());
        Assert.Contains("JSON object", noObject.GetProperty("error").GetString());
        Assert.Equal("values", noValues.ParamName);
    }

    [Fact]
    public async Task WhenTheMethodsAreListed_ThenTheyAreForAgentsOnly()
    {
        // Arrange
        WriteFile("Docs/Readme.md", "# Readme");
        var (_, storage) = await CreateToolsAsync();
        var folder = (VirtualFolder)storage.Children["Docs"];

        // Act
        var storageMethodsForPeople = storage.TryGetRegisteredSubject()!.GetAllMethods(ExclusionTarget.UserInterface);
        var storageMethodsForAgents = storage.TryGetRegisteredSubject()!.GetAllMethods(ExclusionTarget.Agents);
        var folderMethodsForPeople = folder.TryGetRegisteredSubject()!.GetAllMethods(ExclusionTarget.UserInterface);
        var folderMethodsForAgents = folder.TryGetRegisteredSubject()!.GetAllMethods(ExclusionTarget.Agents);

        // Assert
        Assert.DoesNotContain(storageMethodsForPeople, method => method.Title is "Set Metadata" or "Get Order" or "Set Order");
        Assert.DoesNotContain(folderMethodsForPeople, method => method.Title is "Get Order" or "Set Order");
        Assert.Contains(storageMethodsForAgents, method => method.Title == "Set Metadata");
        Assert.Contains(folderMethodsForAgents, method => method.Title == "Set Order");
    }

    [Fact]
    public async Task WhenMetadataIsSearched_ThenTheMatchesAreReturnedAsSetMetadataTakesThem()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "tags": ["floor-2"], "$properties": { "FileSize": { "notes": "Shrinks." } } }""");
        WriteFile("Docs/Fan.json", """{ "speed": 2 }""");
        WriteFile("Docs/Fan.metadata.json", """{ "$properties": { "FileSize": { "notes": "Grows." } } }""");
        var (tools, _) = await CreateToolsAsync();

        // Act
        var result = await CallAsync(tools["invoke_query"], new
        {
            path = "/Storage", method = "SearchMetadata", parameters = new { key = "notes", value = "Grows.", path = "Docs" }
        });

        // Assert
        Assert.Equal(["Docs/Fan.json|FileSize"], GetMatches(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenMetadataIsSearchedThroughTheTools_ThenTheSubjectsAndPropertiesHiddenFromTheToolsAreLeftOut(bool isAgent)
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "tags": ["floor-2"] }""");
        WriteFile("Docs/Hidden.json", $$"""{ "$type": "{{typeof(AgentExcludedDevice).FullName}}" }""");
        WriteFile("Docs/Hidden.metadata.json", """{ "tags": ["floor-2"] }""");
        WriteFile("Docs/Device.json", $$"""{ "$type": "{{typeof(MetadataSearchTestDevice).FullName}}" }""");
        WriteFile("Docs/Device.metadata.json", """{ "tags": ["floor-2"], "$properties": { "Host": { "tags": ["floor-2"] } } }""");
        var (tools, _) = await CreateToolsAsync(isAgent);

        // Act
        var result = await CallAsync(tools["invoke_query"], new { path = "/Storage", method = "SearchMetadata", parameters = new { tag = "floor-2" } });

        // Assert
        Assert.Equal(["Docs/Device|", "Docs/Pump.json|"], GetMatches(result));
    }

    [Fact]
    public async Task WhenARoleHidesABranchOrTheMetadataOfASubject_ThenSearchMetadataLeavesThemOut()
    {
        // Arrange
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "tags": ["floor-2"], "$properties": { "FileSize": { "tags": ["floor-2"] } } }""");
        WriteFile("Docs/Fan.json", """{ "speed": 2 }""");
        WriteFile("Docs/Fan.metadata.json", """{ "tags": ["floor-2"], "$properties": { "FileSize": { "tags": ["floor-2"] } } }""");
        WriteFile("Secret/Valve.json", """{ "speed": 3 }""");
        WriteFile("Secret/Valve.metadata.json", """{ "tags": ["floor-2"] }""");
        WriteFile(".metadata.json", """{ "tags": ["floor-2"] }""");
        var role = new AuthorizationRole
        {
            Name = "Restricted",
            Includes = [BuiltInRoles.ReaderName],
            Deny =
            [
                new PermissionRule { Paths = ["/Storage/Secret/**"], Actions = [PermissionAction.Read] },
                new PermissionRule { Paths = ["/Storage/Docs/Fan.json/*"], Tags = [MemberTags.Metadata], Actions = [PermissionAction.Read] }
            ]
        };
        var (tools, _) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(role.Name, role));

        // Act
        var result = await CallAsync(tools["invoke_query"], new { path = "/Storage", method = "SearchMetadata", parameters = new { tag = "floor-2" } });

        // Assert
        Assert.Equal(["|", "Docs/Pump.json|", "Docs/Pump.json|FileSize"], GetMatches(result));
    }

    [Fact]
    public async Task WhenARoleHidesTheMetadataOfAFolder_ThenSearchMetadataLeavesItOut()
    {
        // Arrange
        WriteFile("Docs/.metadata.json", """{ "tags": ["floor-2"] }""");
        WriteFile("Docs/Pump.json", """{ "speed": 1 }""");
        WriteFile("Docs/Pump.metadata.json", """{ "tags": ["floor-2"] }""");

        // The metadata path of the folder resolves to a key of its children once its metadata is hidden.
        var role = new AuthorizationRole
        {
            Name = "NoFolderMetadata",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Storage/Docs/*"], Tags = [MemberTags.Metadata], Actions = [PermissionAction.Read] }]
        };
        var (tools, _) = await CreateToolsAsync(accessPolicy: TestAccessPolicies.Create(role.Name, role));

        // Act
        var result = await CallAsync(tools["invoke_query"], new { path = "/Storage", method = "SearchMetadata", parameters = new { tag = "floor-2" } });

        // Assert
        Assert.Equal(["Docs/Pump.json|"], GetMatches(result));
    }

    [Fact]
    public async Task WhenMoreThanAHundredSubjectsMatch_ThenTheFirstHundredAreReturned()
    {
        // Arrange
        for (var index = 0; index < 101; index++)
        {
            WriteFile($"Docs/Pump{index:000}.json", """{ "speed": 1 }""");
            WriteFile($"Docs/Pump{index:000}.metadata.json", """{ "tags": ["floor-2"] }""");
        }

        var (tools, _) = await CreateToolsAsync();

        // Act
        var result = await CallAsync(tools["invoke_query"], new { path = "/Storage", method = "SearchMetadata", parameters = new { tag = "floor-2" } });

        // Assert
        var matches = GetMatches(result);
        Assert.Equal(100, matches.Length);
        Assert.Equal("Docs/Pump000.json|", matches[0]);
        Assert.Equal("Docs/Pump099.json|", matches[^1]);
    }

    [Fact]
    public async Task WhenSearchMetadataReturnsAResultOfAnotherShape_ThenNoMatchIsReturned()
    {
        // Arrange
        var (_, storage) = await CreateToolsAsync();
        var subject = storage.TryGetRegisteredSubject()!;
        var method = subject.TryGetProperty(nameof(FluentStorageContainer.SearchMetadata))!.TryGetMethodMetadata()!;

        // Act
        var pathProvider = new StateAttributePathProvider();
        var result = AgentMetadataAccess.FilterSearchMetadataResult(
            subject, method, new[] { "Docs/Hidden.json" }, new McpServerConfiguration { PathProvider = pathProvider }, pathProvider);

        // Assert
        Assert.Empty(Assert.IsType<MetadataSearchMatch[]>(result));
    }

    private static string[] GetMatches(JsonElement result)
        => [.. result.GetProperty("result").EnumerateArray().Select(match =>
            $"{match.GetProperty("path").GetString()}|{(match.TryGetProperty("property", out var property) ? property.GetString() : null)}")];

    private async Task<(Dictionary<string, McpToolInfo> Tools, FluentStorageContainer Storage)> CreateToolsAsync(
        bool isAgent = false, IMcpAccessPolicy? accessPolicy = null)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(MetadataSearchTestDevice).Assembly);
        typeProvider.AddAssembly(typeof(ClaudeCliAgent).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        AgentTestRoot? root = null;
        var pathResolver = new SubjectPathResolver(() => root);
        context.AddService<ISubjectPathResolver>(pathResolver);
        services.AddSingleton(pathResolver);
        services.AddSingleton<MarkdownContentParser>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(AgentTestContext.CreateServices(new AgentTestRoot()));
        var serviceProvider = services.BuildServiceProvider();

        var storage = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
        _storages.Add(storage);

        root = new AgentTestRoot(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage } };
        await storage.ConnectAsync(CancellationToken.None);
        var configuration = HomeBlazeMcpConfigurationFactory.Create(
            serviceProvider, () => root, isReadOnly: false, accessPolicy ?? (isAgent ? TestAccessPolicies.Operator : TestAccessPolicies.Everything));
        return (new McpToolFactory(root, configuration).CreateTools().ToDictionary(tool => tool.Name), storage);
    }

    private static async Task<JsonElement> CallAsync(McpToolInfo tool, object input) =>
        JsonSerializer.SerializeToElement(await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None));

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _directory.Delete(recursive: true);
    }
}

[InterceptorSubject]
public partial class MetadataSearchTestDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
