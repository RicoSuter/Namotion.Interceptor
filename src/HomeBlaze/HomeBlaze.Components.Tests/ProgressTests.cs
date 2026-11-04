using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services.Lifecycle;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ProgressTests
{
    // The initializer turns [State] into the registry attribute that holds the unit, as in the application.
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithLifecycle()
        .WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

    private ValueRangeState GetState(Progress progress, MeasuredSubject subject, string propertyName)
    {
        var property = subject.TryGetRegisteredProperty(propertyName);
        return ValueRangeResolver.GetState(property, property?.GetValue(), progress.Minimum, progress.Maximum);
    }

    [Fact]
    public void WhenProgressIsCreated_ThenItHasNoRangeOfItsOwnAndThePrimaryColor()
    {
        // Act
        var progress = new Progress(_context);

        // Assert
        Assert.Null(progress.Minimum);
        Assert.Null(progress.Maximum);
        Assert.Equal(Color.Primary, progress.Color);
    }

    [Fact]
    public void WhenPropertyIsAPercent_ThenTheBarRunsFromZeroToOne()
    {
        // Arrange
        var progress = new Progress(_context);
        var subject = new MeasuredSubject(_context) { Load = 0.25 };

        // Act
        var state = GetState(progress, subject, nameof(MeasuredSubject.Load));

        // Assert
        Assert.Equal(25d, state.Percent);
        Assert.Null(state.Error);
    }

    [Fact]
    public void WhenPropertyHasRangeAttributes_ThenTheyAreTheRangeOfTheBar()
    {
        // Arrange
        var progress = new Progress(_context);
        var subject = new MeasuredSubject(_context) { TargetSpeed = 750 };

        // Act
        var state = GetState(progress, subject, nameof(MeasuredSubject.TargetSpeed));

        // Assert
        Assert.Equal(25d, state.Percent);
        Assert.Equal(new ValueRange(0, 3000), state.Range);
    }

    [Fact]
    public void WhenANumberHasNoRange_ThenTheBarHasTheMissingRangeError()
    {
        // Arrange
        var progress = new Progress(_context);
        var subject = new MeasuredSubject(_context) { Speed = 500 };

        // Act
        var state = GetState(progress, subject, nameof(MeasuredSubject.Speed));

        // Assert
        Assert.Equal(ValueRangeResolver.MissingRangeError, state.Error);
    }

    [Fact]
    public void WhenRangeIsInverted_ThenTheBarHasTheRangeError()
    {
        // Arrange
        var progress = new Progress(_context) { Minimum = 1600, Maximum = 0 };
        var subject = new MeasuredSubject(_context) { Speed = 500 };

        // Act
        var state = GetState(progress, subject, nameof(MeasuredSubject.Speed));

        // Assert
        Assert.Equal("Maximum must be above Minimum: 1600 to 0", state.Error);
    }

    [Fact]
    public void WhenPropertyHoldsNoNumber_ThenThereIsNoBarAndNoError()
    {
        // Arrange
        var progress = new Progress(_context);
        var subject = new MeasuredSubject(_context) { Name = "Pump" };

        // Act
        var state = GetState(progress, subject, nameof(MeasuredSubject.Name));

        // Assert
        Assert.Null(state.Percent);
        Assert.Null(state.Error);
    }
}
