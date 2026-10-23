using HomeBlaze.Services.Display;
using HomeBlaze.Services.Expressions;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Display;

public class ValueBindingTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }

    [Fact]
    public void WhenPropertyHoldsAPlainValue_ThenValueIsBoundAndItsOwnerIsTheSubject()
    {
        // Arrange
        var charger = new BindingCharger(CreateContext()) { IsCharging = true };
        var property = charger.TryGetRegisteredProperty(nameof(BindingCharger.IsCharging))!;

        // Act
        var binding = ValueBinding.FromProperty(property);

        // Assert
        Assert.Same(property, binding.Property);
        Assert.Equal(true, binding.Value);
        Assert.Same(charger, binding.Subject);
    }

    [Fact]
    public void WhenPropertyIsASubjectReference_ThenTheReferencedSubjectIsBoundAndNoValue()
    {
        // Arrange
        var context = CreateContext();
        var session = new BindingSession(context) { Energy = 12m };
        var charger = new BindingCharger(context) { Session = session };
        var property = charger.TryGetRegisteredProperty(nameof(BindingCharger.Session))!;

        // Act
        var binding = ValueBinding.FromProperty(property);
        var fromResult = ValueBinding.FromExpressionValue(ExpressionValue.FromProperty(property, session));

        // Assert
        Assert.Null(binding.Value);
        Assert.Same(session, binding.Subject);
        Assert.Null(fromResult.Value);
        Assert.Same(session, fromResult.Subject);
    }

    [Fact]
    public void WhenSubjectReferenceIsNull_ThenNoSubjectIsBound()
    {
        // Arrange
        var charger = new BindingCharger(CreateContext());
        var property = charger.TryGetRegisteredProperty(nameof(BindingCharger.Session))!;

        // Act
        var binding = ValueBinding.FromProperty(property);

        // Assert
        Assert.Null(binding.Subject);
        Assert.Null(binding.Value);
    }

    [Fact]
    public void WhenResultHasNoProperty_ThenOnlyItsValueIsBound()
    {
        // Act
        var binding = ValueBinding.FromExpressionValue(ExpressionValue.FromBoolean(false));

        // Assert
        Assert.Equal(false, binding.Value);
        Assert.Null(binding.Subject);
        Assert.Null(binding.Property);
    }
}

[InterceptorSubject]
public partial class BindingCharger
{
    public partial bool IsCharging { get; set; }

    public partial BindingSession? Session { get; set; }
}

[InterceptorSubject]
public partial class BindingSession
{
    public partial decimal Energy { get; set; }
}
