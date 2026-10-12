using System.Text;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Parent;
using MarkdownContentParser = HomeBlaze.Storage.Internal.MarkdownContentParser;

namespace HomeBlaze.Storage.Files;

/// <summary>
/// Represents a Markdown file in storage with support for embedded subjects and expressions.
/// </summary>
[InterceptorSubject]
[FileExtension(".md")]
[FileExtension(".markdown")]
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage, IConfigurationWriter
{
    [GeneratedRegex(@"```subject\(([^)]+)\)\s*\n[\s\S]*?```")]
    private static partial Regex SubjectBlockRegex();

    /// <summary>The largest Markdown source, in UTF-8 bytes, that the write operations accept.</summary>
    public const int MaxContentSize = 64 * 1024;

    private readonly MarkdownContentParser _parser;
    private readonly ConfigurableSubjectSerializer _serializer;

    // Serializes every write of this file, so the read-modify-write of ReplaceText cannot interleave with another write.
    // It is held while the storage reloads the file after a write, so nothing in that reload may write this file.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IStorageContainer Storage { get; }
    public string FullPath { get; }
    public string Name { get; }

    /// <summary>
    /// Whether the file is in local storage (on disk or in memory), the only storage the write operations change.
    /// </summary>
    public bool IsLocal => MarkdownWriteRules.IsLocal(Storage);

    [Derived]
    public string? Title => Frontmatter?.Title ?? FormatFilename(Name);

    public string? IconName => "Article";

    [Derived]
    public string? NavigationTitle => Frontmatter?.NavTitle;

    [Derived]
    public string? NavigationIconName => Frontmatter?.Icon ?? IconName;

    [Derived]
    public int? PagePosition => Frontmatter?.Position;

    [Derived]
    public NavigationLocation PageLocation => Frontmatter?.Location ?? NavigationLocation.NavBar;

    [Derived]
    public AppBarAlignment AppBarAlignment => Frontmatter?.Alignment ?? AppBarAlignment.Left;

    public partial MarkdownFrontmatter? Frontmatter { get; private set; }

    /// <summary>
    /// Tracked content property - triggers UI refresh on change.
    /// </summary>
    public partial string? Content { get; private set; }

    /// <summary>
    /// Child subjects parsed from markdown content.
    /// Contains HtmlSegments, RenderExpressions, and embedded subjects.
    /// </summary>
    [InlinePaths]
    public partial IDictionary<string, IInterceptorSubject> Children { get; private set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    [State("Size", Position = 1)]
    public partial long FileSize { get; set; }

    /// <summary>
    /// Last modification time (UTC).
    /// </summary>
    [State("Modified", Position = 2)]
    public partial DateTime LastModified { get; set; }

    public MarkdownFile(
        IStorageContainer storage,
        string fullPath,
        MarkdownContentParser parser,
        ConfigurableSubjectSerializer serializer)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Children = new Dictionary<string, IInterceptorSubject>();
        _parser = parser;
        _serializer = serializer;
    }

    private async Task LoadFileAsync(CancellationToken cancellationToken)
    {
        var metadata = await Storage.GetBlobMetadataAsync(FullPath, cancellationToken);
        if (metadata != null)
        {
            FileSize = metadata.Size;
            LastModified = metadata.LastModifiedUtc ?? DateTime.UtcNow;
        }

        await using var stream = await Storage.ReadBlobAsync(FullPath, cancellationToken);
        using var reader = new StreamReader(stream);
        Content = await reader.ReadToEndAsync(cancellationToken);
        Frontmatter = FrontmatterParser.Parse<MarkdownFrontmatter>(Content);
        Children = await _parser.ParseAsync(Content, this, Children, cancellationToken);
    }

    [Query(Title = "Read Raw Content", Description = "Returns the markdown file source, including unevaluated {{ path }} expressions", Icon = "Description")]
    public string? ReadRawContent() => Content;

    /// <summary>
    /// Returns the content with each {{ path }} expression replaced by its current value.
    /// </summary>
    /// <param name="includeSourcePaths">When true, each resolved value is followed by the absolute path of its
    /// property in parentheses, for example "21.5 (/Devices/Kitchen/Temperature)"; get_property reads that path when the
    /// property is exposed as state. Null is treated as false.</param>
    [Query(Title = "Read Evaluated Content", Description = "Returns the markdown content with {{ path }} expressions evaluated against the current object graph. With includeSourcePaths set to true, each value is followed by the absolute path of its property in parentheses, which get_property reads when the property is exposed as state", Icon = "Preview")]
    public string? ReadEvaluatedContent(bool? includeSourcePaths = null) =>
        _parser.RenderContent(Content, this, includeSourcePaths == true);

    /// <summary>
    /// Replaces the whole Markdown source of the file.
    /// </summary>
    /// <exception cref="ArgumentException">The content is larger than <see cref="MaxContentSize"/> bytes.</exception>
    /// <exception cref="InvalidOperationException">The file is not in local storage.</exception>
    [Operation(Title = "Replace Content", Description = "Replaces the whole markdown source of this file.", Icon = "EditNote")]
    public async Task ReplaceContentAsync(string content, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureLocal(Storage);
        var bytes = MarkdownWriteRules.GetContentBytes(content, nameof(content));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await WriteUnlockedAsync(bytes, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="oldText"/> in the Markdown source of the file with <paramref name="newText"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, not found, found more than once, or the result is larger than
    /// <see cref="MaxContentSize"/> bytes.</exception>
    /// <exception cref="InvalidOperationException">The file is not in local storage.</exception>
    [Operation(Title = "Replace Text", Description = "Replaces one exact, unique passage of the markdown source.", Icon = "FindReplace")]
    public async Task ReplaceTextAsync(string oldText, string newText, CancellationToken cancellationToken)
    {
        MarkdownWriteRules.EnsureLocal(Storage);
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("The text to replace must not be empty.", nameof(oldText));
        }

        ArgumentNullException.ThrowIfNull(newText);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Read from storage rather than Content, which lags behind an external edit until its file event arrives.
            string source;
            await using (var stream = await ReadAsync(cancellationToken))
            using (var reader = new StreamReader(stream))
            {
                source = await reader.ReadToEndAsync(cancellationToken);
            }

            var index = source.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new ArgumentException(
                    $"The text to replace was not found in {Name}. It must match the Markdown source exactly, including whitespace and line breaks.",
                    nameof(oldText));
            }

            // Searching from index + 1 also finds an overlapping second occurrence, which makes the passage ambiguous as well.
            if (source.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException(
                    $"The text to replace occurs more than once in {Name}; include more of the surrounding text so it is unique.",
                    nameof(oldText));
            }

            var updated = string.Concat(source.AsSpan(0, index), newText, source.AsSpan(index + oldText.Length));
            await WriteUnlockedAsync(MarkdownWriteRules.GetContentBytes(updated, nameof(newText)), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteUnlockedAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await Storage.WriteBlobAsync(FullPath, stream, cancellationToken);
    }

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        return LoadFileAsync(cancellationToken);
    }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
        => Storage.ReadBlobAsync(FullPath, cancellationToken);

    public async Task WriteAsync(Stream content, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await Storage.WriteBlobAsync(FullPath, content, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Rebuild markdown with all embedded subjects serialized
            Content = RebuildMarkdownContent();
            await WriteUnlockedAsync(Encoding.UTF8.GetBytes(Content), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        return true;
    }

    private string RebuildMarkdownContent()
    {
        if (string.IsNullOrEmpty(Content))
        {
            return string.Empty;
        }

        // Regex matches: ```subject(name)\n{json}```
        return SubjectBlockRegex().Replace(
            Content,
            match =>
            {
                var name = match.Groups[1].Value;

                // Find the child subject by name and serialize it
                if (Children.TryGetValue(name, out var child))
                {
                    var json = _serializer.Serialize(child);
                    return $"```subject({name})\n{json}\n```";
                }

                // Subject not found - keep original block unchanged
                return match.Value;
            });
    }

    private static string FormatFilename(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        return string.Join(" ", baseName
            .Replace("-", " ")
            .Replace("_", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}
