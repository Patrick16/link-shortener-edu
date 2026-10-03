using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure;

// Drains LocalPublishQueue so IMessagePublisher.PublishAsync's own network/IO cost never lands on a
// request thread. A handful of parallel workers share RabbitMqPublisher's own channel pool (16 slots,
// see RabbitMqPublisher.PoolCapacity) rather than running one at a time - half of that cap leaves
// headroom for RabbitMqRetryWorker's own TryRepublishAsync calls competing for the same pool.
public sealed class LocalPublishQueueWorker(
    LocalPublishQueue queue,
    IMessagePublisher publisher,
    ILogger<LocalPublishQueueWorker> logger) : IHostedService
{
    private const int WorkerCount = 8;

    private readonly CancellationTokenSource _forceStopCts = new();
    private readonly List<Task> _workers = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < WorkerCount; i++)
        {
            // Task.Run, not awaited here - each loop runs for the lifetime of the host, way past this
            // method's own return. Its own token (not cancellationToken, which is just this startup
            // call's token) is what StopAsync uses to force a stuck worker to actually exit.
            _workers.Add(Task.Run(() => ProcessAsync(_forceStopCts.Token), CancellationToken.None));
        }

        return Task.CompletedTask;
    }

    // Host shutdown (SIGTERM, docker-compose stop): stop accepting new writes first, then give the
    // workers up to the host's own shutdown budget (the cancellationToken here is
    // HostOptions.ShutdownTimeout) to drain whatever was already queued, instead of abandoning it the
    // instant the signal arrives. Channel.Writer.TryComplete lets Reader.ReadAllAsync finish the
    // backlog and return normally on its own - no cancellation needed for the common case where
    // draining finishes well within the budget.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();

        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Local publish queue did not drain within the shutdown timeout - remaining queued events were not published");
        }
        finally
        {
            // Only bites if the WaitAsync above timed out - unsticks a worker still mid-Publish (or
            // still waiting on a slow channel pool slot) so the process can actually exit instead of
            // the host's own hard-kill timeout doing it anyway with no chance to log first.
            await _forceStopCts.CancelAsync();

            // Taken right after the cancel above, with no further dequeues possible in between (every
            // worker's ReadAllAsync observes _forceStopCts and stops pulling from the channel) - an
            // accurate count of whatever never made it to PublishAsync, logged once here instead of
            // racing all WorkerCount workers to drain-and-log their own partial share of it.
            var abandoned = queue.Reader.Count;
            if (abandoned > 0)
            {
                logger.LogWarning(
                    "Local publish queue force-stopped before finishing its drain - {Abandoned} queued event(s) were discarded unpublished",
                    abandoned);
            }
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // The trace context captured at enqueue time (see LocalPublishQueue.EnqueueAsync)
                    // is already baked into this closure as an explicit parentContext argument - it
                    // rides along regardless of whatever Activity.Current happens to be ambient here.
                    await job.Publish(publisher, cancellationToken);
                }
                catch (Exception ex)
                {
                    // PublishAsync already falls back to SQLite internally on its own failures (broker
                    // unreachable) and swallows those - reaching here means something unexpected
                    // escaped that (e.g. the fallback store itself faulted). Must not crash this loop,
                    // or the queue stops draining entirely for every event still waiting behind this one.
                    logger.LogError(ex, "Local publish queue worker failed to process a queued publish");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync's drain budget elapsed and force-cancelled this loop (see _forceStopCts) -
            // whatever this worker never got to dequeue never reached PublishAsync at all, so it never
            // got the SQLite-fallback safety net either. Nothing left to do here: StopAsync logs the
            // total abandoned count itself (via queue.Reader.Count) once every worker has exited this
            // way, instead of each worker draining and logging its own partial share.
        }
    }
}
