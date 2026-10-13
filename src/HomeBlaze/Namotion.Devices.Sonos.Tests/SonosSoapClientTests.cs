using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSoapClientTests
{
    private const string EnvelopeStart =
        "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

    private const string AnswerStart =
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>";

    [Fact]
    public void WhenAnActionHasNoArguments_ThenTheEnvelopeHoldsAnEmptyActionElement()
    {
        // Act
        var envelope = SonosSoapClient.CreateEnvelope(SonosService.DeviceProperties, "GetZoneInfo", []);

        // Assert
        Assert.Equal(
            EnvelopeStart + "<u:GetZoneInfo xmlns:u=\"urn:schemas-upnp-org:service:DeviceProperties:1\" />" + EnvelopeEnd,
            envelope);
    }

    [Fact]
    public void WhenAnActionHasArguments_ThenTheEnvelopeListsThemInOrderAndWritesAnEmptyOneAsAnEmptyElement()
    {
        // Act
        var envelope = SonosSoapClient.CreateEnvelope(
            SonosService.AvTransport,
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
        var envelope = SonosSoapClient.CreateEnvelope(SonosService.AvTransport, "SetAVTransportURI", [("CurrentURIMetaData", metadata)]);

        // Assert
        Assert.Contains(
            "<CurrentURIMetaData>&lt;item id=&quot;1&quot;&gt;&lt;title&gt;Tom &amp; Jerry's &quot;best&quot; &lt;3&lt;/title&gt;&lt;/item&gt;</CurrentURIMetaData>",
            envelope);
        Assert.Equal(metadata, XDocument.Parse(envelope).Descendants("CurrentURIMetaData").Single().Value);
    }

    [Fact]
    public async Task WhenInvokingAnAction_ThenThePostNamesTheActionAndCarriesTheEnvelopeWithoutAByteOrderMark()
    {
        // Arrange
        string? path = null;
        string? soapAction = null;
        string? contentType = null;
        byte[] body = [];
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            path = context.Request.Url!.AbsolutePath;
            soapAction = context.Request.Headers["SOAPACTION"];
            contentType = context.Request.ContentType;
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            body = buffer.ToArray();
            await WriteAsync(context.Response, HttpStatusCode.OK, AnswerStart + "<u:PauseResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\" />" + EnvelopeEnd);
        });
        using var httpClient = new HttpClient();
        var client = new SonosSoapClient(httpClient, speaker.BaseUri);

        // Act
        await client.InvokeAsync(SonosService.AvTransport, "Pause", [("InstanceID", "0")], CancellationToken.None);

        // Assert
        Assert.Equal("/MediaRenderer/AVTransport/Control", path);
        Assert.Equal("urn:schemas-upnp-org:service:AVTransport:1#Pause", soapAction);
        Assert.Equal("text/xml; charset=\"utf-8\"", contentType);
        Assert.Equal(
            EnvelopeStart + "<u:Pause xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID></u:Pause>" + EnvelopeEnd,
            Encoding.UTF8.GetString(body));
        Assert.False(body.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public async Task WhenTheSpeakerAnswers_ThenValuesAreReadByNameAndAMissingOrUnreadableOneIsNull()
    {
        // Arrange
        await using var speaker = Answer(
            HttpStatusCode.OK,
            AnswerStart +
            "<u:GetThingsResponse xmlns:u=\"urn:schemas-upnp-org:service:RenderingControl:1\">" +
            "<Name>K&amp;uuml;che &lt;1&gt;</Name><Empty></Empty><Volume>44</Volume><Negative>-3</Negative><NotANumber>loud</NotANumber>" +
            "<One>1</One><Zero>0</Zero><True>true</True><False>false</False><Maybe>maybe</Maybe>" +
            "</u:GetThingsResponse>" + EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new SonosSoapClient(httpClient, speaker.BaseUri);

        // Act
        var response = await client.InvokeAsync(SonosService.RenderingControl, "GetThings", [], CancellationToken.None);

        // Assert
        Assert.Equal("K&uuml;che <1>", response.GetString("Name"));
        Assert.Equal(string.Empty, response.GetString("Empty"));
        Assert.Null(response.GetString("Missing"));
        Assert.Equal(44, response.GetInt32("Volume"));
        Assert.Equal(-3, response.GetInt32("Negative"));
        Assert.Null(response.GetInt32("NotANumber"));
        Assert.Null(response.GetInt32("Empty"));
        Assert.Null(response.GetInt32("Missing"));
        Assert.True(response.GetBoolean("One"));
        Assert.False(response.GetBoolean("Zero"));
        Assert.True(response.GetBoolean("True"));
        Assert.False(response.GetBoolean("False"));
        Assert.Null(response.GetBoolean("Maybe"));
        Assert.Null(response.GetBoolean("Missing"));
    }

    [Fact]
    public async Task WhenTheSpeakerAnswersWithAFault_ThenTheExceptionNamesTheActionAndTheErrorCode()
    {
        // Arrange
        await using var speaker = Answer(
            HttpStatusCode.InternalServerError,
            AnswerStart +
            "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
            "<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>701</errorCode></UPnPError></detail></s:Fault>" +
            EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new SonosSoapClient(httpClient, speaker.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<SonosFaultException>(
            () => client.InvokeAsync(SonosService.AvTransport, "Next", [("InstanceID", "0")], CancellationToken.None));

        // Assert
        Assert.Equal("Next", exception.Action);
        Assert.Equal(701, exception.UpnpErrorCode);
        Assert.Equal("The Sonos speaker refused Next: not available for the current source or state (UPnP error 701).", exception.Message);
    }

    [Fact]
    public async Task WhenAFaultCarriesNoErrorCode_ThenTheExceptionHoldsTheFaultText()
    {
        // Arrange
        await using var speaker = Answer(
            HttpStatusCode.InternalServerError,
            AnswerStart + "<s:Fault><faultcode>s:Client</faultcode><faultstring>Busy</faultstring></s:Fault>" + EnvelopeEnd);
        using var httpClient = new HttpClient();
        var client = new SonosSoapClient(httpClient, speaker.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<SonosFaultException>(
            () => client.InvokeAsync(SonosService.AvTransport, "Play", [], CancellationToken.None));

        // Assert
        Assert.Null(exception.UpnpErrorCode);
        Assert.Equal("The Sonos speaker refused Play: Busy.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Internal Server Error</body></html>")]
    [InlineData("Internal Server Error")]
    public async Task WhenTheSpeakerAnswersAnErrorThatIsNoFault_ThenTheRequestFailsWithItsStatus(string body)
    {
        // Arrange
        await using var speaker = Answer(HttpStatusCode.InternalServerError, body);
        using var httpClient = new HttpClient();
        var client = new SonosSoapClient(httpClient, speaker.BaseUri);

        // Act
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.InvokeAsync(SonosService.ContentDirectory, "Browse", [], CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("Browse", exception.Message);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<Envelope><Body /></Envelope>")]
    [InlineData("<!DOCTYPE s [<!ENTITY e \"x\">]><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><r>&e;</r></s:Body></s:Envelope>")]
    public async Task WhenTheAnswerIsNoSoapEnvelope_ThenInvokingThrowsXmlException(string body)
    {
        // Arrange
        await using var speaker = Answer(HttpStatusCode.OK, body);
        using var httpClient = new HttpClient();
        var client = new SonosSoapClient(httpClient, speaker.BaseUri);

        // Act & Assert
        await Assert.ThrowsAsync<XmlException>(
            () => client.InvokeAsync(SonosService.AvTransport, "GetTransportInfo", [], CancellationToken.None));
    }

    [Fact]
    public void WhenCreatingAnAudioClipCommand_ThenItNamesThePlayerTheSoundAndTheVolume()
    {
        // Act
        var command = SonosAudioClipClient.CreateCommand("RINCON_1", new Uri("http://host/a b.mp3?x=1&y=2"), 30);

        // Assert
        using var document = JsonDocument.Parse(command);
        var header = document.RootElement[0];
        var clip = document.RootElement[1];
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal("audioClip:1", header.GetProperty("namespace").GetString());
        Assert.Equal("loadAudioClip", header.GetProperty("command").GetString());
        Assert.Equal("RINCON_1", header.GetProperty("playerId").GetString());
        Assert.Equal("http://host/a%20b.mp3?x=1&y=2", clip.GetProperty("streamUrl").GetString());
        Assert.Equal(30, clip.GetProperty("volume").GetInt32());
        Assert.False(string.IsNullOrEmpty(clip.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(clip.GetProperty("appId").GetString()));
    }

    private static LoopbackHttpServer Answer(HttpStatusCode status, string body) =>
        new(context => WriteAsync(context.Response, status, body));

    private static async Task WriteAsync(HttpListenerResponse response, HttpStatusCode status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = "text/xml; charset=\"utf-8\"";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
