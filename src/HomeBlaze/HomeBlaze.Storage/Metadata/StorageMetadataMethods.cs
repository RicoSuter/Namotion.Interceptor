using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// The methods that agents read and change metadata with: their descriptions, the queries for a reader's permissions,
/// and the conversion of a refusal into the error an agent is told.
/// </summary>
internal static class StorageMetadataMethods
{
    /// <summary>The most matches <see cref="Search"/> returns.</summary>
    public const int MaxSearchMetadataMatches = 100;

    public const string SetMetadataDescription =
        "Sets metadata of a subject in this storage or of one of its properties. path is the path of the subject below this storage as browse shows it, " +
        "for example Devices/Shelly/Channels[0] (a leading / is ignored), and empty for the storage itself; property is the name of a property of that subject, or omitted for the subject. " +
        "values is a JSON object that is merged into the metadata: a key with a value is set, replacing a nested value as a whole, a key with null is removed, and " +
        "every other key stays; keys match exactly, case included. HomeBlaze reads title (of a folder), navTitle, icon (a MudBlazor icon name), location (NavBar or " +
        "AppBar), alignment (Left, Right or Menu), notes (Markdown of up to 4 KB without HTML, pictures or blocks), links (an array of at most 20 objects with an " +
        "optional title and either url, an http, https or mailto address, or path, the absolute path of a page, subject or property) and tags (an array of " +
        "at most 20 strings of up to 64 characters, unique without regard to case); any other key, such as comment, is stored as given. " +
        "Example: {\"navTitle\": \"Hall light\", \"tags\": [\"floor-2\"], \"icon\": null}.";

    public const string SearchMetadataDescription =
        "Finds the subjects and properties in this storage whose metadata matches every filter that is given, and returns them by path and then " +
        "property, at most the first 100, each with path, the path of the subject relative to this storage, and property, the name of the property or omitted for the " +
        "subject, as SetMetadata takes them. Only metadata the caller may read is searched, as the caller is shown it. tag: a tag in tags; key: a key that is set, such as notes; value: equals the whole value of a string, or of a " +
        "string in an array, of key when it is given, else of any key, and never a number or a boolean; path: the path of a subject relative to this " +
        "storage that the results are at or below. Keys match exactly, and tags and values without regard to case. To find text inside notes, use search_markdown. " +
        "Example: tag floor-2 finds everything tagged floor-2.";

    public const string GetOrderDescription =
        "Gets the names of the children of this folder or storage that the caller may see, in the order in which they are shown in navigation and in the browser.";

    public const string SetOrderDescription =
        "Sets the order in which the children of this folder or storage are shown: the given names first, in the given order, then the other children in the " +
        "order in which they are shown now. Each name must be a name that GetOrder returns, given once.";

    /// <summary>
    /// Gets the first <see cref="MaxSearchMetadataMatches"/> subjects and properties at or below <paramref name="start"/>
    /// whose metadata member <paramref name="permissions"/> allow to read on a shown subject, see
    /// <see cref="UserPermissionsExtensions.CanReadShown"/>, and whose metadata, as <paramref name="permissions"/> show it,
    /// matches every filter that is given; see <see cref="MetadataSearch.Search"/>.
    /// </summary>
    public static MetadataSearchMatch[] Search(
        IInterceptorSubject storage, IInterceptorSubject start, string? tag, string? key, string? value, IUserPermissions permissions)
    {
        var matches = MetadataSearch.Search(
            storage,
            member => permissions.CanReadShown(member) &&
                permissions.GetValue(member) is ImmutableDictionary<string, JsonElement> metadata &&
                MetadataSearch.IsMatch(metadata, tag, key, value),
            start: start);
        return [.. matches.Take(MaxSearchMetadataMatches)];
    }

    /// <summary>
    /// Gets the names of the children of <paramref name="container"/> that <paramref name="permissions"/> show, in the
    /// order in which they are shown: none when they do not allow to read the member that holds the children, else each
    /// child they show, see <see cref="IUserPermissions.CanShow"/>.
    /// </summary>
    public static string[] GetOrder(IInterceptorSubject container, IUserPermissions permissions)
    {
        if (container.TryGetRegisteredSubject() is not { } registered ||
            registered.TryGetProperty(IMetadataContainer.ChildrenPropertyName) is not { } children ||
            !permissions.CanRead(children))
        {
            return [];
        }

        var shownNames = ChildOrdering.GetListedNames(registered, permissions, static (permissions, _, child) => permissions.CanShow(child.Subject));
        return Array.FindAll(ChildOrdering.GetShownNames(container), shownNames.Contains);
    }

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
