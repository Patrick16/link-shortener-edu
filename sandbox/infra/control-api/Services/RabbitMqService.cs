using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services;

public sealed class RabbitMqService(IContainerRuntime runtime) : IRabbitMqService
{
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
}
