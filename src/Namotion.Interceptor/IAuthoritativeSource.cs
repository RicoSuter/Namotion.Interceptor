namespace Namotion.Interceptor;

/// <summary>
/// Marks an object that may be stamped as a <see cref="ChangeOriginKind.FromSource"/> origin and
/// whose values are authoritative: the external system it represents holds the truth and the subject
/// is a replica of it, so the model must not veto what it sends.
/// </summary>
/// <remarks>
/// Implemented by <c>ISubjectSource</c>, so every real source is authoritative without opting in.
/// It exists separately because the same stamping API carries writes in the opposite direction:
/// a server-role connector stamps a remote peer's write into a model that the peer does not own.
/// Such a connector is not a source, does not carry this marker, and its writes stay untrusted
/// input. Anything unmarked is therefore treated as untrusted, which is the safe default.
/// </remarks>
public interface IAuthoritativeSource;
