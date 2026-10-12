using System.Buffers.Binary;
using AwesomeAssertions;
using ServiceLib.UdpTest.Tester;
using Xunit;

namespace ServiceLib.Tests.Services;

public class StunResponseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BindingSuccess_ShouldRequireMappedAddress(bool ipv6, bool xor)
    {
        var stun = new StunService();
        var response = Response(stun.BuildUdpRequestPacket(), ipv6, xor);
        stun.VerifyAndExtractUdpResponse(response).Should().BeTrue();
    }

    [Theory]
    [InlineData("error")]
    [InlineData("transaction")]
    [InlineData("cookie")]
    [InlineData("length")]
    [InlineData("attribute")]
    [InlineData("family")]
    [InlineData("missing-address")]
    [InlineData("truncated")]
    public void UnrelatedOrMalformedResponse_ShouldNotReportSuccess(string mutation)
    {
        var stun = new StunService();
        var response = Response(stun.BuildUdpRequestPacket(), false, true);
        switch (mutation)
        {
            case "error": response[1] = 0x11; break;
            case "transaction": response[8] ^= 1; break;
            case "cookie": response[4] ^= 1; break;
            case "length": response[3] = 0; break;
            case "attribute": response[23] = 20; break;
            case "family": response[25] = 2; break;
            case "missing-address": response[21] = 0x24; break;
            case "truncated": response = response[..^1]; break;
        }
        stun.VerifyAndExtractUdpResponse(response).Should().BeFalse();
    }

    [Fact]
    public void NewRequest_ShouldRejectStaleTransaction()
    {
        var stun = new StunService();
        var first = stun.BuildUdpRequestPacket();
        var response = Response(first, false, true);
        var next = stun.BuildUdpRequestPacket();
        first.AsSpan(8).SequenceEqual(next.AsSpan(8)).Should().BeFalse();
        stun.VerifyAndExtractUdpResponse(response).Should().BeFalse();
        new StunService().VerifyAndExtractUdpResponse(response).Should().BeFalse();
    }

    private static byte[] Response(byte[] request, bool ipv6, bool xor)
    {
        var addressLength = ipv6 ? 20 : 8;
        var response = new byte[24 + addressLength];
        request.CopyTo(response, 0);
        response[0] = 1;
        response[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), (ushort)(4 + addressLength));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), (ushort)(xor ? 0x0020 : 0x0001));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), (ushort)addressLength);
        response[25] = (byte)(ipv6 ? 2 : 1);
        return response;
    }
}
