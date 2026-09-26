namespace ControlApi.Models;

// One rule firing against one node during one run. Evidence is the concrete number that triggered
// it (already formatted for display, since every rule has its own unit/shape - percent, ms,
// waiting-client count) so the UI/verdict never has to re-derive "why" from raw stats. Severity
// only orders suspects within a verdict, it isn't shown as its own field.
public record BottleneckSuspect(string ServiceId, string NodeType, int Severity, string Evidence, string Recommendation);

// The advisor's full output for a run - ranked suspects (worst first) plus the guided-diagnosis
// checklist data (BottleneckAdvisor.BuildChecklist) so "here's the answer" and "here's how I'd
// have found it myself" render off the exact same computation, never two different code paths that
// could quietly disagree.
public record BottleneckVerdict(IReadOnlyList<BottleneckSuspect> Suspects, IReadOnlyList<ChecklistStep> Checklist);

// One step of the guided "how to look for it yourself" panel - Finding is left null when nothing
// noteworthy showed up for that step in this particular run (still rendered, just as "looks
// normal", since a clean signal is itself useful information when learning to read this stuff).
public record ChecklistStep(string Title, string Explanation, string? Finding);
