using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryEligibilityTests
{
    private enum SampleEnum
    {
        A,
        B
    }

    [Theory]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(long))]
    [InlineData(typeof(int))]
    [InlineData(typeof(short))]
    [InlineData(typeof(sbyte))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(string))]
    [InlineData(typeof(SampleEnum))]
    [InlineData(typeof(double?))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(decimal?))]
    [InlineData(typeof(SampleEnum?))]
    public void WhenTypeIsRecordable_ThenIsRecordableTypeIsTrue(Type type)
    {
        // Arrange & Act
        var result = HistoryEligibility.IsRecordableType(type);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(HistoryEligibilityTests))]
    [InlineData(typeof(EligibilityTestSubject))]
    public void WhenTypeIsNotRecordable_ThenIsRecordableTypeIsFalse(Type type)
    {
        // Arrange & Act
        var result = HistoryEligibility.IsRecordableType(type);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenPropertyIsRecordableScalarState_ThenHasHistoryIsTrue()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.Temperature));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenPropertyIsNotState_ThenHasHistoryIsFalse()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.NotState));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenStatePropertyCanContainSubjects_ThenHasHistoryIsFalse()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.Child));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenStatePropertyTypeIsNotRecordable_ThenHasHistoryIsFalse()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.Marker));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.False(result);
    }

    private static RegisteredSubjectProperty GetRegisteredProperty(string propertyName)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var subject = new EligibilityTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        return registered.TryGetProperty(propertyName)!;
    }
}

[InterceptorSubject]
public partial class EligibilityTestSubject
{
    [State]
    public partial double Temperature { get; set; }

    public partial string? NotState { get; set; }

    [State]
    public partial EligibilityTestSubject? Child { get; set; }

    [State]
    public partial Guid Marker { get; set; }
}
