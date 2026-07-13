using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class OriginWriteContextTests
{
    private sealed class OriginProbe : IWriteInterceptor
    {
        public ChangeOriginKind? KindBeforeWrite;
        public ChangeOriginKind? KindAfterWrite;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            KindBeforeWrite = context.Origin.Kind;
            next(ref context);
            KindAfterWrite = context.Origin.Kind;
        }
    }

    [Fact]
    public void WhenWriteIsSetFromSource_ThenOriginIsFromSourceBeforeAndAfterWrite()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");
        var source = new object();

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), 42))
        {
            car.Speed = 42;
        }

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenStoredValueDiffersFromSentValue_ThenOriginIsFinalizedToLocal()
    {
        // Arrange: set the pending origin with sentValue 42 but write a different
        // value, as a transforming hook or rewriting interceptor would.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), 42))
        {
            car.Speed = 99;
        }

        // Assert: attempted origin visible mid-chain, Local after finalization.
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenSentValueIsNullForValueTypeProperty_ThenOriginIsFinalizedToLocalWithoutThrowing()
    {
        // Arrange: a stamped write on a value-type property whose sent value is null (a malformed
        // inbound value). The survival check must demote to Local via the typed-pattern unbox instead
        // of throwing when it casts null down to the value type.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), sentValue: null))
        {
            car.Speed = 55;
        }

        // Assert: no throw; attempted origin visible mid-chain, demoted to Local after finalization.
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenSentValueTypeMismatchesValueTypeProperty_ThenOriginIsFinalizedToLocalWithoutThrowing()
    {
        // Arrange: the sent value is a boxed string but the property is an int, so the typed-pattern
        // unbox fails and the origin demotes to Local rather than throwing an InvalidCastException.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), sentValue: "55"))
        {
            car.Speed = 55;
        }

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenWriteHasNoPendingOrigin_ThenOriginIsLocal()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);

        // Act
        car.Speed = 7;

        // Assert
        Assert.Equal(ChangeOriginKind.Local, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }
}
