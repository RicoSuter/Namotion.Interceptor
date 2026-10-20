using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class MetadataPathsTests
{
    private static MetadataPathsTestFolder CreateTree()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var device = new MetadataPathsTestDevice(context) { Channels = [new MetadataPathsTestDevice()] };
        return new MetadataPathsTestFolder(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Shelly"] = device } };
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" / ", false)]
    [InlineData("/Shelly/Channels[0]", true)]
    public void WhenThePathIsOnlyWhitespaceAndSlashesOrHasALeadingSlash_ThenTheOriginOrTheSubjectAtThePathIsResolved(string path, bool isChannel)
    {
        // Arrange
        var root = CreateTree();
        var channel = ((MetadataPathsTestDevice)root.Children["Shelly"]).Channels[0];

        // Act
        var subject = MetadataPaths.TryResolveSubject(root, path);

        // Assert
        Assert.Same(isChannel ? channel : root, subject);
    }
}

[InterceptorSubject]
public partial class MetadataPathsTestFolder
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public MetadataPathsTestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}

[InterceptorSubject]
public partial class MetadataPathsTestDevice
{
    public partial MetadataPathsTestDevice[] Channels { get; set; }

    public MetadataPathsTestDevice()
    {
        Channels = [];
    }
}
