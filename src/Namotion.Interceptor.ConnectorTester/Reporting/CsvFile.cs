using System.Globalization;
using System.Text;

namespace Namotion.Interceptor.ConnectorTester.Reporting;

/// <summary>
/// Column-based CSV writer with a cached StreamWriter (AutoFlush). Header and rows
/// share the same column definitions, so they cannot drift. WriteHeader truncates the
/// file; AppendRow appends and flushes immediately. Disposed once on host shutdown.
/// </summary>
public sealed class CsvFile<TRow> : IDisposable
{
    private readonly string _filePath;
    private readonly IReadOnlyList<CsvColumn<TRow>> _columns;
    private readonly string _headerLine;
    private readonly string _rowFormat;
    private readonly object _writerLock = new();

    private StreamWriter? _writer;
    private bool _disposed;

    public CsvFile(string filePath, IReadOnlyList<CsvColumn<TRow>> columns)
    {
        _filePath = filePath;
        _columns = columns;

        var headerBuilder = new StringBuilder();
        var rowFormatBuilder = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                headerBuilder.Append(", ");
                rowFormatBuilder.Append(", ");
            }
            headerBuilder.Append(string.Format(CultureInfo.InvariantCulture, $"{{0,{columns[i].Width}}}", columns[i].Name));
            rowFormatBuilder.Append(columns[i].Format is null
                ? $"{{{i},{columns[i].Width}}}"
                : $"{{{i},{columns[i].Width}:{columns[i].Format}}}");
        }
        _headerLine = headerBuilder.ToString();
        _rowFormat = rowFormatBuilder.ToString();
    }

    public void WriteHeader()
    {
        lock (_writerLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CsvFile<TRow>));
            _writer ??= CreateWriter(append: false);
            _writer.WriteLine(_headerLine);
        }
    }

    public void AppendRow(TRow row)
    {
        var values = new object?[_columns.Count];
        for (var i = 0; i < _columns.Count; i++)
        {
            values[i] = _columns[i].Selector(row);
        }
        var line = string.Format(CultureInfo.InvariantCulture, _rowFormat, values);

        lock (_writerLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CsvFile<TRow>));
            _writer ??= CreateWriter(append: true);
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_writerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter CreateWriter(bool append) => new(_filePath, append) { AutoFlush = true };
}
