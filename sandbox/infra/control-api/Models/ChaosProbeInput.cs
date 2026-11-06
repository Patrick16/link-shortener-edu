namespace ControlApi.Models;

// Drives a chaos-trigger lesson's own probe: fire `Count` real requests against `EndpointId`
// (one of TrafficService.EndpointRegistry's own ids - same vocabulary a load-test step uses),
// `IntervalMs` apart, while the chaos action (stop/degrade) is in effect. Unlike a load-test's
// k6 run, this stays in-process in control-api - a handful of sequential probes is plenty to show
// "did the real request succeed or not", and starting a whole k6 container for 5-10 requests
// would be pure overhead.
public record ChaosProbeInput(string EndpointId, int Count, int IntervalMs = 1000);
