using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerConfigurationIssueTests : StorageTestBase
{
    private static readonly string MotorType = typeof(Samples.Motor).FullName!;

    [Fact]
    public async Task WhenAValueOfASubjectFileCannotBeRead_ThenSavingTheSubjectWritesTheConfigurationAsLoaded()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        motor.Name = "Changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(motor, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        var saved = await File.ReadAllTextAsync(GetFullPath("Motor.json"));
        Assert.Contains("\"name\": \"Changed\"", saved);
        Assert.DoesNotContain("fast", saved);
    }

    [Fact]
    public async Task WhenAFileHasAKeyThatNamesNoSetting_ThenItIsIgnoredAndTheSubjectIsSaved()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "enabeld": false }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);

        // Act
        var isWritten = await storage.WriteConfigurationAsync(motor, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
    }

    [Fact]
    public async Task WhenASubjectThatReceivesIssuesHasAKeyThatNamesNoSetting_ThenItGetsTheIssue()
    {
        // Arrange
        WriteFile("Issues.json", $$"""{ "$type": "{{typeof(IssueReceivingSubject).FullName}}", "name": "a", "nmae": "b" }""");

        // Act
        var storage = await ConnectAsync();

        // Assert
        var subject = Assert.IsType<IssueReceivingSubject>(storage.Children["Issues"]);
        Assert.Equal(["'nmae' is no setting of IssueReceivingSubject"], subject.Issues);
    }

    [Fact]
    public async Task WhenASubjectThatReceivesIssuesIsReadInFull_ThenItGetsNoIssues()
    {
        // Arrange
        WriteFile("Issues.json", $$"""{ "$type": "{{typeof(IssueReceivingSubject).FullName}}", "name": "a", "nmae": "b" }""");
        var storage = await ConnectAsync();
        var subject = Assert.IsType<IssueReceivingSubject>(storage.Children["Issues"]);
        WriteFile("Issues.json", $$"""{ "$type": "{{typeof(IssueReceivingSubject).FullName}}", "name": "b" }""");

        // Act
        await storage.ReconcileAsync(Named("Issues.json"));

        // Assert
        Assert.Same(subject, storage.Children["Issues"]);
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
