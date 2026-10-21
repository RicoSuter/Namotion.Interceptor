using Namotion.Interceptor.Tracking.Change.Performance;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class InlineValueStorageTests
{
    [Fact]
    public void WhenBoxingStoredValue_ThenEveryInlineTypeBoxesAsItself()
    {
        // Act
        BoxingResult[] results =
        [
            Box(42),
            Box(42L),
            Box(4.2),
            Box(4.2f),
            Box(true),
            Box((byte)42),
            Box((sbyte)-42),
            Box((short)-42),
            Box((ushort)42),
            Box(42u),
            Box(42ul),
            Box('x'),
            Box((nint)42),
            Box(4.2m),
            Box(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)),
            Box(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2))),
            Box(TimeSpan.FromSeconds(42)),
            Box(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            Box(DayOfWeek.Friday),
            Box(new SmallStruct(4, 2)),
            Box<int?>(42),
            Box<int?>(null)
        ];

        // Assert
        Assert.All(results, result =>
        {
            Assert.Equal(result.Value, result.Boxed);
            Assert.Equal(result.Value?.GetType(), result.Boxed?.GetType());
            Assert.Equal(result.Type, result.StoredType);
        });
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
        Assert.True(storage.IsEmpty);
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
        Assert.Equal(typeof(DayOfWeek), storage.StoredType);
    }

    private static BoxingResult Box<TValue>(TValue value)
    {
        var storage = InlineValueStorage.Create(value);
        return new BoxingResult(typeof(TValue), value, storage.GetValueBoxed(), storage.StoredType);
    }

    private sealed record BoxingResult(Type Type, object? Value, object? Boxed, Type? StoredType);

    private readonly record struct SmallStruct(int First, int Second);
}
