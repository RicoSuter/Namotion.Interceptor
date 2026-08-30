using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateApplyFailureTests
{
    [Fact]
    public void WhenOnePropertyThrows_ThenTheOthersStillApply()
    {
        // Arrange
        // The update is built by hand so it carries only the two device properties: a complete update
        // would also carry the target's throw configuration and disarm it before PropertyA is applied.
        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    [nameof(ThrowingDevice.PropertyA)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = true },
                    [nameof(ThrowingDevice.PropertyB)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = true }
                }
            }
        };

        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = name => name == nameof(ThrowingDevice.PropertyA)
        };

        // Act
        var exception = Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.False(target.PropertyA);
        Assert.True(target.PropertyB);
        Assert.Contains(nameof(ThrowingDevice.PropertyA), exception.ToString());
    }
}
