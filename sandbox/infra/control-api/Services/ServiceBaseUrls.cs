namespace ControlApi.Services;

// Single place for "which real base URL does this serviceId's traffic go through" - mirrors
// TrafficService's own k6 env-var mapping (LINK_API_URL/REDIRECT_API_URL/AUTH_API_URL/
// REPORTING_API_URL) exactly. Shared by LessonTraceService and LessonProbeService instead of each
// keeping its own copy of the same two dictionaries.
internal static class ServiceBaseUrls
{
    private static readonly IReadOnlyDictionary<string, string> Direct = new Dictionary<string, string>
    {
        ["auth-api"] = "http://auth-api:8080",
        ["link-api"] = "http://link-api:8080",
        ["redirect-api"] = "http://redirect-api:8080",
        ["reporting-api"] = "http://reporting-api:8080",
    };

    // Only link-api/redirect-api sit behind nginx - same two services TrafficService's own
    // nginx-bypass branch touches.
    private static readonly IReadOnlyDictionary<string, string> NginxRouted = new Dictionary<string, string>
    {
        ["link-api"] = "http://nginx:8082",
        ["redirect-api"] = "http://nginx:8083",
    };

    public static string Resolve(string serviceId, bool nginxBypassed) =>
        !nginxBypassed && NginxRouted.TryGetValue(serviceId, out var routed)
            ? routed
            : Direct.GetValueOrDefault(serviceId)
                ?? throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unknown serviceId for a lesson request.");
}
