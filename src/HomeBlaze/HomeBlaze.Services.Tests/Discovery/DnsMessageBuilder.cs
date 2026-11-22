using System.Buffers.Binary;
using System.Net;
using System.Text;
using HomeBlaze.Services.Discovery.MulticastDns;

namespace HomeBlaze.Services.Tests.Discovery;

/// <summary>
/// Builds mDNS answers the way a responder sends them, without name compression unless a test writes it itself.
/// </summary>
internal sealed class DnsMessageBuilder
{
    private readonly List<byte[]> _questions = [];
    private readonly List<byte[]> _records = [];
    private readonly List<byte[]> _additionalRecords = [];
    private ushort? _questionCount;
    private byte _responseCode;
    private bool _isAdditional;

    public DnsMessageBuilder AddPointer(string name, string target) =>
        AddRecord(EncodeName(name), DnsMessage.PointerType, EncodeName(target));

    public DnsMessageBuilder AddService(string name, ushort port, string target)
    {
        var data = new byte[6].Concat(EncodeName(target)).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port);
        return AddRecord(EncodeName(name), DnsMessage.ServiceType, data);
    }

    public DnsMessageBuilder AddText(string name, params string[] entries) =>
        AddRecord(EncodeName(name), DnsMessage.TextType, entries.SelectMany(EncodeCharacterString).ToArray());

    public DnsMessageBuilder AddAddress(string name, string address) =>
        AddRecord(EncodeName(name), DnsMessage.AddressType, IPAddress.Parse(address).GetAddressBytes());

    /// <summary>
    /// Adds a question, whose name is already encoded, to the question section.
    /// </summary>
    public DnsMessageBuilder AddQuestion(byte[] encodedName)
    {
        var fixedPart = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, DnsMessage.PointerType);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 1);
        _questions.Add([.. encodedName, .. fixedPart]);
        return this;
    }

    /// <summary>
    /// Puts the records that follow into the additional section instead of the answer section.
    /// </summary>
    public DnsMessageBuilder InAdditionalSection()
    {
        _isAdditional = true;
        return this;
    }

    /// <summary>
    /// Writes a question count into the header that differs from the questions added.
    /// </summary>
    public DnsMessageBuilder WithQuestionCount(ushort questionCount)
    {
        _questionCount = questionCount;
        return this;
    }

    public DnsMessageBuilder WithResponseCode(byte responseCode)
    {
        _responseCode = responseCode;
        return this;
    }

    public DnsMessageBuilder AddRecord(byte[] encodedName, ushort type, byte[] data, uint timeToLive = 120)
    {
        var fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), timeToLive);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Length);
        (_isAdditional ? _additionalRecords : _records).Add([.. encodedName, .. fixedPart, .. data]);
        return this;
    }

    public byte[] Build()
    {
        var header = new byte[12];
        header[2] = 0x84; // a response, authoritative
        header[3] = _responseCode;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), _questionCount ?? (ushort)_questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)_records.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)_additionalRecords.Count);
        return
        [
            .. header,
            .. _questions.SelectMany(question => question),
            .. _records.SelectMany(record => record),
            .. _additionalRecords.SelectMany(record => record)
        ];
    }

    public static byte[] EncodeName(string name) =>
        [.. name.Split('.').SelectMany(EncodeCharacterString), 0];

    private static IEnumerable<byte> EncodeCharacterString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, .. bytes];
    }
}
