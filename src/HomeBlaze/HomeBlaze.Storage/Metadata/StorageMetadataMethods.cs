using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// The descriptions of the methods that agents read and change metadata with, and the conversion of a refusal into
/// the error an agent is told.
/// </summary>
internal static class StorageMetadataMethods
{
    public const string SetMetadataDescription =
        "Sets metadata of a subject in this storage or of one of its properties. path is the path of the subject below this storage as browse shows it, " +
        "for example Devices/Shelly/Channels[0] (a leading / is ignored), and empty for the storage itself; property is the name of a property of that subject, or omitted for the subject. " +
        "values is a JSON object that is merged into the metadata: a key with a value is set, replacing a nested value as a whole, a key with null is removed, and " +
        "every other key stays; keys match exactly, case included. HomeBlaze reads title (of a folder), navTitle, icon (a MudBlazor icon name), location (NavBar or " +
        "AppBar) and alignment (Left or Right); any other key, such as comment, is stored as given. At most 64 keys, key names of up to 64 characters, strings of up " +
        "to 256 characters and values of up to 4 KB. The order of the children of a folder is set with SetOrder of the folder. Example: {\"navTitle\": \"Hall light\", \"icon\": null}.";

    public const string GetOrderDescription =
        "Gets the names of the children of this folder or storage in the order in which they are shown in navigation and in the browser.";

    public const string SetOrderDescription =
        "Sets the order in which the children of this folder or storage are shown: the given names first, in the given order, then the other children in the " +
        "order in which they are shown now. Each name must be a name that GetOrder returns, given once.";

    /// <summary>
    /// Runs a call and turns a refusal of the storage into an <see cref="ArgumentException"/>, the error whose message
    /// a method passes on to an agent, so that the agent learns why. Any other failure keeps its type.
    /// </summary>
    public static async Task RunAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (StorageRefusedException exception)
        {
            throw new ArgumentException(exception.Message, exception);
        }
    }
}
