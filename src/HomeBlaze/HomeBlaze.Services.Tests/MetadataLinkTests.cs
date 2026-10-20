namespace HomeBlaze.Services.Tests;

public class MetadataLinkTests
{
    [Theory]
    [InlineData("https://example.com/manual.pdf", true)]
    [InlineData("http://192.168.1.10/status", true)]
    [InlineData("mailto:service@example.com", true)]
    [InlineData("HTTPS://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com/manual.pdf", false)]
    [InlineData("/Devices/Pump", false)]
    [InlineData("manual.pdf", false)]
    [InlineData("//example.com", false)]
    [InlineData("", false)]
    public void WhenAnAddressIsChecked_ThenOnlyAbsoluteHttpHttpsAndMailtoAddressesAreWebAddresses(string url, bool expected)
    {
        // Act
        var isWebAddress = MetadataLink.IsWebAddress(url);

        // Assert
        Assert.Equal(expected, isWebAddress);
    }
}
