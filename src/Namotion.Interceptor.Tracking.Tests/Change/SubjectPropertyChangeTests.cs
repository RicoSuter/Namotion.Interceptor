using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class SubjectPropertyChangeTests
{
    private readonly PropertyReference _property;
    private readonly DateTimeOffset _changedTimestamp = DateTimeOffset.UtcNow;
    private readonly DateTimeOffset _receivedTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(-10);

    public SubjectPropertyChangeTests()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        _property = new PropertyReference(person, nameof(Person.FirstName));
    }

    [Theory]
    [InlineData("OldName", "NewName")]
    [InlineData("", "NewName")]
    [InlineData("Test", "")]
    public void WhenCreatedWithString_ThenStoresAndRetrievesCorrectly(string oldValue, string newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void WhenCreatedWithNullString_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        string? oldValue = null;
        const string newValue = "NewName";

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void WhenCreatedWithBothStringsNull_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        string? oldValue = null;
        string? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<string>());
        Assert.Null(change.GetNewValue<string>());
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(0, -1)]
    public void WhenCreatedWithInt_ThenStoresAndRetrievesCorrectly(int oldValue, int newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<int>());
        Assert.Equal(newValue, change.GetNewValue<int>());
    }

    [Theory]
    [InlineData(123456789012345L, 987654321098765L)]
    [InlineData(long.MinValue, long.MaxValue)]
    public void WhenCreatedWithLong_ThenStoresAndRetrievesCorrectly(long oldValue, long newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<long>());
        Assert.Equal(newValue, change.GetNewValue<long>());
    }

    [Theory]
    [InlineData(3.14159265358979, 2.71828182845904)]
    [InlineData(double.MinValue, double.MaxValue)]
    public void WhenCreatedWithDouble_ThenStoresAndRetrievesCorrectly(double oldValue, double newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<double>());
        Assert.Equal(newValue, change.GetNewValue<double>());
    }

    [Theory]
    [InlineData(3.14f, 2.71f)]
    [InlineData(float.MinValue, float.MaxValue)]
    public void WhenCreatedWithFloat_ThenStoresAndRetrievesCorrectly(float oldValue, float newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<float>());
        Assert.Equal(newValue, change.GetNewValue<float>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WhenCreatedWithBool_ThenStoresAndRetrievesCorrectly(bool oldValue, bool newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<bool>());
        Assert.Equal(newValue, change.GetNewValue<bool>());
    }

    [Theory]
    [InlineData((byte)0, (byte)255)]
    [InlineData((byte)128, (byte)64)]
    public void WhenCreatedWithByte_ThenStoresAndRetrievesCorrectly(byte oldValue, byte newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<byte>());
        Assert.Equal(newValue, change.GetNewValue<byte>());
    }

    [Theory]
    [InlineData('A', 'Z')]
    [InlineData('0', '9')]
    public void WhenCreatedWithChar_ThenStoresAndRetrievesCorrectly(char oldValue, char newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<char>());
        Assert.Equal(newValue, change.GetNewValue<char>());
    }

    public static IEnumerable<object[]> LargerValueTypeTestData()
    {
        yield return [123456789.123456789m, 987654321.987654321m];
        yield return [new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc)];
        yield return [new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5))];
        yield return [Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")];
        yield return [TimeSpan.FromHours(1.5), TimeSpan.FromDays(7)];
    }

    [Theory]
    [MemberData(nameof(LargerValueTypeTestData))]
    public void WhenCreatedWithLargerValueTypes_ThenStoresAndRetrievesCorrectly(object oldValue, object newValue)
    {
        // Arrange
        var method = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.Create))!
            .MakeGenericMethod(oldValue.GetType());

        // Act
        var change = (SubjectPropertyChange)method.Invoke(null,
            [_property, null, _changedTimestamp, _receivedTimestamp, oldValue, newValue])!;

        // Assert
        var getOldMethod = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.GetOldValue))!
            .MakeGenericMethod(oldValue.GetType());
        var getNewMethod = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.GetNewValue))!
            .MakeGenericMethod(newValue.GetType());

        Assert.Equal(oldValue, getOldMethod.Invoke(change, null));
        Assert.Equal(newValue, getNewMethod.Invoke(change, null));
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(0, int.MaxValue)]
    public void WhenCreatedWithNullableIntWithValue_ThenStoresAndRetrievesCorrectly(int oldVal, int newVal)
    {
        // Arrange
        int? oldValue = oldVal;
        int? newValue = newVal;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void WhenCreatedWithNullableIntWithNull_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int? oldValue = null;
        int? newValue = 42;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void WhenCreatedWithNullableIntBothNull_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int? oldValue = null;
        int? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int?>());
        Assert.Null(change.GetNewValue<int?>());
    }

    [Fact]
    public void WhenCreatedWithNullableDecimal_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        decimal? oldValue = 123.456m;
        decimal? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<decimal?>());
        Assert.Null(change.GetNewValue<decimal?>());
    }

    private struct SmallCustomStruct
    {
        public int Value1;
        public int Value2;
    }

    private struct LargeCustomStruct
    {
        public long Value1;
        public long Value2;
    }

    private struct OversizedCustomStruct
    {
        public long Value1;
        public long Value2;
        public long Value3; // 24 bytes total - exceeds 16 byte inline storage
    }

    [Fact]
    public void WhenCreatedWithSmallCustomStruct_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new SmallCustomStruct { Value1 = 1, Value2 = 2 };
        var newValue = new SmallCustomStruct { Value1 = 10, Value2 = 20 };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<SmallCustomStruct>();
        var retrievedNew = change.GetNewValue<SmallCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void WhenCreatedWithLargeCustomStruct_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new LargeCustomStruct { Value1 = 111111111111L, Value2 = 222222222222L };
        var newValue = new LargeCustomStruct { Value1 = 333333333333L, Value2 = 444444444444L };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<LargeCustomStruct>();
        var retrievedNew = change.GetNewValue<LargeCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void WhenCreatedWithOversizedCustomStruct_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new OversizedCustomStruct { Value1 = 1L, Value2 = 2L, Value3 = 3L };
        var newValue = new OversizedCustomStruct { Value1 = 10L, Value2 = 20L, Value3 = 30L };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<OversizedCustomStruct>();
        var retrievedNew = change.GetNewValue<OversizedCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(oldValue.Value3, retrievedOld.Value3);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
        Assert.Equal(newValue.Value3, retrievedNew.Value3);
    }

    [Fact]
    public void WhenGettingOldValueOfCustomStructAsObject_ThenReturnsBoxedStruct()
    {
        // Arrange
        var oldValue = new SmallCustomStruct { Value1 = 42, Value2 = 84 };
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, new SmallCustomStruct());

        // Act
        var result = change.GetOldValue<object>();

        // Assert
        Assert.IsType<SmallCustomStruct>(result);
        var unboxed = (SmallCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
    }

    [Fact]
    public void WhenGettingOldValueOfOversizedStructAsObject_ThenReturnsBoxedStruct()
    {
        // Arrange
        var oldValue = new OversizedCustomStruct { Value1 = 1L, Value2 = 2L, Value3 = 3L };
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, new OversizedCustomStruct());

        // Act
        var result = change.GetOldValue<object>();

        // Assert
        Assert.IsType<OversizedCustomStruct>(result);
        var unboxed = (OversizedCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
        Assert.Equal(oldValue.Value3, unboxed.Value3);
    }

    private class CustomClass
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void WhenCreatedWithReferenceType_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new CustomClass { Id = 1, Name = "Old" };
        var newValue = new CustomClass { Id = 2, Name = "New" };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Same(oldValue, change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void WhenCreatedWithNullReferenceType_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        CustomClass? oldValue = null;
        var newValue = new CustomClass { Id = 1, Name = "New" };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void WhenCreatedWithIntArray_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int[] oldValue = [1, 2, 3];
        int[] newValue = [4, 5, 6, 7];

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Same(oldValue, change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Fact]
    public void WhenCreatedWithNullArray_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int[]? oldValue = null;
        int[] newValue = [1, 2, 3];

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Theory]
    [InlineData(42)]
    [InlineData("test")]
    public void WhenGettingOldValueAsObject_ThenReturnsValue(object oldValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, oldValue);

        // Assert
        var result = change.GetOldValue<object>();
        Assert.Equal(oldValue, result);
    }

    [Fact]
    public void WhenTryGettingOldValueWithWrongType_ThenReturnsFalse()
    {
        // Arrange
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            42, 100);

        // Act
        var success = change.TryGetOldValue<string>(out var result);

        // Assert
        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void WhenTryGettingNewValueWithCorrectType_ThenReturnsTrue()
    {
        // Arrange
        var newValue = 42.5;
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            0.0, newValue);

        // Act
        var success = change.TryGetNewValue<double>(out var result);

        // Assert
        Assert.True(success);
        Assert.Equal(newValue, result);
    }

    [Fact]
    public void WhenCreated_ThenPreservesPropertyReference()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Equal(_property, change.Property);
    }

    [Fact]
    public void WhenCreated_ThenPreservesSource()
    {
        // Arrange
        var source = new object();

        // Act
        var change = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Same(source, change.Origin.Source);
    }

    [Fact]
    public void WhenCreated_ThenPreservesTimestamps()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Equal(_changedTimestamp, change.ChangedTimestamp);
        Assert.Equal(_receivedTimestamp, change.ReceivedTimestamp);
    }

    [Fact]
    public void WhenCreatedWithNullReceivedTimestamp_ThenPreservesNull()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, receivedTimestamp: null,
            "old", "new");

        // Assert
        Assert.Null(change.ReceivedTimestamp);
    }

    [Fact]
    public void WhenMergingWithNewerWithInlineValues_ThenKeepsOldFromEarlierAndNewFromLater()
    {
        // Arrange
        var earlierSource = new object();
        var laterSource = new object();
        var earlierTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var laterTimestamp = DateTimeOffset.UtcNow;

        var earlier = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(earlierSource), earlierTimestamp, earlierTimestamp,
            10, 20);
        var later = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(laterSource), laterTimestamp, laterTimestamp,
            20, 30);

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Equal(10, merged.GetOldValue<int>());
        Assert.Equal(30, merged.GetNewValue<int>());
        Assert.Same(laterSource, merged.Origin.Source);
        Assert.Equal(laterTimestamp, merged.ChangedTimestamp);
    }

    [Fact]
    public void WhenMergingWithNewerWithStrings_ThenKeepsOldFromEarlierAndNewFromLater()
    {
        // Arrange
        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "original", "intermediate");
        var later = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "intermediate", "final");

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Equal("original", merged.GetOldValue<string>());
        Assert.Equal("final", merged.GetNewValue<string>());
    }

    [Fact]
    public void WhenMergingWithNewerWithNullStringOldValue_ThenPreservesNull()
    {
        // Arrange
        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            null, "intermediate");
        var later = SubjectPropertyChange.Create<string?>(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "intermediate", "final");

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Null(merged.GetOldValue<string>());
        Assert.Equal("final", merged.GetNewValue<string>());
    }

    [Fact]
    public void WhenMergingWithNewerWithBoxedReferenceTypes_ThenKeepsOldFromEarlierAndNewFromLater()
    {
        // Arrange
        var oldObj = new CustomClass { Id = 1, Name = "Old" };
        var midObj = new CustomClass { Id = 2, Name = "Mid" };
        var newObj = new CustomClass { Id = 3, Name = "New" };

        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldObj, midObj);
        var later = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            midObj, newObj);

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Same(oldObj, merged.GetOldValue<CustomClass>());
        Assert.Same(newObj, merged.GetNewValue<CustomClass>());
    }

    [Fact]
    public void WhenMergingWithNewerWithFromSourceOrigins_ThenPreservesKindAndSource()
    {
        // Arrange
        var source = new object();
        var earlier = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "old", "intermediate");
        var later = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "intermediate", "new");

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert - both the kind and the source survive the merge
        Assert.Equal(ChangeOriginKind.FromSource, merged.Origin.Kind);
        Assert.Same(source, merged.Origin.Source);
    }

    [Fact]
    public void WhenCreatedWithSmallStructContainingReference_ThenKeepsReferenceAliveForGc()
    {
        // Arrange: a small ref-carrying struct; the non-inlined helper leaves the retained change
        // as the value's only possible GC root.
        var (change, weakReference) = CreateChangeWithReferenceStruct(_property, _changedTimestamp, _receivedTimestamp);

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.True(weakReference.IsAlive,
            "SubjectPropertyChange must keep references inside stored values alive for the GC.");
        Assert.Same(weakReference.Target, change.GetOldValue<SmallStructWithReference>().Reference);
        GC.KeepAlive(change);
    }

    [Fact]
    public void WhenCreatedWithImmutableArray_ThenKeepsBackingArrayAliveAcrossGc()
    {
        // Arrange: ImmutableArray<T> wraps a T[] reference; the change may be the backing array's only GC root.
        var (change, weakBackingArray) = CreateChangeWithImmutableArray(_property, _changedTimestamp, _receivedTimestamp);

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert - the backing array survives and the value round-trips intact through the
        // boxed object read (the path collection diffing uses)
        Assert.True(weakBackingArray.IsAlive,
            "SubjectPropertyChange must keep the ImmutableArray's backing array alive for the GC.");
        var oldValue = Assert.IsType<ImmutableArray<string>>(change.GetOldValue<object?>());
        Assert.Equal(new[] { "a", "b" }, oldValue);
        GC.KeepAlive(change);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubjectPropertyChange Change, WeakReference WeakReference) CreateChangeWithReferenceStruct(
        PropertyReference property, DateTimeOffset changedTimestamp, DateTimeOffset receivedTimestamp)
    {
        var referenced = new object();
        var change = SubjectPropertyChange.Create(
            property, origin: ChangeOrigin.Local, changedTimestamp, receivedTimestamp,
            new SmallStructWithReference(referenced), new SmallStructWithReference(null));
        return (change, new WeakReference(referenced));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubjectPropertyChange Change, WeakReference WeakBackingArray) CreateChangeWithImmutableArray(
        PropertyReference property, DateTimeOffset changedTimestamp, DateTimeOffset receivedTimestamp)
    {
        var oldValue = ImmutableArray.Create("a", "b");
        var backingArray = ImmutableCollectionsMarshal.AsArray(oldValue)!;
        var change = SubjectPropertyChange.Create(
            property, origin: ChangeOrigin.Local, changedTimestamp, receivedTimestamp,
            oldValue, ImmutableArray.Create("a", "b", "c"));
        return (change, new WeakReference(backingArray));
    }

    private readonly record struct SmallStructWithReference(object? Reference);

    [Fact]
    public void WhenMeasuringSubjectPropertyChange_ThenSizeStaysWithinOneAlignmentSlotOfMaster()
    {
        // The plain ChangeOrigin field may cost one alignment slot (8 bytes) versus master's
        // object? Source. That growth is accepted. If the benchmark gate later shows it matters
        // on the hot path, flatten the origin into a padding-folded kind byte plus the existing
        // source reference slot; that is the known optimization, not applied preemptively.
        // Master measured at 192 bytes; the accepted bound is master + one alignment slot.
        var size = System.Runtime.CompilerServices.Unsafe.SizeOf<SubjectPropertyChange>();
        Assert.True(size <= 200, $"SubjectPropertyChange grew to {size} bytes");
    }
}
