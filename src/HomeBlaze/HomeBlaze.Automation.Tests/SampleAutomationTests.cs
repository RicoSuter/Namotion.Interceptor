using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Tests;

/// <summary>
/// Loads the automations that ship as samples, the demo file and the JSON examples of the user page, as the storage reads
/// them, so that a sample the code no longer accepts is found here and not by the reader who copies it.
/// </summary>
public partial class SampleAutomationTests
{
    [GeneratedRegex("```json\n(?<json>.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    [Fact]
    public void WhenTheSampleIsLoaded_ThenItIsAValidAutomationWhoseRulesAreDryRuns()
    {
        // Arrange
        var json = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "HomeBlaze", "HomeBlaze", "Data", "Files", "Demo", "ConveyorGuard.json"));
        using var provider = CreateServices();

        // Act
        using var automation = Deserialize(json, provider);
        var isValid = AutomationCompiler.TryCompile(automation.States, automation.InitialState, automation.Rules, automation.RuleOrder, out _, out var error);

        // Assert
        Assert.True(isValid, error);
        Assert.Equal(["Operator"], automation.Roles);
        Assert.Equal(["cooled", "overheat"], automation.Rules.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(automation.Rules.Values, rule => Assert.Equal(AutomationRuleMode.DryRun, rule.Mode));
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<PropertyTrigger>(automation.Rules["overheat"].Triggers[0]).For);
    }

    [Fact]
    public void WhenTheExamplesOfTheUserPageAreLoaded_ThenEachIsAValidAutomation()
    {
        // Arrange
        var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "HomeBlaze", "HomeBlaze", "Data", "Files", "Docs", "administration", "automation.md"))
            .ReplaceLineEndings("\n");
        var examples = JsonFenceRegex().Matches(page)
            .Select(match => match.Groups["json"].Value)
            .Where(json => json.Contains("\"HomeBlaze.Automation.Automation\"", StringComparison.Ordinal))
            .ToArray();
        using var provider = CreateServices();

        // Act
        var errors = new List<string>();
        foreach (var json in examples)
        {
            using var automation = Deserialize(json, provider);
            var written = CreateSerializer(provider).Serialize(automation);
            errors.AddRange(FindUnreadMembers(JsonDocument.Parse(json).RootElement, JsonDocument.Parse(written).RootElement, automation.Name ?? "?"));
            errors.AddRange(AutomationValidator.Validate(automation, automation.Roles, automation.States, automation.InitialState, automation.Rules, automation.RuleOrder)
                .Select(error => $"{automation.Name}: {error.Message}"));
        }

        // Assert
        Assert.Equal(4, examples.Length);
        Assert.Empty(errors);
    }

    // A member the model does not know is dropped on deserialization, so it is missing when the automation is written again.
    private static IEnumerable<string> FindUnreadMembers(JsonElement example, JsonElement written, string path)
    {
        if (example.ValueKind == JsonValueKind.Object && written.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in example.EnumerateObject())
            {
                if (!written.TryGetProperty(member.Name, out var counterpart))
                {
                    yield return $"{path}: '{member.Name}' is not read";
                    continue;
                }

                foreach (var error in FindUnreadMembers(member.Value, counterpart, $"{path}/{member.Name}"))
                {
                    yield return error;
                }
            }
        }
        else if (example.ValueKind == JsonValueKind.Array && written.ValueKind == JsonValueKind.Array)
        {
            var pairs = example.EnumerateArray().Zip(written.EnumerateArray());
            var index = 0;
            foreach (var (item, counterpart) in pairs)
            {
                foreach (var error in FindUnreadMembers(item, counterpart, $"{path}[{index++}]"))
                {
                    yield return error;
                }
            }
        }
    }

    // The samples are checked without the roles of an installation, which need its graph.
    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SubjectPathResolver(() => null));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IAutomationAuthorizer, AllowAllAutomationAuthorizer>();
        services.AddHomeBlazeAutomation();
        return services.BuildServiceProvider();
    }

    private static Automation Deserialize(string json, IServiceProvider provider) => Assert.IsType<Automation>(CreateSerializer(provider).Deserialize(json));

    private static ConfigurableSubjectSerializer CreateSerializer(IServiceProvider provider) =>
        new(new TypeProvider().AddAssembly(typeof(Automation).Assembly), provider);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Namotion.Interceptor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
