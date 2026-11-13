using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Result of reverting previously-written changes at their external sources during transaction rollback.
/// </summary>
/// <param name="Failed">
/// The changes whose revert at the source failed.
/// </param>
/// <param name="Errors">
/// The errors that occurred while reverting, typically one per source that failed to revert.
/// </param>
public readonly record struct SourceRevertResult(
    IReadOnlyList<SubjectPropertyChange> Failed,
    IReadOnlyList<Exception> Errors);
