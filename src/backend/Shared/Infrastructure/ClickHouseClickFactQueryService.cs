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

    public async Task<ClickSummary> GetSummaryAsync(string hash, Guid callerUserId, CancellationToken cancellationToken = default)
    {
        // Owner-scoped: every bucket query below filters on user_id = callerUserId, so a hash with
        // zero rows at all (fresh link, no clicks yet), a hash this caller genuinely owns, and a
        // hash owned by someone else all come back as an ordinary (possibly all-zero) summary.
        // There used to be a separate "does anyone *else* own this hash" check here whose result
        // picked between returning null (-> ReportsController's 404) and this all-zero summary -
        // removed in review: that branch was the actual privacy leak it was trying to prevent,
        // since "exists, owned by someone else" (404) and "doesn't exist" (200, zeros) were
        // distinguishable from the outside. Filtering every query to callerUserId is what actually
        // keeps someone else's data out of the response; no extra branch is needed on top.
        var byDayTask = RunBucketsAsync(
            "SELECT toString(toDate(clicked_at)) AS k, count() AS c FROM clicks WHERE hash = {hash:String} AND user_id = toUUID({userId:String}) GROUP BY k ORDER BY k",
            hash, callerUserId, cancellationToken);
        var byCountryTask = RunTopBucketsAsync("country", hash, callerUserId, cancellationToken);
        var byDeviceTask = RunTopBucketsAsync("device_type", hash, callerUserId, cancellationToken);
        var byBrowserTask = RunTopBucketsAsync("browser", hash, callerUserId, cancellationToken);

        await Task.WhenAll(byDayTask, byCountryTask, byDeviceTask, byBrowserTask);

        var byDay = await byDayTask;

        // No separate "SELECT count()" query - byDay already has no filter beyond hash/owner, so it
        // already carries every row needed to derive the same total (found during review: the old
        // version paid for an extra full filtered scan just to recompute a number already in hand).
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
    private Task<IReadOnlyList<CountBucket>> RunTopBucketsAsync(string column, string hash, Guid userId, CancellationToken cancellationToken) =>
        RunBucketsAsync(
            $"SELECT {column} AS k, count() AS c FROM clicks WHERE hash = {{hash:String}} AND user_id = toUUID({{userId:String}}) AND {column} != '' GROUP BY k ORDER BY c DESC LIMIT 20",
            hash, userId, cancellationToken);

    private async Task<IReadOnlyList<CountBucket>> RunBucketsAsync(string sql, string hash, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, hash, userId);

        var buckets = new List<CountBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new CountBucket(reader.GetString(0), Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return buckets;
    }

    // userId is passed as a plain String parameter, then cast with toUUID({userId:String}) in SQL
    // rather than given its own ClickHouse-side UUID parameter type - ClickHouse.Driver's ADO
    // parameter binding has no documented Guid/UUID DbType of its own (unlike InsertBinaryAsync's
    // bulk-insert path, which accepts a raw Guid directly - see ClickHouseClickFactStore), and a
    // server-side cast from a string it already knows how to bind is the simplest way to avoid
    // guessing at driver-specific type mapping.
    private static void AddParameters(System.Data.Common.DbCommand command, string hash, Guid userId)
    {
        var hashParameter = command.CreateParameter();
        hashParameter.ParameterName = "hash";
        hashParameter.Value = hash;
        command.Parameters.Add(hashParameter);

        var userIdParameter = command.CreateParameter();
        userIdParameter.ParameterName = "userId";
        userIdParameter.Value = userId.ToString();
        command.Parameters.Add(userIdParameter);
    }

    // See ClickHouseClickFactStore's identical NewClient() comment - a fresh wrapper per call is
    // cheap, the pooled HttpClient underneath (via IHttpClientFactory) is what's actually reused.
    private ClickHouseConnection NewConnection() => new(_connectionString, _httpClientFactory, _httpClientName);
}
