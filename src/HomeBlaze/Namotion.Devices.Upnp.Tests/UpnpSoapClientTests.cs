using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Namotion.Devices.Upnp.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Upnp.Tests;

public class UpnpSoapClientTests
{
    private const string EnvelopeStart =
        "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

    private const string AnswerStart =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>";

    private static readonly UpnpService AvTransport = new("urn:schemas-upnp-org:service:AVTransport:1", "/MediaRenderer/AVTransport/Control");
    private static readonly UpnpService RenderingControl = new("urn:schemas-upnp-org:service:RenderingControl:1", "/MediaRenderer/RenderingControl/Control");
    private static readonly UpnpService DeviceProperties = new("urn:schemas-upnp-org:service:DeviceProperties:1", "/DeviceProperties/Control");

    [Fact]
    public void WhenAnActionHasNoArguments_ThenTheEnvelopeHoldsAnEmptyActionElement()
    {
        // Act
        var envelope = UpnpSoapClient.CreateEnvelope(DeviceProperties, "GetZoneInfo", []);

        // Assert
        Assert.Equal(
            EnvelopeStart + "<u:GetZoneInfo xmlns:u=\"urn:schemas-upnp-org:service:DeviceProperties:1\" />" + EnvelopeEnd,
            envelope);
    }

    [Fact]
    public void WhenAnActionHasArguments_ThenTheEnvelopeListsThemInOrderAndWritesAnEmptyOneAsAnEmptyElement()
    {
        // Act
        var envelope = UpnpSoapClient.CreateEnvelope(
            AvTransport,
            "SetAVTransportURI",
            [("InstanceID", "0"), ("CurrentURI", "x-rincon:RINCON_1"), ("CurrentURIMetaData", string.Empty)]);

        // Assert
        Assert.Equal(
            EnvelopeStart +
            "<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<InstanceID>0</InstanceID><CurrentURI>x-rincon:RINCON_1</CurrentURI><CurrentURIMetaData />" +
            "</u:SetAVTransportURI>" + EnvelopeEnd,
            envelope);
    }

    [Fact]
    public void WhenAValueHoldsMarkup_ThenItIsEscapedOnceAndReadsBackUnchanged()
    {
        // Arrange
        const string metadata = """<item id="1"><title>Tom & Jerry's "best" <3</title></item>""";

        // Act
        var envelope = UpnpSoapClient.CreateEnvelope(AvTransport, "SetAVTransportURI", [("CurrentURIMetaData", metadata)]);

        // Assert
        Assert.Contains(
            "<CurrentURIMetaData>&lt;item id=&quot;1&quot;&gt;&lt;title&gt;Tom &amp; Jerry's &quot;best&quot; &lt;3&lt;/title&gt;&lt;/item&gt;</CurrentURIMetaData>",
            envelope);
        Assert.Equal(metadata, XDocument.Parse(envelope).Descendants("CurrentURIMetaData").Single().Value);
    }

    [Fact]
    public async Task WhenInvokingAnAction_ThenThePostNamesTheQuotedActionAndCarriesTheEnvelopeWithoutAByteOrderMark()
    {
        // Arrange
        string? method = null;
        string? path = null;
        string? soapAction = null;
        string? contentType = null;
        byte[] body = [];
        await using var device = new LoopbackHttpServer(async context =>
        {
            method = context.Request.HttpMethod;
            path = context.Request.Url!.AbsolutePath;
            soapAction = context.Request.Headers["SOAPACTION"];
            contentType = context.Request.ContentType;
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            body = buffer.ToArray();
            await WriteAsync(context.Response, HttpStatusCode.OK, AnswerStart + "<u:PauseResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\" />" + EnvelopeEnd);
        });
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        await client.InvokeAsync(AvTransport, "Pause", [("InstanceID", "0")], CancellationToken.None);

        // Assert
        Assert.Equal("POST", method);
        Assert.Equal("/MediaRenderer/AVTransport/Control", path);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#Pause\"", soapAction);
        Assert.Equal("text/xml; charset=\"utf-8\"", contentType);
        Assert.Equal(
            EnvelopeStart + "<u:Pause xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID></u:Pause>" + EnvelopeEnd,
            Encoding.UTF8.GetString(body));
        Assert.False(body.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public async Task WhenTheDeviceAnswers_ThenValuesAreReadByNameAndAMissingOrUnreadableOneIsNull()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.OK,
            AnswerStart +
            "<u:GetThingsResponse xmlns:u=\"urn:schemas-upnp-org:service:RenderingControl:1\">" +
            "<Name>K&amp;uuml;che &lt;1&gt;</Name><Empty></Empty><Volume>44</Volume><Negative>-3</Negative><NotANumber>loud</NotANumber>" +
            "<Large>4294967296</Large>" +
            "<One>1</One><Zero>0</Zero><True>true</True><False>false</False><Maybe>maybe</Maybe>" +
            "<Yes>yes</Yes><No>NO</No><TrueUpper>TRUE</TrueUpper>" +
            "<PaddedNumber> 7 </PaddedNumber><PaddedTrue> 1 </PaddedTrue><Nested><Inner>5</Inner></Nested>" +
            "<Split>a<!-- c -->b</Split><Section><![CDATA[<x>]]></Section><u:Prefixed>p</u:Prefixed><Last>z</Last>" +
            "</u:GetThingsResponse>" + EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var response = await client.QueryAsync(RenderingControl, "GetThings", [], CancellationToken.None);

        // Assert
        Assert.Equal("K&uuml;che <1>", response.GetString("Name"));
        Assert.Equal(string.Empty, response.GetString("Empty"));
        Assert.Null(response.GetString("Missing"));
        Assert.Equal(44, response.GetInt32("Volume"));
        Assert.Equal(-3, response.GetInt32("Negative"));
        Assert.Null(response.GetInt32("NotANumber"));
        Assert.Null(response.GetInt32("Empty"));
        Assert.Null(response.GetInt32("Missing"));
        Assert.Null(response.GetInt32("Large"));
        Assert.Equal(4294967296L, response.GetInt64("Large"));
        Assert.Null(response.GetInt64("NotANumber"));
        Assert.True(response.GetBoolean("One"));
        Assert.False(response.GetBoolean("Zero"));
        Assert.True(response.GetBoolean("True"));
        Assert.False(response.GetBoolean("False"));
        Assert.Null(response.GetBoolean("Maybe"));
        Assert.Null(response.GetBoolean("Missing"));
        Assert.True(response.GetBoolean("Yes"));
        Assert.False(response.GetBoolean("No"));
        Assert.True(response.GetBoolean("TrueUpper"));
        Assert.Equal(7, response.GetInt32("PaddedNumber"));
        Assert.True(response.GetBoolean("PaddedTrue"));
        Assert.Null(response.GetString("Nested"));
        Assert.Equal("ab", response.GetString("Split"));
        Assert.Equal("<x>", response.GetString("Section"));
        Assert.Equal("p", response.GetString("Prefixed"));
        Assert.Equal("z", response.GetString("Last"));
    }

    [Fact]
    public async Task WhenAValueIsWhitespaceOrHasSurroundingSpaces_ThenItIsReturnedAsSent()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.OK,
            AnswerStart +
            "<u:GetNamesResponse xmlns:u=\"urn:schemas-upnp-org:service:DeviceProperties:1\">" +
            "<Blank> </Blank><Padded> x </Padded>" +
            "</u:GetNamesResponse>" + EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var response = await client.QueryAsync(DeviceProperties, "GetNames", [], CancellationToken.None);

        // Assert
        Assert.Equal(" ", response.GetString("Blank"));
        Assert.Equal(" x ", response.GetString("Padded"));
    }

    [Fact]
    public async Task WhenTheAnswerIsIndentedWithAHeaderBeforeTheBody_ThenQueryingReadsItsValues()
    {
        // Arrange: the kind of multi-line, indented SOAP with a header a real device sends.
        await using var device = Answer(
            HttpStatusCode.OK,
            "<?xml version=\"1.0\"?>\n" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n" +
            "  <s:Header>\n" +
            "    <SessionId>abc</SessionId>\n" +
            "  </s:Header>\n" +
            "  <s:Body>\n" +
            "    <u:GetVolumeResponse xmlns:u=\"urn:schemas-upnp-org:service:RenderingControl:1\">\n" +
            "      <CurrentVolume>50</CurrentVolume>\n" +
            "    </u:GetVolumeResponse>\n" +
            "  </s:Body>\n" +
            "</s:Envelope>\n");
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var response = await client.QueryAsync(RenderingControl, "GetVolume", [], CancellationToken.None);

        // Assert
        Assert.Equal(50, response.GetInt32("CurrentVolume"));
    }

    [Fact]
    public async Task WhenTheCharsetOfTheAnswerHeaderIsWrong_ThenTheTextIsStillReadFromTheDocument()
    {
        // Arrange
        await using var device = new LoopbackHttpServer(context => WriteAsync(
            context.Response,
            HttpStatusCode.OK,
            AnswerStart + "<u:GetNameResponse xmlns:u=\"urn:schemas-upnp-org:service:DeviceProperties:1\"><Name>Küche</Name></u:GetNameResponse>" + EnvelopeEnd,
            "text/xml; charset=iso-8859-1"));
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var response = await client.QueryAsync(DeviceProperties, "GetName", [], CancellationToken.None);

        // Assert
        Assert.Equal("Küche", response.GetString("Name"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.OK, "done")]
    public async Task WhenACommandIsAnsweredWithoutAnEnvelope_ThenItStillSucceeds(HttpStatusCode status, string body)
    {
        // Arrange
        await using var device = Answer(status, body);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.InvokeAsync(AvTransport, "Next", [("InstanceID", "0")], CancellationToken.None));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenTheDeviceAnswersWithAFault_ThenTheExceptionNamesTheServiceTheActionAndTheErrorCode()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.InternalServerError,
            AnswerStart +
            "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
            "<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>701</errorCode></UPnPError></detail></s:Fault>" +
            EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<UpnpFaultException>(
            () => client.InvokeAsync(AvTransport, "Next", [("InstanceID", "0")], CancellationToken.None));

        // Assert
        Assert.Equal(AvTransport.ServiceType, exception.ServiceType);
        Assert.Equal("Next", exception.Action);
        Assert.Equal(701, exception.ErrorCode);
        Assert.Equal("UPnPError", exception.FaultText);
        Assert.Equal("The device refused Next (UPnP error 701).", exception.Message);
    }

    [Fact]
    public async Task WhenACommandIsAnsweredWithAFaultAndASuccessStatus_ThenInvokingThrowsTheFault()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.OK,
            AnswerStart +
            "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
            "<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>402</errorCode></UPnPError></detail></s:Fault>" +
            EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<UpnpFaultException>(
            () => client.InvokeAsync(AvTransport, "Next", [("InstanceID", "0")], CancellationToken.None));

        // Assert
        Assert.Equal(402, exception.ErrorCode);
    }

    [Fact]
    public async Task WhenTheAnswerIsIndentedWithAHeaderAndCarriesAFaultWithASuccessStatus_ThenInvokingThrowsTheFault()
    {
        // Arrange: the kind of multi-line, indented SOAP with a header a real device sends.
        await using var device = Answer(
            HttpStatusCode.OK,
            "<?xml version=\"1.0\"?>\n" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\n" +
            "  <s:Header>\n" +
            "    <SessionId>abc</SessionId>\n" +
            "  </s:Header>\n" +
            "  <s:Body>\n" +
            "    <s:Fault>\n" +
            "      <faultcode>s:Client</faultcode>\n" +
            "      <faultstring>UPnPError</faultstring>\n" +
            "      <detail>\n" +
            "        <UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">\n" +
            "          <errorCode>402</errorCode>\n" +
            "        </UPnPError>\n" +
            "      </detail>\n" +
            "    </s:Fault>\n" +
            "  </s:Body>\n" +
            "</s:Envelope>\n");
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<UpnpFaultException>(
            () => client.InvokeAsync(AvTransport, "Play", [], CancellationToken.None));

        // Assert
        Assert.Equal(402, exception.ErrorCode);
    }

    [Theory]
    [InlineData(401, "invalid action")]
    [InlineData(402, "invalid arguments")]
    [InlineData(501, "action failed")]
    [InlineData(600, "argument value invalid")]
    [InlineData(601, "argument value out of range")]
    [InlineData(602, "optional action not implemented")]
    [InlineData(603, "out of memory")]
    [InlineData(604, "human intervention required")]
    [InlineData(605, "string argument too long")]
    [InlineData(606, "action not authorized")]
    public async Task WhenTheFaultCodeIsStandard_ThenTheMessageDescribesIt(int code, string description)
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.InternalServerError,
            AnswerStart +
            "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
            $"<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code}</errorCode></UPnPError></detail></s:Fault>" +
            EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<UpnpFaultException>(
            () => client.InvokeAsync(AvTransport, "Play", [], CancellationToken.None));

        // Assert
        Assert.Equal($"The device refused Play: {description} (UPnP error {code}).", exception.Message);
    }

    [Fact]
    public async Task WhenAFaultCarriesNoErrorCode_ThenTheExceptionHoldsTheFaultText()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.InternalServerError,
            AnswerStart + "<s:Fault><faultcode>s:Client</faultcode><faultstring>Busy</faultstring></s:Fault>" + EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<UpnpFaultException>(
            () => client.InvokeAsync(AvTransport, "Play", [], CancellationToken.None));

        // Assert
        Assert.Null(exception.ErrorCode);
        Assert.Equal("The device refused Play: Busy.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Internal Server Error</body></html>")]
    [InlineData("Internal Server Error")]
    public async Task WhenTheDeviceAnswersAnErrorThatIsNoFault_ThenTheRequestFailsWithItsStatus(string body)
    {
        // Arrange
        await using var device = Answer(HttpStatusCode.InternalServerError, body);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.InvokeAsync(AvTransport, "Browse", [], CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("Browse", exception.Message);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<Envelope><Body><GetTransportInfoResponse><CurrentTransportState>PLAYING</CurrentTransportState></GetTransportInfoResponse></Body></Envelope>")]
    [InlineData("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body /></s:Envelope>")]
    [InlineData("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetMediaInfoResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><CurrentURI>x</CurrentURI></u:GetMediaInfoResponse></s:Body></s:Envelope>")]
    public async Task WhenTheAnswerIsNotTheSoapAnswerToTheAction_ThenQueryingThrowsXmlException(string body)
    {
        // Arrange
        await using var device = Answer(HttpStatusCode.OK, body);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act & Assert
        await Assert.ThrowsAsync<XmlException>(
            () => client.QueryAsync(AvTransport, "GetTransportInfo", [], CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheAnswerDeclaresADocumentType_ThenQueryingThrowsXmlException()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.OK,
            "<!DOCTYPE s [<!ENTITY e \"x\">]>" + AnswerStart +
            "<u:GetTransportInfoResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><CurrentTransportState>PLAYING</CurrentTransportState></u:GetTransportInfoResponse>" +
            EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act & Assert
        await Assert.ThrowsAsync<XmlException>(
            () => client.QueryAsync(AvTransport, "GetTransportInfo", [], CancellationToken.None));
    }

    [Fact]
    public async Task WhenAValueOfTheAnswerIsNestedVeryDeeply_ThenItIsUnknownAndTheValueAfterItIsRead()
    {
        // Arrange
        const int depth = 100_000;
        var nested = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));
        await using var device = Answer(
            HttpStatusCode.OK,
            AnswerStart + "<u:GetNameResponse xmlns:u=\"urn:schemas-upnp-org:service:DeviceProperties:1\"><Name>" + nested + "</Name><Room>Küche</Room></u:GetNameResponse>" + EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var response = await client.QueryAsync(DeviceProperties, "GetName", [], CancellationToken.None);

        // Assert
        Assert.Null(response.GetString("Name"));
        Assert.Equal("Küche", response.GetString("Room"));
    }

    [Fact]
    public async Task WhenAReadIsAnsweredWithAFaultAndASuccessStatus_ThenQueryingThrowsTheFault()
    {
        // Arrange
        await using var device = Answer(
            HttpStatusCode.OK,
            AnswerStart +
            "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
            "<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>402</errorCode></UPnPError></detail></s:Fault>" +
            EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new UpnpSoapClient(httpClient, device.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<UpnpFaultException>(
            () => client.QueryAsync(RenderingControl, "GetVolume", [], CancellationToken.None));

        // Assert
        Assert.Equal(402, exception.ErrorCode);
    }

    [Fact]
    public void WhenTheHttpClientIsNull_ThenConstructingTheClientThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UpnpSoapClient(null!, new Uri("http://localhost/")));
    }

    [Fact]
    public void WhenTheBaseUriIsNull_ThenConstructingTheClientThrowsArgumentNullException()
    {
        // Arrange
        using var httpClient = new HttpClient();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UpnpSoapClient(httpClient, null!));
    }

    [Fact]
    public void WhenTheServiceTypeIsNull_ThenConstructingTheServiceThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UpnpService(null!, "/path"));
    }

    [Fact]
    public void WhenTheControlPathIsNull_ThenConstructingTheServiceThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UpnpService("urn:schemas-upnp-org:service:AVTransport:1", null!));
    }

    [Fact]
    public void WhenTheFaultServiceTypeIsNull_ThenConstructingTheExceptionThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UpnpFaultException(null!, "Play", null, null));
    }

    [Fact]
    public void WhenTheFaultActionIsNull_ThenConstructingTheExceptionThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new UpnpFaultException("urn:schemas-upnp-org:service:AVTransport:1", null!, null, null));
    }

    private static LoopbackHttpServer Answer(HttpStatusCode status, string body) =>
        new(context => WriteAsync(context.Response, status, body));

    private static async Task WriteAsync(
        HttpListenerResponse response, HttpStatusCode status, string body, string contentType = "text/xml; charset=\"utf-8\"")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
