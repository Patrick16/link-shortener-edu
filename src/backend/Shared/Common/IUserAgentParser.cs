namespace Common;

public readonly record struct ParsedUserAgent(string? Browser, string? Os, string? DeviceType);

public interface IUserAgentParser
{
    ParsedUserAgent Parse(string userAgent);
}
