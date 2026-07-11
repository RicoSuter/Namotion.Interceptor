using Namotion.Interceptor.Tracking.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyLocalOriginTests
{
    [Fact]
    public void WhenDerivedRecalculationTriggeredUnderSourceScope_ThenManualInpcRaiseSeesLocalOrigin()
    {
        // Arrange: the subject inherits a hand-written IRaisePropertyChanged implementation.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new ManualInpcDerivedPerson(context);
        var source = new object();
        object? sourceSeenDuringDerivedRaise = "sentinel";
        person.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManualInpcDerivedPerson.FullName))
            {
                sourceSeenDuringDerivedRaise = SubjectChangeContext.Current.Source;
            }
        };

        // Act: trigger the derived recalculation under an ambient source scope, like an inbound apply.
        using (SubjectChangeContext.WithSource(source))
        {
            person.FirstName = "John";
        }

        // Assert: the INPC handler observed a null source throughout the derived notification.
        Assert.Null(sourceSeenDuringDerivedRaise);
    }
}
