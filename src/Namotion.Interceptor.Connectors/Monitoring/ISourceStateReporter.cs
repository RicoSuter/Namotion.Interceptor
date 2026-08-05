namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// The seam through which <see cref="SubjectPropertyWriter"/> drives a source's connection state.
/// Internal because state reporting is the base class's responsibility, not part of the source contract.
/// </summary>
internal interface ISourceStateReporter
{
    /// <summary>Reports that the source is connecting or reconnecting and its live feed is not trusted.</summary>
    void ReportConnecting();

    /// <summary>Reports that the source completed its initial load procedure.</summary>
    void ReportSynchronized();
}
