using ClickHouse.Driver;
using Common.Models;

namespace Infrastructure;

// Batched, not per-row - ClickHouse's MergeTree engine is built for bulk inserts; a single-row
// INSERT per click would create one tiny part per statement and force constant background merges
// (the canonical ClickHouse anti-pattern this feature exists partly to teach). ConsumeBatchAsync
// already hands this one bulk write per batch, same shape as every other batch consumer here.
public sealed class ClickHouseClickFactStore(
    string connectionString, IHttpClientFactory httpClientFactory, string httpClientName) : IClickFactStore
{
    private static readonly string[] Columns =
        ["id", "hash", "clicked_at", "country", "device_type", "browser", "os", "referrer_domain"];

    private readonly string _connectionString = connectionString;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly string _httpClientName = httpClientName;

    public async Task InsertManyAsync(IReadOnlyCollection<ClickFact> facts, CancellationToken cancellationToken = default)
    {
        if (facts.Count == 0) return;

        using var client = NewClient();
        var rows = facts.Select(f => new object?[]
        {
            f.Id, f.Hash, f.ClickedAt, f.Country ?? "", f.DeviceType ?? "", f.Browser ?? "", f.Os ?? "", f.ReferrerDomain ?? "",
        });

        await client.InsertBinaryAsync("clicks", Columns, rows, options: default, cancellationToken);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = NewClient();
            await client.ExecuteNonQueryAsync("SELECT 1", parameters: null, options: null, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // A fresh ClickHouseClient per call is cheap - it holds no socket of its own. What's expensive
    // (and was the actual problem found during review) is handing it a one-off HttpClient instead
    // of one borrowed from IHttpClientFactory: the factory keeps the real connection pool alive and
    // reused across calls regardless of how many short-lived ClickHouseClient wrappers sit on top.
    private ClickHouseClient NewClient() => new(_connectionString, _httpClientFactory, _httpClientName);
}
