namespace HomeBlaze.Storage;

/// <summary>
/// The route under which the content of the files of a storage is served over HTTP.
/// </summary>
public static class StorageFileRoute
{
    /// <summary>
    /// The prefix of the route. The route path of the file subject follows it, as in <c>/files/Docs/images/plan.png</c>.
    /// </summary>
    public const string Prefix = "/files";
}
