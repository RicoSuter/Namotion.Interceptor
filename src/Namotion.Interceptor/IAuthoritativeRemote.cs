namespace Namotion.Interceptor;

/// <summary>
/// Marks a remote that may be stamped as the origin of a change and whose values are authoritative:
/// the external system it represents holds the truth and the subject is a replica of it, so the model
/// must not veto what it sends.
/// </summary>
/// <remarks>
/// Implemented by <c>ISubjectSource</c>, so every real source is authoritative without opting in.
/// It exists separately because the same stamping API carries writes in the opposite direction:
/// a server-role connector stamps a remote peer's write into a model that the peer does not own.
/// Such a connector is not a source, does not carry this marker, and its writes stay untrusted
/// input. An unmarked remote is therefore treated as untrusted, which is the safe default. The marker
/// governs inbound values only: a value a source confirmed during a transaction commit is the model's
/// own value returning, so it is never re-validated whatever the confirming party is.
/// </remarks>
public interface IAuthoritativeRemote;
