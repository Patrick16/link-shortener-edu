namespace ControlApi.Models;

// A fixture call made first so the real traced call has something real to target (e.g. "resolve a
// link" needs a hash that exists) - its response's ExtractField is spliced into the main request's
// Path wherever "{{value}}" appears. Not a general templating engine, just enough for the one
// create-then-use shape Lesson Mode's content actually needs.
public record TracedRequestSetup(string ServiceId, string Method, string Path, string? Body, string ExtractField);

// One request a lesson wants to issue itself (not through k6) so the result carries a trace id -
// see LessonTraceService for why this needs its own tiny path instead of reusing ITrafficService.
public record TracedRequestInput(string ServiceId, string Method, string Path, TracedRequestSetup? Setup = null);
