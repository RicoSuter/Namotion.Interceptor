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
        Assert.Equal(["cooled", "overheat"], automation.Rules.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(automation.Rules.Values, rule => Assert.Equal(AutomationRuleMode.DryRun, rule.Mode));
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
            errors.AddRange(AutomationValidator.Validate(automation.States, automation.InitialState, automation.Rules, automation.RuleOrder)
                .Select(error => $"{automation.Name}: {error.Message}"));
        }

        // Assert
        Assert.Equal(4, examples.Length);
        Assert.Empty(errors);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SubjectPathResolver(() => null));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHomeBlazeAutomation();
        return services.BuildServiceProvider();
    }

    private static Automation Deserialize(string json, IServiceProvider provider) =>
        Assert.IsType<Automation>(new ConfigurableSubjectSerializer(new TypeProvider().AddAssembly(typeof(Automation).Assembly), provider).Deserialize(json));

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
