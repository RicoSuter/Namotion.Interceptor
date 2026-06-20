using Namotion.Interceptor.Generator.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class HookScopeGenerationTests
{
    [Fact]
    public void WhenHookImplemented_ThenHookBodyRunsUnderLocalOriginScope()
    {
        // Arrange
        var person = new PersonWithSelectiveHooks();
        var source = new object();

        // Act: write under an ambient source scope, like an inbound/commit apply.
        using (SubjectChangeContext.WithSource(source))
        {
            person.Hooked = "value";
        }

        // Assert: the implemented hook saw a null source (the generated scope reset it),
        // even though the surrounding write ran under a real source scope.
        Assert.Null(person.HookedSourceInsideChanged);
    }

    [Fact]
    public void WhenHookNotImplemented_ThenSetterStillWritesValue()
    {
        // Arrange
        var person = new PersonWithSelectiveHooks();

        // Act: a property whose hooks are not implemented pays nothing and behaves normally.
        person.NotHooked = "value";

        // Assert
        Assert.Equal("value", person.NotHooked);
    }

    [Fact]
    public void WhenBaseManuallyImplementsRaisePropertyChanged_ThenRaiseRunsUnderLocalOriginScope()
    {
        // Arrange
        var subject = new ManualInpcSubject();
        var source = new object();
        object? sourceSeenDuringRaise = "sentinel";
        subject.PropertyChanged += (_, _) => sourceSeenDuringRaise = SubjectChangeContext.Current.Source;

        // Act: write under an ambient source scope. The generated subclass setter wraps the
        // interface-cast RaisePropertyChanged call site in a local-origin scope.
        using (SubjectChangeContext.WithSource(source))
        {
            subject.Name = "value";
        }

        // Assert: the INPC subscriber, invoked by the manual base's raise, saw a null source.
        Assert.Null(sourceSeenDuringRaise);
    }
}
