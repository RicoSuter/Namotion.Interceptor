using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class SubjectStructureTests
{
    [Fact]
    public void ChildrenName_IsTheNameTheRegistryRejectsForAddedProperties()
    {
        Assert.Equal("\0children", SubjectStructure.ChildrenName);
    }
}
