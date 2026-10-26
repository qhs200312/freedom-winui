using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Fmt;

public sealed class NowhereFmtTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("relay.example", "tcp", true)]
    [InlineData("192.0.2.1", "udp", false)]
    [InlineData("[2001:db8::1]", "udp", true)]
    public void ShareLink_ShouldRoundTripWithoutLosingClientParameters(string host, string net, bool morph)
    {
        var link = $"nowhere://{Key}@{host}:2000?net={net}&morph={(morph ? 1 : 0)}&pin={new string('a', 64)}&sni=portal.example&rate=100&etar=200#Test%20Node";
        var node = FmtHandler.ResolveConfig(link, out _)!;
        node.ConfigType.Should().Be(EConfigType.Nowhere);
        node.Address.Should().Be(host.Trim('[', ']'));
        node.Remarks.Should().Be("Test Node");
        node.GetProtocolExtra().NowhereTransport.Should().Be(net);
        node.GetProtocolExtra().NowhereMorph.Should().Be(morph);
        node.GetProtocolExtra().NowhereUploadMbps.Should().Be(100);
        node.GetProtocolExtra().NowhereDownloadMbps.Should().Be(200);
        var restored = FmtHandler.ResolveConfig(FmtHandler.GetShareUri(node)!, out _)!;
        restored.Password.Should().Be(Key);
        restored.ProtoExtra.Should().Be(node.ProtoExtra);
        restored.CertSha.Should().Be(node.CertSha);
        restored.Sni.Should().Be(node.Sni);
    }

    [Fact]
    public void Defaults_ShouldUseTlsMorphAndSystemTrust()
    {
        var node = NowhereFmt.Resolve($"nowhere://{Key}@example.com:2000", out _)!;
        node.GetProtocolExtra().NowhereTransport.Should().Be("tcp");
        node.GetProtocolExtra().NowhereMorph.Should().BeTrue();
        node.CertSha.Should().BeEmpty();
        node.GetAllowInsecure().Should().BeFalse();
    }

    [Theory]
    [InlineData("tcp4", "192.0.2.1")]
    [InlineData("udp6", "[2001:db8::1]")]
    public void VectorInput_ShouldRetainAddressFamilyAndOverrideImportedListener(string transport, string host)
    {
        var node = NowhereFmt.Resolve($"vector://{Key}@{host}/{transport}:2001?in=0.0.0.0:6666&morph=0&unknown=ignored", out _)!;
        var launch = NowhereFmt.ToVectorUri(node, 12345, "localuser", "localpass");
        launch.Should().Contain($"/{transport}:2001?");
        launch.Should().Contain("in=localuser:localpass@127.0.0.1:12345");
        launch.Should().NotContain("0.0.0.0").And.NotContain("unknown");
        var restored = NowhereFmt.Resolve(NowhereFmt.ToUri(node), out _)!;
        restored.GetProtocolExtra().NowhereTransport.Should().Be(transport);
    }

    [Fact]
    public void RecognizedDuplicates_ShouldUseFirstValue()
    {
        NowhereFmt.Resolve($"nowhere://{Key}@example.com:2000?morph=0&morph=1", out _)!
            .GetProtocolExtra().NowhereMorph.Should().BeFalse();
        NowhereFmt.Resolve($"nowhere://{Key}@example.com:2000?morph=bad&morph=1", out _).Should().BeNull();
    }

    [Fact]
    public void VectorAuthority_ShouldUseTlsAndIgnoreShareOnlyNetParameter()
    {
        var node = NowhereFmt.Resolve($"vector://{Key}@example.com:2000?net=udp", out _)!;
        node.GetProtocolExtra().NowhereTransport.Should().Be("tcp");
    }

    [Theory]
    [InlineData("portal://0123456789abcdef0123456789abcdef@localhost:2000")]
    [InlineData("egress://0123456789abcdef0123456789abcdef@localhost:2000")]
    [InlineData("nowhere://short@localhost:2000")]
    [InlineData("nowhere://0123456789ABCDEF0123456789ABCDEF@localhost:2000")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef:password@localhost:2000")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost:2000/")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost:2000?pin=short")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost:2000?net=invalid")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost:2000?rate=-1")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost:2000?etar=2147483648")]
    [InlineData("nowhere://0123456789abcdef0123456789abcdef@localhost:2000?sni=127.0.0.1")]
    [InlineData("vector://0123456789abcdef0123456789abcdef@localhost:2000/udp:2000")]
    [InlineData("vector://0123456789abcdef0123456789abcdef@127.0.0.1/udp6:2000")]
    public void InvalidOrServerLinks_ShouldBeRejected(string link)
        => NowhereFmt.Resolve(link, out _).Should().BeNull();

    [Fact]
    public void ProtocolExtraSerialization_ShouldRetainOptionsWithoutNewDatabaseColumns()
    {
        var node = NowhereFmt.Resolve($"nowhere://{Key}@example.com:2000?net=udp&morph=0&rate=100&etar=100", out _)!;
        var clone = JsonUtils.Deserialize<ProfileItem>(JsonUtils.Serialize(node))!;
        clone.GetProtocolExtra().Should().Be(node.GetProtocolExtra());
        NodeValidator.Validate(clone, ECoreType.Xray).Success.Should().BeTrue();
        NodeValidator.Validate(clone, ECoreType.sing_box).Success.Should().BeTrue();
        NodeValidator.Validate(clone, ECoreType.mihomo).Success.Should().BeFalse();
        clone.AllowInsecure = Global.StringTrue;
        NodeValidator.Validate(clone, ECoreType.Xray).Success.Should().BeFalse();
    }
}
