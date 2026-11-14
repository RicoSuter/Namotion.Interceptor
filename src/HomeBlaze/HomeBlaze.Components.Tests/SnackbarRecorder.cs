using System.Collections.Concurrent;
using Moq;
using MudBlazor;

namespace HomeBlaze.Components.Tests;

/// <summary>
/// A snackbar that records the text messages it was given, from any thread.
/// </summary>
internal sealed class SnackbarRecorder
{
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly ConcurrentQueue<(string Message, Severity Severity)> _messages = new();

    public SnackbarRecorder()
    {
        _snackbar
            .Setup(snackbar => snackbar.Add(It.IsAny<string>(), It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()))
            .Callback((string message, Severity severity, Action<SnackbarOptions>? _, string? _) => _messages.Enqueue((message, severity)));
    }

    public ISnackbar Snackbar => _snackbar.Object;

    public IReadOnlyCollection<(string Message, Severity Severity)> Messages => _messages;
}
