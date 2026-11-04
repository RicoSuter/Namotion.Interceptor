using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedRecalculationWriteContextTests
{
    [Fact]
    public void WhenPlainPropertyIsWritten_ThenOnlyTheDerivedRecalculationIsFlagged()
    {
        // Arrange
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        person.FirstName = "Ada";

        // Assert
        Assert.Contains((nameof(Person.FirstName), false), probe.Writes);
        Assert.Contains((nameof(Person.FullName), true), probe.Writes);
        Assert.DoesNotContain((nameof(Person.FirstName), true), probe.Writes);
        Assert.DoesNotContain((nameof(Person.FullName), false), probe.Writes);
    }

    [Fact]
    public void WhenSettableDerivedPropertyIsWritten_ThenTheWriteIsNotFlaggedAndItsRecalculationsAre()
    {
        // Arrange
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.AddService(probe);
        var person = new DerivedSetterPerson(context);
        probe.Writes.Clear();

        // Act
        person.Nickname = "Bob";

        // Assert
        Assert.Single(probe.Writes, write => write == (nameof(DerivedSetterPerson.Nickname), false));
        Assert.Contains((nameof(DerivedSetterPerson.Nickname), true), probe.Writes);
        Assert.Contains((nameof(DerivedSetterPerson.NicknameWithPrefix), true), probe.Writes);
        Assert.DoesNotContain((nameof(DerivedSetterPerson.NicknameWithPrefix), false), probe.Writes);
    }

    private sealed class RecalculationFlagProbe : IWriteInterceptor
    {
        public List<(string Property, bool IsDerivedRecalculation)> Writes { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((context.Property.Name, context.IsDerivedRecalculation));
            next(ref context);
        }
    }
}
