namespace HomeBlaze.History.Import.Influx.Tests;

public class FluxCsvReaderTests
{
    [Fact]
    public void WhenResponseHasSeveralTables_ThenAllPointsAreRead()
    {
        // Arrange
        var csv =
            ",result,table,_time,_value\r\n" +
            ",_result,0,2023-08-06T18:08:05.4924881Z,1.5\r\n" +
            ",_result,0,2023-08-06T18:08:06Z,\"a, \"\"quoted\"\"\nvalue\"\r\n" +
            "\r\n" +
            ",result,table,_value,_time\r\n" +
            ",_result,1,3,2024-01-01T00:00:00.123456789Z\r\n";

        // Act
        var points = FluxCsvReader.Read(new StringReader(csv), CancellationToken.None).ToList();

        // Assert
        Assert.Equal(3, points.Count);
        Assert.Equal(new DateTimeOffset(2023, 8, 6, 18, 8, 5, TimeSpan.Zero).AddTicks(4924881), points[0].Timestamp);
        Assert.Equal("1.5", points[0].Value);
        Assert.Equal("a, \"quoted\"\nvalue", points[1].Value);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567), points[2].Timestamp);
        Assert.Equal("3", points[2].Value);
    }

    [Fact]
    public void WhenResponseCarriesAnError_ThenReadThrows()
    {
        // Arrange
        var csv = ",error,reference\r\n,query exceeded limit,897\r\n";

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            FluxCsvReader.Read(new StringReader(csv), CancellationToken.None).ToList());
    }
}
