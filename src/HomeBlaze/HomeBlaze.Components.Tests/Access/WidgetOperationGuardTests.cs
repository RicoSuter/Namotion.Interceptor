using System.Text.RegularExpressions;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// Scans the sources of the adapter libraries, the Blazor libraries, the components, the host, the application and the
/// samples: a component runs an operation or query through <c>UserPermissionsExtensions</c> or <c>OperationInvoker</c>, which
/// check the viewer, never by calling or binding the method itself. Edit and setup components are left out, as only the configuration dialog hosts them. The scan goes by
/// method name, so a call of a method of the same name on something other than a subject is listed in
/// <see cref="AllowedCalls"/> by the path of its file below the source directory and the method name.
/// </summary>
public partial class WidgetOperationGuardTests
{
    private static readonly (string RelativePath, string MethodName)[] AllowedCalls =
    [
        // The source editor component, not a subject.
        ("HomeBlaze.Storage.Blazor/Files/MarkdownTextDialog.razor", "CreateAsync"),

        // The memory editor of an agent and the structure editor of a page, not subjects, which check the viewer themselves.
        ("HomeBlaze.AI.Blazor/AgentMemoryTab.razor", "DeleteAsync"),
        ("HomeBlaze.Components/SubjectComponent.razor", "DeleteAsync"),

        // The metadata store, not an operation, which the component checks with CanWrite on the metadata member as agents are.
        ("HomeBlaze.Host/Components/Browser/SubjectPropertyPanel.razor", "SetOrderAsync"),
        ("HomeBlaze.Host/Components/Dialogs/MetadataEditDialog.razor", "SetMetadataAsync"),
    ];

    // Edit components that people who are no full administrators open too, so they are scanned like widgets.
    private static readonly HashSet<string> ScannedEditComponents = new(StringComparer.Ordinal)
    {
        "RoleEditComponent.razor"
    };

    [Fact]
    public void WhenTheMethodsAreCollected_ThenTheOperationsOfTheDevicesAreFound()
    {
        // Act
        var methodNames = CollectMethodNames(FindSourceDirectory());

        // Assert
        Assert.Contains("TogglePlaybackAsync", methodNames);
        Assert.Contains("SetNightModeAsync", methodNames);
        Assert.Contains("TurnOnAsync", methodNames);
        Assert.Contains("TurnOffAsync", methodNames);
    }

    [Fact]
    public void WhenTheSourcesAreEnumerated_ThenTheRoleEditorIsScanned()
    {
        // Act
        var fileNames = EnumerateScannedSources(FindSourceDirectory()).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        // Assert
        Assert.Superset(ScannedEditComponents, fileNames!);
    }

    [Fact]
    public void WhenAnAttributeSpansLinesAndHoldsBrackets_ThenTheMethodAfterItIsCollected()
    {
        // Arrange
        const string source = """
            [Operation(
                Title = "Set [mode]",
                Description = "Sets the mode, see ]this[.")]
            [Category("Modes")]
            public Task SetModeAsync(int mode, CancellationToken cancellationToken) => Task.CompletedTask;
            """;

        // Act
        var methodNames = CollectMethodNames(source, []);

        // Assert
        Assert.Equal(["SetModeAsync"], methodNames);
    }

    [Theory]
    [InlineData("[Category(\"A\"), Query]\npublic string GetStatus() => \"\";", "GetStatus")]
    [InlineData("[method: OperationAttribute(Title = \"x\")]\npublic void Stop() { }", "Stop")]
    [InlineData("[HomeBlaze.Abstractions.Attributes.Operation]\nTask TurnOnAsync(CancellationToken cancellationToken);", "TurnOnAsync")]
    [InlineData("[Query]\npublic Task<T> ReadAsync<T>(CancellationToken cancellationToken) => default!;", "ReadAsync")]
    [InlineData("[Operation(Title = @\"say \"\"]\"\" \")]\npublic void Say() { }", "Say")]
    [InlineData("[Operation(Title = \"\"\"raw ] \"\"\")]\npublic void Raw() { }", "Raw")]
    public void WhenAnOperationOrQueryAttributeIsWrittenInAnotherForm_ThenTheMethodIsCollected(string source, string expected)
    {
        // Act
        var methodNames = CollectMethodNames(source, []);

        // Assert
        Assert.Equal([expected], methodNames);
    }

    [Theory]
    [InlineData("[State]\npublic partial int Level { get; set; }\npublic void Run() { }")]
    [InlineData("[Description(\"[Operation]\")]\npublic void Describe() { }")]
    [InlineData("/// [Operation]\npublic void Documented() { }")]
    [InlineData("var x = list[Operation];\npublic void Indexed() { }")]
    public void WhenNoOperationOrQueryAttributeIsPresent_ThenNothingIsCollected(string source)
    {
        // Act
        var methodNames = CollectMethodNames(source, []);

        // Assert
        Assert.Empty(methodNames);
    }

    [Theory]
    [InlineData("await device.StopAsync(CancellationToken.None);", true)]
    [InlineData("<MudButton OnClick=\"@device.StopAsync\" />", true)]
    [InlineData("Func<CancellationToken, Task> stop = device.StopAsync;", true)]
    [InlineData("Permissions.CanInvoke(device, nameof(Device.StopAsync))", false)]
    [InlineData("// device.StopAsync()", false)]
    [InlineData("/// <see cref=\"Device.StopAsync\"/>", false)]
    [InlineData("device.StopAsync_IsEnabled", false)]
    public void WhenALineIsScanned_ThenACallOrBindingOfTheMethodIsFound(string line, bool expected)
    {
        // Act
        var isFound = FindMethodReferences(line, new HashSet<string>(StringComparer.Ordinal) { "StopAsync" }).Any();

        // Assert
        Assert.Equal(expected, isFound);
    }

    [Fact]
    public void WhenTheComponentsAndTheHostAreScanned_ThenNoneCallsOrBindsAnOperationOrQueryDirectly()
    {
        // Arrange
        var sourceDirectory = FindSourceDirectory();
        var methodNames = CollectMethodNames(sourceDirectory);

        // Act
        var directCalls = new List<string>();
        foreach (var file in EnumerateScannedSources(sourceDirectory))
        {
            var relativePath = GetRelativePath(sourceDirectory, file);
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (FindMethodReferences(lines[index], methodNames).Any(methodName => !IsAllowed(relativePath, methodName)))
                {
                    directCalls.Add($"{relativePath}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        // Assert
        Assert.True(directCalls.Count == 0, "Direct operation calls:" + Environment.NewLine + string.Join(Environment.NewLine, directCalls));
    }

    [Fact]
    public void WhenTheSourcesAreScanned_ThenEveryAllowedCallStillOccurs()
    {
        // Arrange
        var sourceDirectory = FindSourceDirectory();
        var methodNames = AllowedCalls.Select(allowed => allowed.MethodName).ToHashSet(StringComparer.Ordinal);
        var references = EnumerateScannedSources(sourceDirectory)
            .SelectMany(file => File.ReadLines(file).SelectMany(line => FindMethodReferences(line, methodNames))
                .Select(methodName => (RelativePath: GetRelativePath(sourceDirectory, file), MethodName: methodName)))
            .ToHashSet();

        // Act
        var staleCalls = AllowedCalls
            .Where(allowed => !references.Contains(allowed))
            .Select(allowed => $"{allowed.RelativePath}: {allowed.MethodName}")
            .ToList();

        // Assert
        Assert.True(staleCalls.Count == 0, "Allowed calls that no longer occur:" + Environment.NewLine + string.Join(Environment.NewLine, staleCalls));
    }

    [Theory]
    [InlineData("HomeBlaze.Host/Components/Browser/SubjectPropertyPanel.razor", "SetOrderAsync", true)]
    [InlineData("HomeBlaze.Host/Components/Browser/SubjectPropertyPanel.razor", "SetMetadataAsync", false)]
    [InlineData("HomeBlaze.Host/Components/Other/SubjectPropertyPanel.razor", "SetOrderAsync", false)]
    public void WhenACallIsChecked_ThenItIsAllowedOnlyByItsPathAndMethodName(string relativePath, string methodName, bool expected)
    {
        // Act
        var isAllowed = IsAllowed(relativePath, methodName);

        // Assert
        Assert.Equal(expected, isAllowed);
    }

    private static bool IsAllowed(string relativePath, string methodName) =>
        AllowedCalls.Contains((relativePath, methodName));

    // With forward slashes on every platform, as the allowed calls are written.
    private static string GetRelativePath(string sourceDirectory, string file) =>
        Path.GetRelativePath(sourceDirectory, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "HomeBlaze.Components.Abstractions")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The HomeBlaze source directory was not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> CollectMethodNames(string sourceDirectory)
    {
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateFiles(sourceDirectory, "*.cs"))
        {
            CollectMethodNames(File.ReadAllText(file), methodNames);
        }

        return methodNames;
    }

    /// <summary>
    /// Adds the name of every method declared after an [Operation] or [Query] attribute list that starts a line, which may
    /// span lines and be followed by further attribute lists.
    /// </summary>
    private static HashSet<string> CollectMethodNames(string source, HashSet<string> methodNames)
    {
        var index = 0;
        while ((index = source.IndexOf('[', index)) >= 0)
        {
            if (!StartsLine(source, index))
            {
                index++;
                continue;
            }

            var end = FindClosingBracket(source, index);
            if (end < 0)
            {
                break;
            }

            if (IsMethodAttributeList(source[(index + 1)..end]))
            {
                var signatureStart = SkipWhitespace(source, end + 1);
                while (signatureStart < source.Length && source[signatureStart] == '[')
                {
                    var attributeEnd = FindClosingBracket(source, signatureStart);
                    if (attributeEnd < 0)
                    {
                        return methodNames;
                    }

                    signatureStart = SkipWhitespace(source, attributeEnd + 1);
                }

                var parenthesis = source.IndexOf('(', signatureStart);
                if (parenthesis >= 0 && SignatureNamePattern().Match(source[signatureStart..parenthesis]) is { Success: true } match)
                {
                    methodNames.Add(match.Groups["name"].Value);
                }
            }

            index = end + 1;
        }

        return methodNames;
    }

    private static bool StartsLine(string source, int index)
    {
        for (var position = index - 1; position >= 0 && source[position] != '\n'; position--)
        {
            if (!char.IsWhiteSpace(source[position]))
            {
                return false;
            }
        }

        return true;
    }

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>Gets the index of the bracket that closes the one at <paramref name="start"/>, skipping strings and characters.</summary>
    private static int FindClosingBracket(string source, int start)
    {
        var depth = 0;
        for (var index = start; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
                case '"':
                case '\'':
                    index = SkipLiteral(source, index);
                    break;
            }
        }

        return -1;
    }

    /// <summary>Gets the index of the last character of the string or character literal that starts at <paramref name="start"/>.</summary>
    private static int SkipLiteral(string source, int start)
    {
        var quote = source[start];
        if (quote == '"' && string.CompareOrdinal(source, start, "\"\"\"", 0, 3) == 0)
        {
            var end = source.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + 2;
        }

        var isVerbatim = quote == '"' && start > 0 && source[start - 1] == '@';
        for (var index = start + 1; index < source.Length; index++)
        {
            if (!isVerbatim && source[index] == '\\')
            {
                index++;
            }
            else if (source[index] == quote)
            {
                if (isVerbatim && index + 1 < source.Length && source[index + 1] == quote)
                {
                    index++;
                }
                else
                {
                    return index;
                }
            }
        }

        return source.Length;
    }

    /// <summary>Gets whether one of the attributes of the list, separated by commas outside parentheses, is [Operation] or [Query].</summary>
    private static bool IsMethodAttributeList(string attributes)
    {
        var depth = 0;
        var attributeStart = 0;
        for (var index = 0; index <= attributes.Length; index++)
        {
            var character = index < attributes.Length ? attributes[index] : ',';
            if (character is '"' or '\'')
            {
                index = SkipLiteral(attributes, index);
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                if (AttributeNamePattern().Match(attributes[attributeStart..index]) is { Success: true } match &&
                    match.Groups["name"].Value is "Operation" or "Query")
                {
                    return true;
                }

                attributeStart = index + 1;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateScannedSources(string sourceDirectory)
    {
        foreach (var project in Directory.EnumerateDirectories(sourceDirectory))
        {
            var name = Path.GetFileName(project);
            var isScanned = name.EndsWith(".HomeBlaze", StringComparison.Ordinal) ||
                            name is "HomeBlaze" or "HomeBlaze.Host" or "HomeBlaze.Components" or "HomeBlaze.Samples" ||
                            (name.StartsWith("HomeBlaze.", StringComparison.Ordinal) && name.EndsWith(".Blazor", StringComparison.Ordinal));
            if (!isScanned)
            {
                continue;
            }

            foreach (var file in EnumerateFiles(project, "*.razor").Concat(EnumerateFiles(project, "*.cs")))
            {
                var fileName = Path.GetFileName(file);
                if (ScannedEditComponents.Contains(fileName) ||
                    (!fileName.Contains("EditComponent", StringComparison.Ordinal) && !fileName.Contains("SetupComponent", StringComparison.Ordinal)))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($".Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Finds the calls and method group bindings of the methods on a line, leaving out comments and nameof.</summary>
    private static IEnumerable<string> FindMethodReferences(string line, IReadOnlySet<string> methodNames)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("@*", StringComparison.Ordinal))
        {
            return [];
        }

        return ReferencePattern().Matches(NameofPattern().Replace(line, string.Empty))
            .Select(match => match.Groups["name"].Value)
            .Where(methodNames.Contains);
    }

    // The attribute name without target, namespace and Attribute suffix, such as "Operation" in "method: X.OperationAttribute(".
    [GeneratedRegex(@"^\s*(?:\w+\s*:)?\s*(?:\w+\.)*(?<name>\w+?)(?:Attribute)?\s*(?:\(|$)", RegexOptions.Singleline)]
    private static partial Regex AttributeNamePattern();

    // The last name before the parameter list, after an optional type parameter list.
    [GeneratedRegex(@"\b(?<name>\w+)\s*(?:<[^<>]*>)?\s*$")]
    private static partial Regex SignatureNamePattern();

    [GeneratedRegex(@"\.(?<name>\w+)\b")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"\bnameof\s*\([^()]*\)")]
    private static partial Regex NameofPattern();
}
