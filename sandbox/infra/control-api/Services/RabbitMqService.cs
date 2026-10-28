using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlApi.Models;

namespace ControlApi.Services;

public sealed class RabbitMqService(IContainerRuntime runtime) : IRabbitMqService
{
    // Sample/replay need to read and re-publish actual message bodies, not just queue
    // names/counts - rabbitmqctl (the docker-exec mechanism the rest of this class uses) has no
    // command for either. The management plugin's HTTP API already runs inside the rabbitmq
    // container regardless of whether RABBITMQ_UI_PORT publishes it to the host (see
    // docker-compose.yml's own comment on that toggle), so this reaches it directly over the
    // Docker network instead. Same "new HttpClient() held for this singleton's lifetime" pattern
    // HaproxyService/TrafficService already use - no IHttpClientFactory machinery needed for one
    // fixed internal URL. Same dev-only guest/guest credentials already used throughout this
    // project's own `ConnectionStrings__RabbitMq` (no real auth posture anywhere in this stack).
    private readonly HttpClient _managementClient = CreateManagementClient();

    private static HttpClient CreateManagementClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("http://rabbitmq:15672") };
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return client;
    }

    // "/" URL-encoded, as the management API's own docs require for the default vhost.
    private const string Vhost = "%2F";
    private static readonly JsonSerializerOptions ManagementJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // --formatter=json instead of parsing rabbitmqctl's plain-text table - rabbitmqctl's default
    // text output interleaves an informational "Listing queues for vhost ..." banner with the data
    // on the same stream depending on version. JSON is the one format rabbitmqctl guarantees won't
    // change shape across 3.x point releases.
    internal sealed record RabbitMqQueueInfo(string Name, int Messages);
    private static readonly JsonSerializerOptions RabbitMqJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // rabbitmqctl's json formatter emits newline-delimited JSON objects for list_queues (one row per
    // line), not a single wrapped array - but that shape isn't documented as a stable guarantee
    // across versions, so this tolerates a real array too rather than assuming one or the other.
    internal static List<RabbitMqQueueInfo> ParseRabbitMqQueueList(string output)
    {
        var trimmed = output.TrimStart();
        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<RabbitMqQueueInfo>>(trimmed, RabbitMqJsonOptions) ?? [];
        }

        var result = new List<RabbitMqQueueInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            // A line starting with '{' can still be malformed/truncated - e.g. the exec timeout
            // cutting rabbitmqctl off mid-stream. Skipping just that line keeps this read-only
            // status query "fails safely", instead of taking the whole panel down with an uncaught
            // JsonException over one bad line.
            try
            {
                var queue = JsonSerializer.Deserialize<RabbitMqQueueInfo>(line, RabbitMqJsonOptions);
                if (queue is not null)
                {
                    result.Add(queue);
                }
            }
            catch (JsonException)
            {
            }
        }

        return result;
    }

    public async Task<DeadLetterQueueStats?> GetDeadLetterQueueStatsAsync(CancellationToken ct)
    {
        var container = await runtime.FindAsync("rabbitmq", ct);
        if (container is null)
        {
            return null;
        }

        // Needs the exit code, unlike the other exec-based reads in this class - those read
        // config/status where "empty/unparseable output" already fails safely as itself, but here
        // an empty result is indistinguishable from the real "zero dead-lettered messages" success
        // case. Without checking the exit code, a failed rabbitmqctl invocation (e.g. a transient
        // auth/cookie mismatch right after the container restarts) would silently report "all clear"
        // instead of surfacing the failure - exactly the kind of false negative this panel exists to
        // avoid.
        var (exitCode, output) = await runtime.ExecWithExitCodeAsync(container.ID, ["rabbitmqctl", "list_queues", "name", "messages", "--formatter=json"], ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl exited {exitCode}: {output}");
        }

        var allQueues = ParseRabbitMqQueueList(output);

        // ".dead" is RabbitMqConsumer's own DeadQueueName convention ("{queueName}.dead") - matching
        // on the suffix instead of a hardcoded queue list means a new consumer's dead-letter queue
        // shows up here automatically, with nothing to keep in sync by hand.
        var deadQueues = allQueues
            .Where(q => q.Name.EndsWith(".dead", StringComparison.Ordinal))
            .Select(q => new DeadLetterQueueDepth(q.Name, q.Messages))
            .ToList();

        return new DeadLetterQueueStats(deadQueues, deadQueues.Sum(q => q.MessageCount));
    }

    public async Task<int?> ClearDeadLetterQueuesAsync(CancellationToken ct)
    {
        var container = await runtime.FindAsync("rabbitmq", ct);
        if (container is null)
        {
            return null;
        }

        var (exitCode, output) = await runtime.ExecWithExitCodeAsync(container.ID, ["rabbitmqctl", "list_queues", "name", "messages", "--formatter=json"], ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl exited {exitCode}: {output}");
        }

        var deadQueues = ParseRabbitMqQueueList(output)
            .Where(q => q.Name.EndsWith(".dead", StringComparison.Ordinal))
            .ToList();

        var totalPurged = 0;
        foreach (var queue in deadQueues)
        {
            var (purgeExitCode, purgeOutput) = await runtime.ExecWithExitCodeAsync(container.ID, ["rabbitmqctl", "purge_queue", queue.Name], ct);
            if (purgeExitCode != 0)
            {
                throw new InvalidOperationException($"rabbitmqctl purge_queue {queue.Name} exited {purgeExitCode}: {purgeOutput}");
            }

            // Counted from the listing taken just above, not from purge_queue's own output (which
            // reports success/failure, not a count) - a message landing in a dead queue between the
            // listing and its purge would be dropped without being counted here, but nothing in this
            // project produces real dead-letter traffic outside manual testing, so that race isn't a
            // practical concern for a teaching-only clear button.
            totalPurged += queue.Messages;
        }

        return totalPurged;
    }

    // Only the display preview gets truncated - a human reading a dead message's JSON body never
    // needs more than this, and it keeps the panel's payload box small. Replay below deliberately
    // does NOT reuse this limit - see its own comment.
    private const int SamplePreviewTruncateBytes = 50_000;

    public async Task<IReadOnlyList<DeadLetterMessageSample>?> SampleDeadLetterQueueAsync(string deadQueueName, int count, CancellationToken ct)
    {
        var container = await runtime.FindAsync("rabbitmq", ct);
        if (container is null)
        {
            return null;
        }

        var messages = await GetMessagesAsync(deadQueueName, count, requeue: true, SamplePreviewTruncateBytes, ct);
        return messages
            .Select(m => new DeadLetterMessageSample(m.Properties?.MessageId, ExtractDeadReason(m.Properties), m.Payload))
            .ToList();
    }

    public async Task<DlqReplayResult?> ReplayDeadLetterQueueAsync(string deadQueueName, int count, CancellationToken ct)
    {
        var container = await runtime.FindAsync("rabbitmq", ct);
        if (container is null)
        {
            return null;
        }

        if (!deadQueueName.EndsWith(".dead", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{deadQueueName}' is not a dead-letter queue name (expected a \".dead\" suffix).", nameof(deadQueueName));
        }

        var liveQueueName = deadQueueName[..^".dead".Length];

        // Peek (non-destructive) first, then publish each message to the live queue, and only
        // THEN remove them from the dead queue - never the other way around. A failure between
        // "remove from dead" and "publish to live" would lose the message with no trace; a
        // failure here (e.g. a transient blip talking to the management API) just leaves it where
        // it already was, safe to retry. Every current dead-letter producer is confirmed
        // redelivery-safe (checked for each one before this was built - see PLAN.md item 13), so a
        // message that ends up published twice because the removal step below raced a new arrival
        // is an accepted, harmless outcome; losing one outright is not.
        //
        // Deliberately does NOT pass SamplePreviewTruncateBytes here (review finding F1): that limit
        // is fine for a human-readable preview, but this payload gets republished verbatim - the
        // management API silently truncates "payload" past the given byte count while still
        // reporting the real size in "payload_bytes", so reusing the preview's 50,000-byte cap would
        // let replay silently publish a corrupted, truncated body and then permanently delete the
        // one real copy of the original. int.MaxValue as the truncate limit means "don't truncate" in
        // practice; VerifyNotTruncated below is a second, explicit guard against the same failure
        // mode in case some future caller ever does pass a real limit here.
        var messages = await GetMessagesAsync(deadQueueName, count, requeue: true, int.MaxValue, ct);
        if (messages.Count == 0)
        {
            return new DlqReplayResult(deadQueueName, 0);
        }

        foreach (var message in messages)
        {
            VerifyNotTruncated(deadQueueName, message);
        }

        foreach (var message in messages)
        {
            await PublishToLiveQueueAsync(liveQueueName, message, ct);
        }

        // Removes exactly as many messages as were just republished, assuming FIFO order and no
        // concurrent writer to this dead queue in the narrow window since the peek above - a
        // reasonable assumption for a manual, operator-triggered action against a queue nothing
        // else is meant to be touching. If a genuinely new message lands here in that window, this
        // could remove it instead of one just republished - not a loss (it's still redelivery-safe
        // to replay later), just a reordering.
        await GetMessagesAsync(deadQueueName, messages.Count, requeue: false, int.MaxValue, ct);

        return new DlqReplayResult(deadQueueName, messages.Count);
    }

    // Defense-in-depth against the truncation failure mode described in ReplayDeadLetterQueueAsync's
    // own comment (review finding F1): compares the management API's reported original size
    // (payload_bytes) against the actual size of the payload we got back, decoded per its own
    // encoding. A mismatch means the body was truncated server-side despite the large truncate limit
    // we pass - republishing it would corrupt the message and the step after this would then delete
    // the only full copy, so this throws instead (surfaced to the caller as a 500, not a silent
    // corrupt-then-delete).
    private static void VerifyNotTruncated(string deadQueueName, ManagementMessage message)
    {
        var actualBytes = message.PayloadEncoding == "base64"
            ? Convert.FromBase64String(message.Payload).LongLength
            : System.Text.Encoding.UTF8.GetByteCount(message.Payload);

        if (actualBytes != message.PayloadBytes)
        {
            throw new InvalidOperationException(
                $"Refusing to replay a message from '{deadQueueName}': the management API reported " +
                $"{message.PayloadBytes} original bytes but returned only {actualBytes} - replaying a " +
                "truncated payload would corrupt it and then permanently delete the only full copy.");
        }
    }

    private async Task<List<ManagementMessage>> GetMessagesAsync(string queueName, int count, bool requeue, int truncateBytes, CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        var request = new ManagementGetRequest(count, requeue ? "ack_requeue_true" : "ack_requeue_false", "auto", truncateBytes);
        using var response = await _managementClient.PostAsJsonAsync(
            $"/api/queues/{Vhost}/{Uri.EscapeDataString(queueName)}/get", request, ManagementJsonOptions, ct);

        // A missing queue (404) is a legitimate "nothing to sample/replay" outcome here - the
        // stats panel's own listing can go stale for a few seconds (it's polled, not live), so a
        // queue shown a moment ago may have already been fully drained/purged by the time this
        // runs. Any other non-success status is a real failure, surfaced to the caller as-is.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        var messages = await response.Content.ReadFromJsonAsync<List<ManagementMessage>>(ManagementJsonOptions, ct);
        return messages ?? [];
    }

    private async Task PublishToLiveQueueAsync(string liveQueueName, ManagementMessage message, CancellationToken ct)
    {
        // Published via the default (nameless) exchange, addressed as "amq.default" in the
        // management API - the same exchange RabbitMqConsumer's own PublishDeadLetterAsync uses to
        // reach a queue directly by name, just in reverse (dead queue -> live queue instead of
        // live queue -> dead queue). Persistent (delivery_mode: 2), matching every other publish
        // in this project (RabbitMqPublisher.cs) - without it, a replayed message would survive a
        // queue restart only by luck, unlike one published through the normal application path.
        // The dead message's own "x-dead-reason" header is deliberately NOT carried over - it
        // describes a dead-letter event, not a live message, and would be misleading on one.
        var request = new ManagementPublishRequest(
            new ManagementPublishProperties(message.Properties?.MessageId, "application/json", DeliveryMode: 2),
            liveQueueName,
            message.Payload,
            message.PayloadEncoding);

        using var response = await _managementClient.PostAsJsonAsync(
            $"/api/exchanges/{Vhost}/amq.default/publish", request, ManagementJsonOptions, ct);
        response.EnsureSuccessStatusCode();
    }

    internal static string? ExtractDeadReason(ManagementMessageProperties? properties)
    {
        if (properties?.Headers is not { } headers || !headers.TryGetValue("x-dead-reason", out var reason))
        {
            return null;
        }

        return reason.ValueKind == JsonValueKind.String ? reason.GetString() : reason.ToString();
    }

    // Shapes matching the RabbitMQ management API's own JSON contract (snake_case) for the
    // "get messages from a queue" and "publish to an exchange" endpoints - see
    // https://www.rabbitmq.com/docs/management#http-api for the reference this was built against.
    // Every field needs an explicit lowercase JsonPropertyName: PropertyNameCaseInsensitive (set on
    // ManagementJsonOptions) only relaxes DESERIALIZATION matching, it doesn't change what case
    // System.Text.Json writes on serialize - without these, PostAsJsonAsync would send "Count"/
    // "Ackmode"/etc. and RabbitMQ's management API rejects them outright with key_missing errors
    // (confirmed live against a real dead-lettered message before this fix).
    private sealed record ManagementGetRequest(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("ackmode")] string Ackmode,
        [property: JsonPropertyName("encoding")] string Encoding,
        [property: JsonPropertyName("truncate")] int Truncate);

    private sealed record ManagementMessage(
        string Payload,
        [property: JsonPropertyName("payload_encoding")] string PayloadEncoding,
        // The ORIGINAL message size, as stored - unlike Payload, never affected by the request's
        // own truncate limit. Compared against Payload's actual decoded size in VerifyNotTruncated.
        [property: JsonPropertyName("payload_bytes")] long PayloadBytes,
        ManagementMessageProperties? Properties,
        [property: JsonPropertyName("routing_key")] string RoutingKey);

    internal sealed record ManagementMessageProperties(
        [property: JsonPropertyName("message_id")] string? MessageId,
        Dictionary<string, JsonElement>? Headers);

    private sealed record ManagementPublishRequest(
        [property: JsonPropertyName("properties")] ManagementPublishProperties Properties,
        [property: JsonPropertyName("routing_key")] string RoutingKey,
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("payload_encoding")] string PayloadEncoding);

    private sealed record ManagementPublishProperties(
        [property: JsonPropertyName("message_id")] string? MessageId,
        [property: JsonPropertyName("content_type")] string ContentType,
        [property: JsonPropertyName("delivery_mode")] int DeliveryMode);
}
