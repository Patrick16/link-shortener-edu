using UAParser;

namespace Common;

// Wraps the UAParser library (offline regex-based parsing, no external calls) behind
// IUserAgentParser so callers depend on a plain DTO instead of UAParser's own types.
public sealed class UaParserUserAgentParser : IUserAgentParser
{
    private static readonly Parser Parser = Parser.GetDefault();

    // Real browser/bot user agents are well under this; the header itself is caller-controlled and
    // unbounded otherwise, and UAParser's matching is regex-based - truncating before it ever sees
    // the string bounds the worst-case parse cost regardless of how long an adversarial value is.
    private const int MaxLength = 512;

    public ParsedUserAgent Parse(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new ParsedUserAgent(null, null, null);
        }

        if (userAgent.Length > MaxLength)
        {
            userAgent = userAgent[..MaxLength];
        }

        var client = Parser.Parse(userAgent);

        var browser = client.UA.Family is null or "Other" ? null : client.UA.Family;
        var os = client.OS.Family is null or "Other" ? null : client.OS.Family;
        var deviceType = ClassifyDevice(userAgent, client);

        return new ParsedUserAgent(browser, os, deviceType);
    }

    private static string ClassifyDevice(string userAgent, ClientInfo client)
    {
        if (userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("crawler", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("spider", StringComparison.OrdinalIgnoreCase))
        {
            return "Bot";
        }

        if (client.Device.Family.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("tablet", StringComparison.OrdinalIgnoreCase))
        {
            return "Tablet";
        }

        if ((client.Device.Family != "Other" && client.OS.Family is "iOS" or "Android") ||
            userAgent.Contains("mobi", StringComparison.OrdinalIgnoreCase))
        {
            return "Mobile";
        }

        return "Desktop";
    }
}
