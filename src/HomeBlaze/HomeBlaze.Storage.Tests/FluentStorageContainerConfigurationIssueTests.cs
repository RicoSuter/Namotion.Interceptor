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
}
