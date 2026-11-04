namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// The category of a failed transaction commit, see <see cref="CommitFailureClassifier"/>.
/// </summary>
public enum CommitFailureKind
{
    /// <summary>
    /// Another participant changed a written property since the transaction captured it. Expected under chaos.
    /// </summary>
    Conflict,

    /// <summary>
    /// Only source writes failed, as when a connector is disconnected or faulted. Expected under chaos.
    /// </summary>
    SourceWriteFailure,

    /// <summary>
    /// Any other failure, for example a validation rejection, a local apply or revert failure, or a transaction
    /// writer that threw or violated its contract. Fails the cycle.
    /// </summary>
    Unexpected
}
