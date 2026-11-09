using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectSerializerIssueTests
{
    private static ConfigurableSubjectSerializer CreateSerializer()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(SubjectWithTypedProperties), typeof(ChildSubject), typeof(IssueReceivingSubject)]);

        var services = new ServiceCollection();
        services.AddSingleton(InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer));
        return new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());
    }

    [Theory]
    [InlineData(ConfigurationNaming.JsonProperty, "count")]
    [InlineData(ConfigurationNaming.CSharpProperty, "Count")]
    public void WhenAValueIsSkipped_ThenTheIssueNamesItsKey(ConfigurationNaming naming, string key)
    {
        // Arrange
        var serializer = CreateSerializer();
        var json = $$"""{ "$type": "{{typeof(SubjectWithTypedProperties).FullName}}", "{{key}}": "three", "unknown": 1 }""";
        var skipped = new List<SkippedConfigurationProperty>();
        var subject = (IInterceptorSubject)serializer.Deserialize(json, created: null, skipped.Add, naming)!;

        // Act
        var issues = serializer.ReportIssues(subject, json, skipped, naming);

        // Assert
        var issue = Assert.Single(issues);
        Assert.StartsWith($"the value of '{key}' could not be read: ", issue);
    }

    [Fact]
    public void WhenASubjectThatReceivesIssuesHasKeysThatNameNoSetting_ThenItGetsAnIssuePerKey()
    {
        // Arrange
        var serializer = CreateSerializer();
        var json = $$"""{ "$type": "{{typeof(IssueReceivingSubject).FullName}}", "name": "a", "nmae": "b", "Name": "c" }""";
        var skipped = new List<SkippedConfigurationProperty>();
        var subject = (IssueReceivingSubject)serializer.Deserialize(json, created: null, skipped.Add)!;

        // Act
        var issues = serializer.ReportIssues(subject, json, skipped);

        // Assert
        Assert.Equal(["'nmae' is no setting of IssueReceivingSubject", "'Name' is no setting of IssueReceivingSubject"], issues.ToArray());
        Assert.Equal(issues.ToArray(), subject.Issues);
    }

    [Fact]
    public void WhenASubjectThatReceivesIssuesIsReadInFull_ThenItGetsNone()
    {
        // Arrange
        var serializer = CreateSerializer();
        var json = $$"""{ "$type": "{{typeof(IssueReceivingSubject).FullName}}", "name": "a" }""";
        var subject = (IssueReceivingSubject)serializer.Deserialize(json)!;
        subject.SetConfigurationIssues(["stale"]);

        // Act
        var issues = serializer.ReportIssues(subject, json, []);

        // Assert
        Assert.Empty(issues);
        Assert.Empty(subject.Issues);
    }
}

[InterceptorSubject]
public partial class IssueReceivingSubject : IConfigurable, IConfigurationIssueReceiver
{
    public IssueReceivingSubject()
    {
        Name = string.Empty;
    }

    [Configuration]
    public partial string Name { get; set; }

    public string[] Issues { get; private set; } = [];

    public void SetConfigurationIssues(string[] issues) => Issues = issues;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
