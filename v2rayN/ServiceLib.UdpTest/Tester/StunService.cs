using System.Security.Cryptography;

namespace ServiceLib.UdpTest.Tester;

public class StunService : IUdpTest
{
    private const int StunDefaultPort = 3478;
    private const string StunDefaultServer = "stun.voztovoice.org";
    private byte[]? _transactionId;

    private static readonly byte[] StunBindingRequestPacket =
    [
        // STUN Binding Request
        0x00, 0x01, // Message Type: Binding Request (0x0001)
        0x00, 0x00, // Message Length: 0 (no attributes)
        0x21, 0x12, 0xA4, 0x42, // Magic Cookie: 0x2112A442
        // Transaction ID: 96 bits (12 bytes) random
        0x66, 0x0E, 0xAB, 0xBC, 0x61, 0x0D,
        0xA4, 0x40, 0x8C, 0x65, 0xC1, 0xBE,
    ];

    public byte[] BuildUdpRequestPacket()
    {
        var request = (byte[])StunBindingRequestPacket.Clone();
        _transactionId = RandomNumberGenerator.GetBytes(12);
        _transactionId.CopyTo(request, 8);
        return request;
    }

    public bool VerifyAndExtractUdpResponse(byte[] stunResponseBytes)
    {
        var response = stunResponseBytes.AsSpan();
        if (_transactionId is null || response.Length < 20
            || BinaryPrimitives.ReadUInt16BigEndian(response) != 0x0101
            || BinaryPrimitives.ReadUInt32BigEndian(response[4..]) != 0x2112A442
            || !response.Slice(8, 12).SequenceEqual(_transactionId))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if (length % 4 != 0 || response.Length != 20 + length)
        {
            return false;
        }

        var mappedAddress = false;
        for (var offset = 20; offset < response.Length;)
        {
            if (response.Length - offset < 4) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var attributeLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            var paddedLength = (attributeLength + 3) & ~3;
            offset += 4;
            if (response.Length - offset < paddedLength) return false;
            if (type is 0x0001 or 0x0020)
            {
                if (attributeLength < 4 || response[offset] != 0) return false;
                var family = response[offset + 1];
                if (!((family == 1 && attributeLength == 8) || (family == 2 && attributeLength == 20)))
                    return false;
                mappedAddress = true;
            }
            offset += paddedLength;
        }
        return mappedAddress;
    }

    public ushort GetDefaultTargetPort()
    {
        return StunDefaultPort;
    }

    public string GetDefaultTargetHost()
    {
        return StunDefaultServer;
    }
}
