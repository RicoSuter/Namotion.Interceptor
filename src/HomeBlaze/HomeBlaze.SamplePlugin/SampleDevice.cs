using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.SamplePlugin;

[InterceptorSubject]
public partial class SampleDevice : IConfigurable
{
    public SampleDevice()
    {
        Name = "Sample Device";
        Temperature = 0.0;
    }

    [Configuration]
    public partial string Name { get; set; }

    [State]
    public partial double Temperature { get; internal set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
