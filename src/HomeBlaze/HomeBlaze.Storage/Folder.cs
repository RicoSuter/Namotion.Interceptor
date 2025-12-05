using HomeBlaze.Core.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Represents a folder within a storage container.
/// Inherits scanning and watching behavior from parent storage.
/// </summary>
[InterceptorSubject]
public partial class Folder : StorageContainer
{
    private FileSystemWatcher? _watcher;
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;
    private readonly ILogger? _logger;

    /// <summary>
    /// Full path to this folder.
    /// </summary>
    public partial string Path { get; set; }

    public Folder(
        SubjectTypeRegistry typeRegistry,
        SubjectSerializer serializer,
        ILogger? logger = null) : base(logger)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _logger = logger;
        Path = string.Empty;
    }

    public Folder(
        IInterceptorSubjectContext context,
        SubjectTypeRegistry typeRegistry,
        SubjectSerializer serializer,
        ILogger? logger = null) : base(context, logger)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _logger = logger;
        Path = string.Empty;
    }

    protected override async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new InvalidOperationException("Path is not configured");

        if (!Directory.Exists(Path))
            throw new DirectoryNotFoundException($"Directory not found: {Path}");

        _logger?.LogDebug("Scanning folder: {Path}", Path);

        var context = ((IInterceptorSubject)this).Context;
        var existingKeys = new HashSet<string>(Children.Keys);

        // Scan files
        foreach (var filePath in Directory.GetFiles(Path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = System.IO.Path.GetFileName(filePath);
            existingKeys.Remove(fileName);

            if (Children.ContainsKey(fileName))
                continue;

            var subject = await CreateSubjectForFileAsync(filePath, context, cancellationToken);
            if (subject != null)
            {
                AddChild(fileName, subject);
            }
        }

        // Scan subdirectories
        foreach (var subDirPath in Directory.GetDirectories(Path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirName = System.IO.Path.GetFileName(subDirPath);
            existingKeys.Remove(dirName);

            if (Children.ContainsKey(dirName))
                continue;

            var folder = new Folder(context, _typeRegistry, _serializer, _logger)
            {
                Path = subDirPath,
                Name = dirName
            };

            AddChild(dirName, folder);
        }

        // Remove items that no longer exist
        foreach (var key in existingKeys)
        {
            RemoveChild(key);
        }
    }

    private async Task<IInterceptorSubject?> CreateSubjectForFileAsync(
        string filePath,
        IInterceptorSubjectContext context,
        CancellationToken cancellationToken)
    {
        var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

        // JSON files - deserialize with Type discriminator
        if (extension == ".json")
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var subject = _serializer.Deserialize(json, context);
                return subject;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize JSON file: {FilePath}", filePath);
                return null;
            }
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            return CreateFileSubject(mappedType, filePath, context);
        }

        // Default to GenericFile
        return CreateFileSubject(typeof(GenericFile), filePath, context);
    }

    private IInterceptorSubject? CreateFileSubject(Type type, string filePath, IInterceptorSubjectContext context)
    {
        try
        {
            var ctor = type.GetConstructor(new[] { typeof(IInterceptorSubjectContext) });
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke(new object[] { context });
                SetFileProperties(subject, filePath);
                return subject;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create subject for file: {FilePath}", filePath);
        }

        return null;
    }

    private void SetFileProperties(IInterceptorSubject subject, string filePath)
    {
        var type = subject.GetType();
        var fileInfo = new FileInfo(filePath);

        SetPropertyIfExists(type, subject, "FilePath", filePath);
        SetPropertyIfExists(type, subject, "FileName", fileInfo.Name);
        SetPropertyIfExists(type, subject, "Extension", fileInfo.Extension);
        SetPropertyIfExists(type, subject, "FileSize", fileInfo.Length);
        SetPropertyIfExists(type, subject, "LastModified", fileInfo.LastWriteTimeUtc);
    }

    private static void SetPropertyIfExists(Type type, object target, string propertyName, object value)
    {
        var prop = type.GetProperty(propertyName);
        if (prop != null && prop.CanWrite)
        {
            try
            {
                prop.SetValue(target, value);
            }
            catch
            {
                // Ignore
            }
        }
    }

    protected override async Task WatchAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path))
            return;

        _watcher = new FileSystemWatcher(Path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileSystemChange;
        _watcher.Deleted += OnFileSystemChange;
        _watcher.Changed += OnFileSystemChange;
        _watcher.Renamed += OnFileSystemChange;

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileSystemChange(object sender, FileSystemEventArgs e)
    {
        Task.Run(async () =>
        {
            try
            {
                await ScanAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling file system change in folder {Path}", Path);
            }
        });
    }
}
