using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Modbus.Polling;

namespace Namotion.Interceptor.Modbus.Tests;

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
        pollingMetrics.SetPlan(batchCount: 3, unavailableProperties: 1);
        pollingMetrics.RecordFailedBatch();
        pollingMetrics.RecordPoll(TimeSpan.FromMilliseconds(12), time);

        // Assert
        Assert.Equal(1, diagnostics.Polling.TotalPolls);
        Assert.Equal(1, diagnostics.Polling.FailedBatches);
        Assert.Equal(3, diagnostics.Polling.BatchCount);
        Assert.Equal(1, diagnostics.Polling.UnavailableProperties);
        Assert.Equal(TimeSpan.FromMilliseconds(12), diagnostics.Polling.LastPollDuration);
        Assert.Equal(time, diagnostics.Polling.LastPollTime);
    }
}
