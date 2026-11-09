using System.Text;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
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
        var exception = await Assert.ThrowsAsync<ConfigurationWriteRefusedException>(() => storage.WriteConfigurationAsync(motor, CancellationToken.None));

        // Assert
        Assert.Contains("the value of 'targetSpeed' could not be read", exception.Message);
        Assert.Contains("Correct them in the file; the storage reads it again once it changes", exception.Message);
        Assert.Single(exception.Issues);
        Assert.Equal(content, await File.ReadAllTextAsync(GetFullPath("Motor.json")));
    }

    [Fact]
    public async Task WhenAValueOfAFileCannotBeRead_ThenTheCheckOfASubjectNestedInItsConfigurationThrowsTheRefusal()
    {
        // Arrange
        var nestedType = typeof(ConfigurationWriterTestSubject).FullName;
        WriteFile("Layout.json", $$"""{ "$type": "{{nestedType}}", "name": 5, "items": [ { "$type": "{{nestedType}}", "name": "nested" } ] }""");
        var storage = await ConnectAsync();
        var file = Assert.IsType<ConfigurationWriterTestSubject>(storage.Children["Layout"]);
        var nested = Assert.Single(file.Items);

        // Act
        var exception = Assert.Throws<ConfigurationWriteRefusedException>(() => ((IConfigurationWriter)storage).ThrowIfWriteRefused(nested));

        // Assert
        Assert.Contains("'Layout.json'", exception.Message);
        Assert.Contains("the value of 'name' could not be read", exception.Message);
    }

    [Fact]
    public async Task WhenTheValueIsCorrectedInTheFile_ThenTheCheckNoLongerThrows()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var writer = (IConfigurationWriter)storage;
        var refusalBefore = Record.Exception(() => writer.ThrowIfWriteRefused(motor));
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": 1200 }""");

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        Assert.IsType<ConfigurationWriteRefusedException>(refusalBefore);
        Assert.Null(Record.Exception(() => writer.ThrowIfWriteRefused(motor)));
        Assert.False(storage.Connection.Reconciler.HasConfigurationIssues);
    }

    [Fact]
    public async Task WhenTheCorrectedFileIsWrittenThroughTheStorage_ThenTheSubjectIsSavedAgain()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);

        // Act
        await storage.WriteBlobAsync(
            "Motor.json", Encoding.UTF8.GetBytes($$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": 1200 }"""), CancellationToken.None);
        motor.Name = "Changed";
        var isWritten = await storage.WriteConfigurationAsync(motor, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"name\": \"Changed\"", await File.ReadAllTextAsync(GetFullPath("Motor.json")));
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
        await Assert.ThrowsAsync<ConfigurationWriteRefusedException>(() => storage.WriteConfigurationAsync(motor, CancellationToken.None));
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
        await Assert.ThrowsAsync<ConfigurationWriteRefusedException>(save);
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

    [Fact]
    public async Task WhenTheFileOfASubjectWithIssuesIsDeleted_ThenTheCheckNoLongerRefusesTheSubject()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        File.Delete(GetFullPath("Motor.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert: like the write, which finds no file for the subject.
        Assert.Null(Record.Exception(() => ((IConfigurationWriter)storage).ThrowIfWriteRefused(motor)));
        Assert.False(await storage.WriteConfigurationAsync(motor, CancellationToken.None));
        Assert.False(storage.Connection.Reconciler.HasConfigurationIssues);
    }

    [Fact]
    public async Task WhenASubjectWithIssuesIsDeletedThroughTheStorage_ThenTheStorageHasNoConfigurationIssues()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var hadIssues = storage.Connection.Reconciler.HasConfigurationIssues;

        // Act
        await storage.DeleteSubjectAsync(motor, CancellationToken.None);

        // Assert
        Assert.True(hadIssues);
        Assert.False(File.Exists(GetFullPath("Motor.json")));
        Assert.False(storage.Connection.Reconciler.HasConfigurationIssues);
    }

    [Fact]
    public async Task WhenOneOfTwoFilesWithIssuesIsDeleted_ThenTheSubjectOfTheOtherIsStillRefused()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        WriteFile("Pump.json", $$"""{ "$type": "{{MotorType}}", "name": "Pump", "targetSpeed": "slow" }""");
        var storage = await ConnectAsync();
        var pump = Assert.IsType<Samples.Motor>(storage.Children["Pump"]);
        File.Delete(GetFullPath("Motor.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        var check = Assert.Throws<ConfigurationWriteRefusedException>(() => ((IConfigurationWriter)storage).ThrowIfWriteRefused(pump));
        Assert.Contains("Pump.json", check.Message);
    }

    [Fact]
    public async Task WhenTheFileOfASubjectWithIssuesIsMovedToAnotherFolder_ThenTheSubjectIsStillRefusedUnderTheNewPath()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        Directory.CreateDirectory(GetFullPath("Devices"));
        File.Move(GetFullPath("Motor.json"), GetFullPath("Devices/Engine.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        Assert.Same(motor, devices.Children["Engine"]);
        var check = Assert.Throws<ConfigurationWriteRefusedException>(() => ((IConfigurationWriter)storage).ThrowIfWriteRefused(motor));
        Assert.Contains("Engine.json", check.Message);
        await Assert.ThrowsAsync<ConfigurationWriteRefusedException>(() => storage.WriteConfigurationAsync(motor, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheNewFileOfAMovedSubjectWithIssuesIsCorrected_ThenTheStorageHasNoConfigurationIssues()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        Directory.CreateDirectory(GetFullPath("Devices"));
        File.Move(GetFullPath("Motor.json"), GetFullPath("Devices/Engine.json"));
        await storage.ReconcileAsync();
        var devices = Assert.IsType<VirtualFolder>(storage.Children["Devices"]);
        var isMoved = ReferenceEquals(motor, devices.Children["Engine"]);
        WriteFile("Devices/Engine.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": 1200 }""");

        // Act
        await storage.ReconcileAsync(Named("Devices/Engine.json"));

        // Assert
        Assert.True(isMoved);
        Assert.False(storage.Connection.Reconciler.HasConfigurationIssues);
        Assert.True(await storage.WriteConfigurationAsync(motor, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheFileOfASubjectWithIssuesIsReplacedByASubjectOfAnotherType_ThenNeitherSubjectIsRefused()
    {
        // Arrange
        WriteFile("Motor.json", $$"""{ "$type": "{{MotorType}}", "name": "Belt", "targetSpeed": "fast" }""");
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        WriteFile("Motor.json", $$"""{ "$type": "{{typeof(ConfigurationWriterTestSubject).FullName}}", "name": "replacement" }""");

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        var replacement = Assert.IsType<ConfigurationWriterTestSubject>(storage.Children["Motor"]);
        var writer = (IConfigurationWriter)storage;
        Assert.Null(Record.Exception(() => writer.ThrowIfWriteRefused(replacement)));
        Assert.Null(Record.Exception(() => writer.ThrowIfWriteRefused(motor)));
        Assert.True(await storage.WriteConfigurationAsync(replacement, CancellationToken.None));
        Assert.False(storage.Connection.Reconciler.HasConfigurationIssues);
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
