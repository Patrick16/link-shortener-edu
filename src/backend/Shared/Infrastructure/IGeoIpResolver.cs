namespace Infrastructure;

public readonly record struct GeoLocation(string? Country, string? City);

public interface IGeoIpResolver
{
    Task<GeoLocation> ResolveAsync(string? ipAddress, CancellationToken cancellationToken = default);
}
