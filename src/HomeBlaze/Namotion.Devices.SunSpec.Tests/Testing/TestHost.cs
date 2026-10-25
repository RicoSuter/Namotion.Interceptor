using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.SunSpec.Tests.Testing;

[InterceptorSubject]
public partial class TestHost
{
    public TestHost()
    {
        Device = null;
    }

    public partial SunSpecDevice? Device { get; set; }

    public static SunSpecDevice CreateAttachedDevice(string? dataDirectory = null)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        if (dataDirectory is not null)
        {
            context.WithService<IDataDirectoryProvider>(() => new TestDataDirectory(dataDirectory));
        }

        var device = new SunSpecDevice(NullLogger<SunSpecDevice>.Instance);
        _ = new TestHost(context) { Device = device };
        return device;
    }

    private sealed class TestDataDirectory(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory { get; } = dataDirectory;
    }
}
