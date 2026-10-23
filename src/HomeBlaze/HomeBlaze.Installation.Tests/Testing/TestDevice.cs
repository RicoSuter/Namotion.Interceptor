using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Installation.Tests.Testing;

[InterceptorSubject]
public partial class TestDevice : INotificationPublisher
{
    [Configuration]
    public partial string Label { get; set; }

    [State]
    public partial decimal? Temperature { get; set; }

    public TestDevice()
    {
        Label = string.Empty;
        Temperature = null;
    }

    [Operation(Title = "Reset")]
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Query(Title = "Read")]
    public Task<string> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Label);

    public Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken) =>
        Task.FromResult(new NotificationResult());
}
