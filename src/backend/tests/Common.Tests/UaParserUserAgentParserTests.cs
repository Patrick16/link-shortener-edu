namespace Common.Tests;

public class UaParserUserAgentParserTests
{
    private readonly UaParserUserAgentParser _sut = new();

    private const string ChromeOnWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string ChromeOnAndroidMobile =
        "Mozilla/5.0 (Linux; Android 10; SM-G960F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.120 Mobile Safari/537.36";

    private const string SafariOnIPad =
        "Mozilla/5.0 (iPad; CPU OS 14_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.0 Mobile/15E148 Safari/604.1";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsAllNull(string userAgent)
    {
        var result = _sut.Parse(userAgent);

        Assert.Equal(new ParsedUserAgent(null, null, null), result);
    }

    [Fact]
    public void Parse_DesktopBrowser_ReturnsDesktopWithBrowserAndOs()
    {
        var result = _sut.Parse(ChromeOnWindows);

        Assert.Equal("Chrome", result.Browser);
        Assert.Equal("Windows", result.Os);
        Assert.Equal("Desktop", result.DeviceType);
    }

    [Fact]
    public void Parse_AndroidMobileBrowser_ClassifiesAsMobile()
    {
        var result = _sut.Parse(ChromeOnAndroidMobile);

        Assert.Equal("Android", result.Os);
        Assert.Equal("Mobile", result.DeviceType);
    }

    [Fact]
    public void Parse_IPad_ClassifiesAsTablet()
    {
        var result = _sut.Parse(SafariOnIPad);

        Assert.Equal("Tablet", result.DeviceType);
    }

    [Theory]
    [InlineData("Googlebot/2.1 (+http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("Mozilla/5.0 (compatible; SomeCrawler/1.0)")]
    [InlineData("Mozilla/5.0 (compatible; SomeSpider/1.0)")]
    public void Parse_BotLikeUserAgent_ClassifiesAsBot(string userAgent)
    {
        var result = _sut.Parse(userAgent);

        Assert.Equal("Bot", result.DeviceType);
    }

    [Fact]
    public void Parse_BotSubstringWinsOverMobileSubstring_ClassifiesAsBotNotMobile()
    {
        // Ordering matters: Bot must be checked before Tablet/Mobile, since a bot's UA string can
        // itself contain "mobi" (many crawlers advertise a mobile-looking UA to see mobile content).
        var result = _sut.Parse("Mozilla/5.0 (Linux; Android 10) Mobile BotCrawler/1.0");

        Assert.Equal("Bot", result.DeviceType);
    }

    [Fact]
    public void Parse_UnrecognizedUserAgent_ReturnsNullBrowserAndOsButStillDesktop()
    {
        // UAParser maps an unrecognized client to family "Other" - that must surface as null, not
        // the literal string "Other".
        var result = _sut.Parse("TotallyUnknownClient/1.0");

        Assert.Null(result.Browser);
        Assert.Null(result.Os);
        Assert.Equal("Desktop", result.DeviceType);
    }
}
