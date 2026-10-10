using System.Text;
using System.Text.Json;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The agent-owned memory document, included in the system prompt of every turn.
/// </summary>
public sealed class AgentMemory
{
    /// <summary>The maximum size of the memory document in UTF-8 bytes.</summary>
    public const int MaxBytes = 8192;

    private static readonly JsonElement UpdateToolSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            content = new { type = "string", description = "The complete new memory document in markdown. Replaces the current one." }
        },
        required = new[] { "content" }
    });

    private readonly string? _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile Snapshot _snapshot = new(string.Empty, null);

    /// <param name="filePath">The markdown file, or null to keep memory in-process only.</param>
    /// <param name="timeProvider">The clock for the last update time.</param>
    public AgentMemory(string? filePath, TimeProvider timeProvider)
    {
        _filePath = filePath;
        _timeProvider = timeProvider;
    }

    /// <summary>The current memory document.</summary>
    public string Content => _snapshot.Content;

    /// <summary>When the memory was last written, null if never.</summary>
    public DateTimeOffset? LastUpdated => _snapshot.LastUpdated;

    /// <summary>
    /// Loads the memory file if it exists.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(_filePath, cancellationToken);
        _snapshot = new Snapshot(content, File.GetLastWriteTimeUtc(_filePath));
    }

    /// <summary>
    /// Replaces the memory document. Returns false, leaving the memory unchanged, when the content exceeds <see cref="MaxBytes"/>.
    /// </summary>
    public async Task<bool> TryWriteAsync(string content, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_filePath is not null)
            {
                await AtomicFile.WriteAllTextAsync(_filePath, content, cancellationToken);
            }

            _snapshot = new Snapshot(content, _timeProvider.GetUtcNow());
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Creates the agent-local <c>update_memory</c> tool.
    /// </summary>
    public McpToolInfo CreateUpdateTool() => new()
    {
        Name = "update_memory",
        Description = "Replace your memory document. Your memory is included in every future run, so record what you must remember: alerts already sent, trends, open issues. Keep it under 8 KB.",
        InputSchema = UpdateToolSchema,
        Handler = async (input, cancellationToken) =>
        {
            if (!input.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
            {
                return new { error = "The 'content' parameter is required." };
            }

            var written = await TryWriteAsync(contentElement.GetString()!, cancellationToken);
            return written
                ? new { success = true }
                : (object)new { error = $"The memory exceeds {MaxBytes} bytes. Please condense it and call update_memory again." };
        }
    };

    private sealed record Snapshot(string Content, DateTimeOffset? LastUpdated);
}
