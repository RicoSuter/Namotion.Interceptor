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

        // Act: write under an ambient source scope. The manual raiser owns the local-origin scope.
        using (SubjectChangeContext.WithSource(source))
        {
            subject.Name = "value";
        }

        // Assert: the INPC subscriber, invoked by the manual base's raise, saw a null source.
        Assert.Null(sourceSeenDuringRaise);
    }

    [Fact]
    public void WhenMultiLevelChainRootedAtManualBase_ThenRaiseRunsUnderLocalOriginScope()
    {
        // Arrange: ManualInpcGrandchild : ManualInpcSubject : ManualInpcBase.
        var subject = new ManualInpcGrandchild();
        var source = new object();
        object? sourceSeenDuringRaise = "sentinel";
        subject.PropertyChanged += (_, _) => sourceSeenDuringRaise = SubjectChangeContext.Current.Source;

        // Act
        using (SubjectChangeContext.WithSource(source))
        {
            subject.GrandchildName = "value";
        }

        // Assert: the subscriber observed a null source through the inherited manual raise.
        Assert.Null(sourceSeenDuringRaise);
    }

    [Fact]
    public void WhenBaseSubjectManuallyImplementsInpcItself_ThenChildRaiseRunsUnderLocalOriginScope()
    {
        // Arrange: SelfInpcChild : SelfInpcSubject, where the parent is a generated subject that
        // declares IRaisePropertyChanged manually in its own base list and owns the raise contract.
        var subject = new SelfInpcChild();
        var source = new object();
        object? sourceSeenDuringRaise = "sentinel";
        subject.PropertyChanged += (_, _) => sourceSeenDuringRaise = SubjectChangeContext.Current.Source;

        // Act
        using (SubjectChangeContext.WithSource(source))
        {
            subject.ChildName = "value";
        }

        // Assert
        Assert.Null(sourceSeenDuringRaise);
    }

    [Fact]
    public void WhenLocalWriteRaisesPropertyChanged_ThenHandlerSeesNullSource()
    {
        // Arrange: the generated RaisePropertyChanged skips the local-origin scope when the ambient
        // source is already null; the handler must still observe a null source.
        var person = new PersonWithSelectiveHooks();
        object? sourceSeenDuringRaise = "sentinel";
        person.PropertyChanged += (_, _) => sourceSeenDuringRaise = SubjectChangeContext.Current.Source;

        // Act
        person.NotHooked = "value";

        // Assert
        Assert.Null(sourceSeenDuringRaise);
    }
}
