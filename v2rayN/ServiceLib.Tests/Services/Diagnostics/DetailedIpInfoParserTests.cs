using ServiceLib.Services.Diagnostics;
using Xunit;

namespace ServiceLib.Tests.Services.Diagnostics;

public class DetailedIpInfoParserTests
{
    [Fact]
    public void IpWhoIs_ParsesNestedConnectionTimezoneAndZeroCoordinates()
    {
        var result = DetailedIpInfoParser.Parse("""
            {"success":true,"ip":"203.0.113.10","country_code":"hk","country":"Hong Kong",
             "region":"Central","city":"Hong Kong","latitude":0,"longitude":"114.1",
             "connection":{"asn":64512,"isp":"Fixture ISP","org":"Fixture Network"},
             "timezone":{"id":"Asia/Hong_Kong"}}
            """);
        Assert.NotNull(result);
        Assert.Equal("203.0.113.10", result.Ip);
        Assert.Equal("HK", result.CountryCode);
        Assert.Equal("Hong Kong", result.CountryName);
        Assert.Equal("Central", result.Region);
        Assert.Equal("Hong Kong", result.City);
        Assert.Equal(64512L, result.Asn);
        Assert.Equal("Fixture ISP", result.Provider);
        Assert.Equal("Fixture Network", result.Organization);
        Assert.Equal("Asia/Hong_Kong", result.Timezone);
        Assert.Equal(0d, result.Latitude);
        Assert.Equal(114.1d, result.Longitude);
    }

    [Fact]
    public void IpInfo_ParsesIPv6OrgPrefixAndLoc()
    {
        var result = DetailedIpInfoParser.Parse("""
            {"ip":"2001:0db8:0000::1","country":"US","city":"Seattle","region":"Washington",
             "org":"AS64513 Fixture Incorporated","timezone":"America/Los_Angeles","loc":"47.61,-122.33"}
            """);
        Assert.NotNull(result);
        Assert.Equal("2001:db8::1", result.Ip);
        Assert.Equal("US", result.CountryCode);
        Assert.False(string.IsNullOrWhiteSpace(result.CountryName));
        Assert.Equal(64513L, result.Asn);
        Assert.Equal("Fixture Incorporated", result.Organization);
        Assert.Equal("Fixture Incorporated", result.Provider);
        Assert.Equal(47.61d, result.Latitude);
        Assert.Equal(-122.33d, result.Longitude);
    }

    [Fact]
    public void IpApiIs_ParsesNestedLocationAndAsn()
    {
        var result = DetailedIpInfoParser.Parse("""
            {"ip":"192.0.2.1","location":{"country_code":"GBR","country":"United Kingdom",
             "state":"England","city":"London","timezone":"Europe/London","latitude":51.5,"longitude":-0.1},
             "asn":{"asn":64514,"org":"Fixture Transit"},"company":{"name":"Fixture Hosting"}}
            """);
        Assert.NotNull(result);
        Assert.Equal("GB", result.CountryCode);
        Assert.Equal("United Kingdom", result.CountryName);
        Assert.Equal("England", result.Region);
        Assert.Equal("Fixture Hosting", result.Provider);
        Assert.Equal("Fixture Transit", result.Organization);
        Assert.Equal(64514L, result.Asn);
    }

    [Theory]
    [InlineData("UK", "GB")]
    [InlineData("EL", "GR")]
    [InlineData("USA", "US")]
    [InlineData("CHN", "CN")]
    [InlineData("ZZZ", null)]
    public void CountryAliases_AreNormalized(string code, string? expected)
    {
        var result = DetailedIpInfoParser.Parse("{\"ip\":\"192.0.2.1\",\"country_code\":\"" + code + "\"}");
        Assert.NotNull(result);
        Assert.Equal(expected, result.CountryCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"ip\"")]
    [InlineData("{}")]
    [InlineData("{\"ip\":123}")]
    [InlineData("{\"ip\":\"not-an-ip\"}")]
    [InlineData("{\"ip\":\"127.1\"}")]
    [InlineData("{\"ip\":\"2130706433\"}")]
    [InlineData("{\"ip\":\"0x7f.0.0.1\"}")]
    [InlineData("{\"ip\":\"010.0.0.1\"}")]
    [InlineData("{\"ip\":\"192.0.2.1\",\"success\":false}")]
    [InlineData("{\"ip\":\"192.0.2.1\",\"status\":\"FAIL\"}")]
    [InlineData("{\"ip\":\"192.0.2.1\",\"error\":{\"message\":\"quota\"}}")]
    public void InvalidAndApiErrorPayloads_ReturnNull(string body)
    {
        Assert.Null(DetailedIpInfoParser.Parse(body));
    }

    [Fact]
    public void OptionalMalformedFields_DoNotInvalidateGoodIPOrInventCoordinates()
    {
        var result = DetailedIpInfoParser.Parse("""
            {"ip":"192.0.2.1","country":{},"connection":[],"asn":-1,"org":45,"timezone":[],
             "latitude":91,"longitude":"NaN","loc":"bad,181","city":null}
            """);
        Assert.NotNull(result);
        Assert.Null(result.CountryCode);
        Assert.Null(result.CountryName);
        Assert.Null(result.Asn);
        Assert.Null(result.Timezone);
        Assert.Null(result.Latitude);
        Assert.Null(result.Longitude);
    }

    [Theory]
    [InlineData("AS64515", 64515L)]
    [InlineData("4294967295", 4294967295L)]
    [InlineData("4294967296", null)]
    [InlineData("ASinvalid", null)]
    [InlineData("0", null)]
    public void StringAsn_ValidatesRange(string value, long? expected)
    {
        var result = DetailedIpInfoParser.Parse("{\"query\":\"192.0.2.1\",\"countryCode\":\"US\",\"asn\":\"" + value + "\"}");
        Assert.NotNull(result);
        Assert.Equal(expected, result.Asn);
    }

    [Fact]
    public void GeoJs_StringCoordinatesAndOrganization()
    {
        var result = DetailedIpInfoParser.Parse("""
            {"ip":"198.51.100.1","country_code":"JP","country":"Japan","organization_name":"Fixture ISP",
             "asn":64516,"latitude":"35.6","longitude":"139.7","timezone":"Asia/Tokyo"}
            """);
        Assert.NotNull(result);
        Assert.Equal("Fixture ISP", result.Organization);
        Assert.Equal(35.6d, result.Latitude);
        Assert.Equal(139.7d, result.Longitude);
    }
}
