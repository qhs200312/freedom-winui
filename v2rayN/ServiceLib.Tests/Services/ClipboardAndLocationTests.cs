using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public class ClipboardAndLocationTests
{
    [Fact]
    public void Clipboard_ShouldSelectOnlyPastedEnabledSubscriptionsOnce()
    {
        var urls = ClipboardSubscriptionImport.GetUrls(
            " https://example.org/sub?token=example \r\nhttps://example.org/sub?token=example\nvmess://sample\nftp://example.org\nHTTPS://other.example/sub");
        urls.Should().HaveCount(2);
        var ids = ClipboardSubscriptionImport.SelectIds(urls,
        [
            new SubItem { Id = "one", Url = "https://example.org/sub?token=example", Enabled = true },
            new SubItem { Id = "one", Url = "https://example.org/sub?token=example", Enabled = true },
            new SubItem { Id = "disabled", Url = "HTTPS://other.example/sub", Enabled = false },
            new SubItem { Id = "unrelated", Url = "https://unrelated.example/sub", Enabled = true },
            new SubItem { Id = "", Url = "HTTPS://other.example/sub", Enabled = true }
        ]);
        ids.Should().Equal("one");
    }

    [Theory]
    [InlineData("vmess://sample")]
    [InlineData("not a subscription")]
    [InlineData("")]
    public void NodeOrInvalidClipboard_ShouldNotSelectAllSubscriptions(string text)
    {
        var urls = ClipboardSubscriptionImport.GetUrls(text);
        urls.Should().BeEmpty();
        ClipboardSubscriptionImport.SelectIds(urls,
            [new SubItem { Id = "one", Url = "https://example.org", Enabled = true }]).Should().BeEmpty();
    }

    [Fact]
    public void ExitLocation_ShouldTranslateScreenshotExample()
    {
        IpLocationLocalization.Format(new IpInfoResult("US", "203.0.113.1", "United States", "California", "Los Angeles"))
            .Should().Be("美国 · 加利福尼亚州 · 洛杉矶");
    }

    [Fact]
    public void ExitLocation_ShouldKeepLocalizedNamesAndDeduplicate()
    {
        IpLocationLocalization.Format(new IpInfoResult("HK", "203.0.113.1", "香港", "香港", "香港")).Should().Be("香港");
        IpLocationLocalization.Format(new IpInfoResult("US", "203.0.113.1", null, "Unmapped Region", "Unmapped City"))
            .Should().Be("美国 · Unmapped Region · Unmapped City");
    }

    [Fact]
    public void ChineseApiLocale_ShouldPreserveOtherParametersAndCustomProviders()
    {
        var localized = IpLocationLocalization.LocalizeApiUrl("https://ipwho.is/?lang=en&fields=ip%2Ccountry");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(localized).Query);
        query["lang"].Should().Be("zh-CN");
        query["fields"].Should().Be("ip,country");
        const string custom = "https://example.org/api?lang=en";
        IpLocationLocalization.LocalizeApiUrl(custom).Should().Be(custom);
    }
}
