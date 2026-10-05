using ClickHouse.Driver.ADO;
using Common.Models;

namespace Infrastructure;

// Read side of the ClickHouse read model, queried directly by ReportingApi - no ORM, just raw SQL
// with server-side parameter substitution ({name:Type} placeholders, resolved by ClickHouse itself,
// not string-interpolated here) since this is the one place in the codebase actually showing what a
// columnar OLAP query looks like; wrapping it behind an EF-style abstraction would hide the lesson.
public sealed class ClickHouseClickFactQueryService(
    string connectionString, IHttpClientFactory httpClientFactory, string httpClientName) : IClickFactQueryService
{
    private readonly string _connectionString = connectionString;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly string _httpClientName = httpClientName;

    public async Task<ClickSummary> GetSummaryAsync(string hash, CancellationToken cancellationToken = default)
    {
        // Four independent queries, none depending on another's result - run them concurrently on
        // their own connections rather than one connection serially (found during review: a single
        // ADO.NET connection can't run overlapping commands anyway, so "share one connection" and
        // "run concurrently" were mutually exclusive; each connection here draws from the same
        // pooled HttpClient via IHttpClientFactory, so opening 4 is cheap, not 4x the socket cost).
        var byDayTask = RunBucketsAsync(
            "SELECT toString(toDate(clicked_at)) AS k, count() AS c FROM clicks WHERE hash = {hash:String} GROUP BY k ORDER BY k",
            hash, cancellationToken);
        var byCountryTask = RunTopBucketsAsync("country", hash, cancellationToken);
        var byDeviceTask = RunTopBucketsAsync("device_type", hash, cancellationToken);
        var byBrowserTask = RunTopBucketsAsync("browser", hash, cancellationToken);

        await Task.WhenAll(byDayTask, byCountryTask, byDeviceTask, byBrowserTask);
        var byDay = await byDayTask;

        // No separate "SELECT count()" query - byDay already has no filter beyond hash, so it
        // already carries every row needed to derive the same total (found during review: the old
        // version paid for a 5th full filtered scan just to recompute a number already in hand).
        var total = byDay.Sum(b => b.Count);

        return new ClickSummary(hash, total, byDay, await byCountryTask, await byDeviceTask, await byBrowserTask);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = NewConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // byCountry/byDevice/byBrowser only ever differed by which column they grouped on - collapsed
    // into one helper instead of three near-identical SQL strings. `column` is always one of the 3
    // hardcoded literals passed by GetSummaryAsync above, never user input - safe to interpolate
    // directly (ClickHouse has no parameter placeholder for identifiers, only values).
    private Task<IReadOnlyList<CountBucket>> RunTopBucketsAsync(string column, string hash, CancellationToken cancellationToken) =>
        RunBucketsAsync(
            $"SELECT {column} AS k, count() AS c FROM clicks WHERE hash = {{hash:String}} AND {column} != '' GROUP BY k ORDER BY c DESC LIMIT 20",
            hash, cancellationToken);

    private async Task<IReadOnlyList<CountBucket>> RunBucketsAsync(string sql, string hash, CancellationToken cancellationToken)
    {
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddHashParameter(command, hash);

        var buckets = new List<CountBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new CountBucket(reader.GetString(0), Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return buckets;
    }

    private static void AddHashParameter(System.Data.Common.DbCommand command, string hash)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "hash";
        parameter.Value = hash;
        command.Parameters.Add(parameter);
    }

    // See ClickHouseClickFactStore's identical NewClient() comment - a fresh wrapper per call is
    // cheap, the pooled HttpClient underneath (via IHttpClientFactory) is what's actually reused.
    private ClickHouseConnection NewConnection() => new(_connectionString, _httpClientFactory, _httpClientName);
}
