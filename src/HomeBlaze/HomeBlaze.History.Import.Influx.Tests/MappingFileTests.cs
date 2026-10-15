namespace HomeBlaze.History.Import.Influx.Tests;

public class MappingFileTests
{
    private const string Header = "enabled,confidence,measurement,field,path,type,divisor,values,note";

    [Fact]
    public void WhenRowsAreParsed_ThenColumnsAreMappedByHeaderName()
    {
        // Arrange
        var csv = Header + "\n" +
            "true,high,abc/temperatures/flow,Temperature,/House/HeatPump/Flow/Temperature,decimal,,,flow\n" +
            "false,medium,abc,TotalHeatingOperatingTime,/House/HeatPump/Heating/TotalOperatingHours,decimal,3600,,\"seconds, to hours\"\n";

        // Act
        var rows = MappingFile.Parse(new StringReader(csv));

        // Assert
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Enabled);
        Assert.Equal("abc/temperatures/flow", rows[0].Measurement);
        Assert.Equal("/House/HeatPump/Flow/Temperature", rows[0].Path);
        Assert.Equal(MappingValueType.Decimal, rows[0].Type);
        Assert.Equal(1m, rows[0].Divisor);
        Assert.False(rows[1].Enabled);
        Assert.Equal(3600m, rows[1].Divisor);
        Assert.Equal("seconds, to hours", rows[1].Note);
    }

    [Fact]
    public void WhenValuesColumnHasFallback_ThenMapAndFallbackAreParsed()
    {
        // Arrange
        var csv = Header + "\n" +
            "true,high,m,State,/a/ShutterState,string,,Open=Open;Closed=Closed;*=Unknown,\n";

        // Act
        var row = Assert.Single(MappingFile.Parse(new StringReader(csv)));

        // Assert
        Assert.Equal("Open", row.Values!["Open"]);
        Assert.Equal("Closed", row.Values["Closed"]);
        Assert.Equal("Unknown", row.FallbackValue);
    }

    [Fact]
    public void WhenQuotedFieldContainsQuote_ThenItIsUnescaped()
    {
        // Arrange
        var csv = Header + "\n" + "true,high,m,f,/a/b,string,,,\"say \"\"hi\"\"\"\n";

        // Act
        var row = Assert.Single(MappingFile.Parse(new StringReader(csv)));

        // Assert
        Assert.Equal("say \"hi\"", row.Note);
    }

    [Fact]
    public void WhenTypeIsUnknown_ThenParseThrowsWithLineNumber()
    {
        // Arrange
        var csv = Header + "\n" + "true,high,m,f,/a/b,float,,,\n";

        // Act & Assert
        var exception = Assert.Throws<FormatException>(() => MappingFile.Parse(new StringReader(csv)));
        Assert.Contains("line 2", exception.Message);
    }
}
