using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Counts the render batches that the server sends to a page and records the versions of the model of a Monaco editor whose
/// text the server decorated. The server sends a render batch for every render, and calls update of the module of the editor
/// once it decorated the text of a version of the editor model. Create it before the page navigates.
/// </summary>
internal sealed class RenderBatchRecorder
{
    private readonly IPage _page;
    private readonly string _editorId;
    private readonly ConcurrentQueue<string> _decoratedVersions = new();
    private int _renderBatchCount;

    public RenderBatchRecorder(IPage page, string editorId)
    {
        _page = page;
        _editorId = editorId;
        var update = new Regex($"update.{{0,8}}\\[\"{Regex.Escape(editorId)}\",(\\d+),");
        page.WebSocket += (_, webSocket) => webSocket.FrameReceived += (_, frame) =>
        {
            var text = frame.Binary is { } binary ? Encoding.Latin1.GetString(binary) : frame.Text ?? string.Empty;
            Interlocked.Add(ref _renderBatchCount, Regex.Count(text, "JS\\.RenderBatch"));
            foreach (Match match in update.Matches(text))
            {
                _decoratedVersions.Enqueue(match.Groups[1].Value);
            }
        };
    }

    /// <summary>The number of render batches the page received so far.</summary>
    public int RenderBatchCount => Volatile.Read(ref _renderBatchCount);

    /// <summary>
    /// Waits until the server decorated the text of the current version of the editor model, after it handled every change.
    /// </summary>
    public async Task WaitForDecoratedVersionAsync()
    {
        var version = (await _page.EvaluateAsync<int>($"() => blazorMonaco.editor.getEditor('{_editorId}').getModel().getVersionId()"))
            .ToString(CultureInfo.InvariantCulture);
        await AsyncTestHelpers.WaitUntilAsync(() => _decoratedVersions.Contains(version), message: $"The server did not decorate version {version}.");
    }
}
