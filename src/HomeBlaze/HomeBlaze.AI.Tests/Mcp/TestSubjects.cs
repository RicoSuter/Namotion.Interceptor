using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.AI.Tests.Mcp;

[InterceptorSubject]
public partial class TestThing : ITitleProvider, IIconProvider
{
    public string? Title => Name;
    public string? IconName => "Lightbulb";

    [State]
    public partial string Name { get; set; }

    [State]
    public partial decimal Temperature { get; set; }

    public partial TestChildThing? Child { get; set; }
}

[InterceptorSubject]
public partial class TestChildThing : ITitleProvider
{
    public string? Title => ChildName;

    [State]
    public partial string ChildName { get; set; }

    [State]
    public partial bool IsActive { get; set; }
}

[InterceptorSubject]
public partial class TestFolder : ITitleProvider
{
    public string? Title => Name;

    [State]
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestFolder> Children { get; set; }

    public TestFolder()
    {
        Children = new Dictionary<string, TestFolder>();
    }
}

public interface ITestSensorInterface
{
    decimal Temperature { get; }
}

[InterceptorSubject]
public partial class TestStorageFolder
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public TestStorageFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}

[InterceptorSubject]
public partial class TestStorageFile : IStorageFile
{
    public string Name { get; init; } = "";

    public string FullPath => Name;

    public IStorageContainer Storage => throw new NotSupportedException();

    public long FileSize { get; set; }

    public DateTime LastModified { get; set; }

    /// <summary>The bytes <see cref="ReadAsync"/> returns.</summary>
    public byte[] Content { get; init; } = [];

    /// <summary>Whether the returned stream can seek, so its length is known.</summary>
    public bool IsSeekable { get; init; } = true;

    /// <summary>When set, <see cref="ReadAsync"/> throws it.</summary>
    public Exception? ReadException { get; init; }

    /// <summary>The bytes read from the last returned stream.</summary>
    public long BytesRead => _lastStream?.BytesRead ?? 0;

    private CountingStream? _lastStream;

    public Task<Stream> ReadAsync(CancellationToken cancellationToken)
    {
        if (ReadException is not null)
        {
            throw ReadException;
        }

        _lastStream = new CountingStream(new MemoryStream(Content), IsSeekable);
        return Task.FromResult<Stream>(_lastStream);
    }

    public Task WriteAsync(Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task OnFileChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Counts the bytes read through it and can hide that the inner stream is seekable.
/// </summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _isSeekable;

    public CountingStream(Stream inner, bool isSeekable)
    {
        _inner = inner;
        _isSeekable = isSeekable;
    }

    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => _isSeekable;
    public override bool CanWrite => false;
    public override long Length => _isSeekable ? _inner.Length : throw new NotSupportedException();

    public override long Position
    {
        get => _isSeekable ? _inner.Position : throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
