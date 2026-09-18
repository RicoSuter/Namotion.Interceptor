namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// The outcome of <see cref="SubjectUpdateApplyContext.ClaimSubjectPayload"/>.
/// </summary>
internal enum SubjectPayloadClaim
{
    /// <summary>The subject takes the payload of the ID now and has to receive it.</summary>
    Claimed,

    /// <summary>The subject already took the payload of this ID in this update, so it is not applied again.</summary>
    AlreadyClaimed,

    /// <summary>
    /// The subject already took the payload of another ID in this update. Two IDs are two source
    /// subjects, so this subject cannot be the one the ID names.
    /// </summary>
    ClaimedByAnotherId
}
