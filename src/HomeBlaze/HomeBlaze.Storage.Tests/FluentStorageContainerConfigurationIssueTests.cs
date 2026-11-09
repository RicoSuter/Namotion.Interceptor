using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerConfigurationIssueTests : StorageTestBase
{
    private static readonly string MotorType = typeof(Samples.Motor).FullName!;

    [Fact]
    public async Task WhenAValueOfASubjectFileCannotBeRead_ThenSavingTheSubjectIsRefusedAndTheFileIsKept()
    {
        // Arrange
        var content = $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""";
        WriteFile("Motor.json", content);
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        motor.Name = "Changed";

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WriteConfigurationAsync(motor, CancellationToken.None));

        // Assert
        Assert.Contains("the value of 'targetSpeed' could not be read", exception.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(GetFullPath("Motor.json")));
    }

    [Fact]
    public async Task WhenTheValueIsCorrectedInTheFile_ThenTheSubjectIsSavedAgain()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": 1200 }""");
        await storage.ReconcileAsync(Named("Motor.json"));
        motor.Name = "Changed";

        // Act
        var isWritten = await storage.WriteConfigurationAsync(motor, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        var saved = await File.ReadAllTextAsync(GetFullPath("Motor.json"));
        Assert.Contains("\"name\": \"Changed\"", saved);
        Assert.Contains("\"targetSpeed\": 1200", saved);
    }

    [Fact]
    public async Task WhenAValueOfASubjectFileCannotBeReadAfterAChange_ThenSavingTheSubjectIsRefused()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": 1200 }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        await storage.ReconcileAsync(Named("Motor.json"));

        // Act & Assert
        Assert.Same(motor, storage.Children["Motor"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.WriteConfigurationAsync(motor, CancellationToken.None));
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
    public async Task WhenASubjectThatReceivesIssuesHasAKeyThatNamesNoSetting_ThenItGetsTheIssueAndIsNotSaved()
    {
        // Arrange
        WriteFile("Issues.json", $$"""{ "$type": "{{typeof(IssueReceivingSubject).FullName}}", "name": "a", "nmae": "b" }""");
        var storage = await ConnectAsync();
        var subject = Assert.IsType<IssueReceivingSubject>(storage.Children["Issues"]);

        // Act
        var save = () => storage.WriteConfigurationAsync(subject, CancellationToken.None);

        // Assert
        Assert.Equal(["'nmae' is no setting of IssueReceivingSubject"], subject.Issues);
        await Assert.ThrowsAsync<InvalidOperationException>(save);
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
        Assert.True(await storage.WriteConfigurationAsync(subject, CancellationToken.None));
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
