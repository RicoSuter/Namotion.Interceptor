using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Modbus.Client.Polling;

namespace Namotion.Interceptor.Modbus.Tests.Client;

public class ModbusClientDiagnosticsTests
{
    [Fact]
    public void WhenMetricsAreRecorded_ThenDiagnosticsReportThem()
    {
        // Arrange
        var pollingMetrics = new ModbusPollingMetrics();
        var diagnostics = new ModbusClientDiagnostics(new SourceMetrics(), pollingMetrics);
        var time = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        // Act
        pollingMetrics.SetPlan(batchCount: 3, unavailablePropertyCount: 1);
        pollingMetrics.RecordFailedRequest();
        pollingMetrics.RecordPoll(TimeSpan.FromMilliseconds(12), time, hasReadData: true);

        // Assert
        Assert.Equal(1, diagnostics.Polling.TotalPolls);
        Assert.Equal(1, diagnostics.Polling.TotalFailedRequests);
        Assert.Equal(3, diagnostics.Polling.BatchCount);
        Assert.Equal(1, diagnostics.Polling.UnavailablePropertyCount);
        Assert.Equal(TimeSpan.FromMilliseconds(12), diagnostics.Polling.LastPollDuration);
        Assert.Equal(time, diagnostics.Polling.LastPollTime);
    }
}
