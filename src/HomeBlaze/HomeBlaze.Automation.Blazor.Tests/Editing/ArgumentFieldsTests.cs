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
}
