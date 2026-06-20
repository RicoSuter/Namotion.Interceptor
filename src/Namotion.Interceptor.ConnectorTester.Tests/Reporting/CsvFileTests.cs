using Xunit;
using Namotion.Interceptor.ConnectorTester.Reporting;

namespace Namotion.Interceptor.ConnectorTester.Tests.Reporting;

public class CsvFileTests
{
    private record Sample(string Name, double Value, int Count);

    private static IReadOnlyList<CsvColumn<Sample>> SampleColumns =>
    [
        new() { Name = "Name",  Width = 10, Selector = sample => sample.Name },
        new() { Name = "Value", Width =  8, Format = "F1", Selector = sample => sample.Value },
        new() { Name = "Count", Width =  6, Selector = sample => sample.Count }
    ];

    [Fact]
    public void WhenHeaderWritten_ThenColumnsAreRightAlignedAndCommaSeparated()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = new CsvFile<Sample>(path, SampleColumns);

            // Act
            file.WriteHeader();
            file.Dispose();

            // Assert
            var content = File.ReadAllText(path);
            // Three columns, widths 10/8/6 right-aligned, separated by ", ".
            Assert.Equal($"      Name,    Value,  Count{Environment.NewLine}", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WhenRowAppended_ThenValuesAreFormattedAndAlignedPerColumn()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = new CsvFile<Sample>(path, SampleColumns);
            file.WriteHeader();

            // Act
            file.AppendRow(new Sample("alpha", 3.14159, 42));
            file.Dispose();

            // Assert
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal("     alpha,      3.1,     42", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WhenManyRowsAppended_ThenStreamIsFlushedAfterEachRow()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = new CsvFile<Sample>(path, SampleColumns);
            file.WriteHeader();

            // Act
            for (var i = 0; i < 5; i++)
            {
                file.AppendRow(new Sample($"row{i}", i, i));
                // After AppendRow returns, the row must be visible to a fresh reader.
                // On Windows, the cached writer holds an exclusive write lock; open the reader with FileShare.ReadWrite.
                string partialContent;
                using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(read))
                {
                    partialContent = reader.ReadToEnd();
                }
                Assert.Contains($"row{i}", partialContent);
            }

            // Assert: total 6 lines (header + 5 rows).
            file.Dispose();
            Assert.Equal(6, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WhenAppendRowCalledAfterDispose_ThenItThrows()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            var file = new CsvFile<Sample>(path, SampleColumns);
            file.WriteHeader();
            file.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => file.AppendRow(new Sample("x", 1, 1)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
