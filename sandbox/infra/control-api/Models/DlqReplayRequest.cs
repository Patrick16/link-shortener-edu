namespace ControlApi.Models;

// QueueName is the dead-letter queue's own full name (e.g. "shortener-service.link-created.dead",
// exactly what GetDeadLetterQueueStatsAsync already reports and the frontend already holds per
// row) - not the live queue it replays back onto, which DlqReplayCapability derives by stripping
// the ".dead" suffix.
public record DlqReplayRequest(string QueueName, int Count);
