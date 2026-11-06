namespace ControlApi.Models;

// Drives a chaos-trigger lesson's own probe: fire `Count` real requests against `EndpointId`
// (one of TrafficService.EndpointRegistry's own ids - same vocabulary a load-test step uses),
// all at once, while the chaos action (stop/degrade) is in effect. Unlike a load-test's k6 run,
// this stays in-process in control-api - a handful of concurrent probes is plenty to show "did
// the real request succeed or not", and starting a whole k6 container for 5-10 requests would be
// pure overhead. Concurrent, not one-at-a-time with a pause between - see LessonProbeService's
// own comment for why (bounds the probe's wall-clock time to roughly one request's timeout
// regardless of Count, so it reliably fits inside the chaos action's own fixed-duration window).
public record ChaosProbeInput(string EndpointId, int Count);
