using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

public class RegisteredSubjectPropertyGetValueTests
{
    [Fact]
    public void WhenDynamicPropertyIsReadWithTimestamp_ThenValueAndTimestampOfLastWriteAreReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var backingValue = 0;
        var property = person.TryGetRegisteredSubject()!.AddProperty(
            "Counter", typeof(int), _ => backingValue, (_, value) => backingValue = (int)value!);

        var writeTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (SubjectChangeContext.WithChangedTimestamp(writeTimestamp))
        {
            property.SetValue(5);
        }

        // Act
        var value = property.GetValue(out var metadata);

        // Assert
        Assert.Equal(5, value);
        Assert.Equal(writeTimestamp, metadata.WriteTimestamp);
    }
}
