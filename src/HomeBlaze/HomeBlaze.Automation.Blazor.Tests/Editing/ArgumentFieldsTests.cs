using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class ArgumentFieldsTests
{
    [Fact]
    public void WhenAnOperationHasSimpleAndRecordParameters_ThenARecordHasAFieldPerMember()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            Parameters =
            [
                new MethodParameter { Name = "position", Type = typeof(int) },
                new MethodParameter { Name = "notification", Type = typeof(Notification) },
                new MethodParameter { Name = "label", Type = typeof(string), IsNullable = true },
                new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true }
            ]
        };

        // Act
        var fields = ArgumentFields.Create(method);

        // Assert
        Assert.Equal(
            ["position", "notification.Message", "notification.Title", "notification.Priority", "notification.Url", "notification.UrlTitle", "label"],
            fields.Select(field => field.Key).ToArray());
        Assert.True(fields[0].IsRequired);
        Assert.False(fields[0].IsTemplate);
        Assert.Equal("Message", fields[1].Label);
        Assert.True(fields[1].IsTemplate);
        Assert.True(fields[1].IsRequired);
        Assert.Contains("Normal", fields[3].Choices!);
        Assert.False(fields[6].IsRequired);
        Assert.True(fields[6].IsTemplate);
    }

    private sealed record Unsupported
    {
        public string? Text { get; init; }

        public List<int> Numbers { get; init; } = [];
    }

    private sealed record Options
    {
        public bool Enabled { get; init; }

        public DayOfWeek Day { get; init; }
    }

    [Fact]
    public void WhenARecordIsNullableOrHasUnsupportedMembers_ThenItIsOneWholeValueField()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            Parameters =
            [
                new MethodParameter { Name = "optional", Type = typeof(Notification), IsNullable = true },
                new MethodParameter { Name = "unsupported", Type = typeof(Unsupported) }
            ]
        };

        // Act
        var fields = ArgumentFields.Create(method);

        // Assert
        Assert.Equal(["optional", "unsupported"], fields.Select(field => field.Key).ToArray());
    }

    [Fact]
    public void WhenARecordHasBooleanAndEnumMembers_ThenTheyHaveChoices()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            Parameters = [new MethodParameter { Name = "options", Type = typeof(Options) }]
        };

        // Act
        var fields = ArgumentFields.Create(method);

        // Assert
        Assert.Equal(["true", "false"], fields.Single(field => field.Key == "options.Enabled").Choices);
        Assert.Contains("Monday", fields.Single(field => field.Key == "options.Day").Choices!);
    }

    [Fact]
    public void WhenAParameterIsRuntimeProvided_ThenItHasNoField()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            Parameters = [new MethodParameter { Name = "cancellationToken", Type = typeof(CancellationToken), IsRuntimeProvided = true }]
        };

        // Act
        var fields = ArgumentFields.Create(method);

        // Assert
        Assert.Empty(fields);
    }

    [Fact]
    public void WhenArgumentsAreKeptForAnotherOperation_ThenOnlyTheKeysOfItsFieldsRemain()
    {
        // Arrange
        var method = new MethodMetadata(_ => null)
        {
            Parameters =
            [
                new MethodParameter { Name = "position", Type = typeof(int) },
                new MethodParameter { Name = "notification", Type = typeof(Notification) }
            ]
        };
        var fields = ArgumentFields.Create(method);
        var arguments = new Dictionary<string, string>
        {
            ["Position"] = "5",
            ["notification.Message"] = "Hi",
            ["notification.Gone"] = "x",
            ["speed"] = "3"
        };

        // Act
        var kept = ArgumentFields.KeepKnown(fields, arguments);

        // Assert
        Assert.Equal(["Position", "notification.Message"], kept.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(ArgumentFields.KeepKnown([], arguments));
    }
}
