using Namotion.Interceptor.Tracking.Change.Performance;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class InlineValueStorageTests
{
    [Fact]
    public void WhenBoxingStoredValue_ThenEveryInlineTypeBoxesAsItself()
    {
        // Act & Assert
        AssertBox(42);
        AssertBox(42L);
        AssertBox(4.2);
        AssertBox(4.2f);
        AssertBox(true);
        AssertBox((byte)42);
        AssertBox((sbyte)-42);
        AssertBox((short)-42);
        AssertBox((ushort)42);
        AssertBox(42u);
        AssertBox(42ul);
        AssertBox('x');
        AssertBox((nint)42);
        AssertBox(4.2m);
        AssertBox(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        AssertBox(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)));
        AssertBox(TimeSpan.FromSeconds(42));
        AssertBox(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        AssertBox(DayOfWeek.Friday);
        AssertBox(new SmallStruct(4, 2));
        AssertBox<int?>(42);
        AssertBox<int?>(null);
    }

    [Fact]
    public void WhenNothingIsStored_ThenBoxedValueIsNull()
    {
        // Arrange
        var storage = default(InlineValueStorage);

        // Act
        var boxed = storage.GetValueBoxed();

        // Assert
        Assert.Null(boxed);
        Assert.Null(storage.StoredType);
    }

    [Fact]
    public void WhenReadingWithAnotherType_ThenNoValueIsReturned()
    {
        // Arrange
        var storage = InlineValueStorage.Create(DayOfWeek.Friday);

        // Act
        var readAsUnderlyingType = storage.TryGetValue<int>(out _);
        var readAsStoredType = storage.TryGetValue<DayOfWeek>(out var value);
        var readAsObject = storage.TryGetValue<object>(out _);

        // Assert
        Assert.False(readAsUnderlyingType);
        Assert.True(readAsStoredType);
        Assert.Equal(DayOfWeek.Friday, value);
        Assert.False(readAsObject);
    }

    private static void AssertBox<TValue>(TValue value)
    {
        var storage = InlineValueStorage.Create(value);
        var boxed = storage.GetValueBoxed();

        Assert.Equal(value, boxed);
        Assert.Equal(value?.GetType(), boxed?.GetType());
        Assert.Equal(typeof(TValue), storage.StoredType);
    }

    private readonly record struct SmallStruct(int First, int Second);
}
