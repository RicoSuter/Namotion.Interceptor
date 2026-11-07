using HomeBlaze.Automation.Runtime;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Tests;

public class SampleAutomationTests
{
    [Fact]
    public void WhenTheSampleIsLoaded_ThenItIsAValidAutomationWhoseRulesAreDryRuns()
    {
        // Arrange
        var json = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "HomeBlaze", "HomeBlaze", "Data", "Files", "Demo", "ConveyorGuard.json"));
        var services = new ServiceCollection();
        services.AddSingleton(new SubjectPathResolver(() => null));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHomeBlazeAutomation();
        using var provider = services.BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(new TypeProvider().AddAssembly(typeof(Automation).Assembly), provider);

        // Act
        using var automation = Assert.IsType<Automation>(serializer.Deserialize(json));
        var isValid = AutomationCompiler.TryCompile(automation.States, automation.InitialState, automation.Rules, automation.RuleOrder, out _, out var error);

        // Assert
        Assert.True(isValid, error);
        Assert.Equal(["cooled", "overheat"], automation.Rules.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(automation.Rules.Values, rule => Assert.Equal(AutomationRuleMode.DryRun, rule.Mode));
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<PropertyTrigger>(automation.Rules["overheat"].Triggers[0]).For);
    }

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
