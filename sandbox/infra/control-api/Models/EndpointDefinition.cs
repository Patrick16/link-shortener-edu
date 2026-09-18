namespace ControlApi.Models;

// One real HTTP route the flow runner (k6-scripts/flow.js) knows how to call - the server-side
// source of truth for "what can a traffic run actually do", so the k6 script itself stays a dumb,
// generic interpreter with zero knowledge of this app's routes. PathTemplate/BodyTemplate use
// "{{varName}}" placeholders, resolved by flow.js from a per-iteration variable store seeded with
// a few auto-generated values (email/password/originalLink) plus whatever earlier steps in the
// same sequence produced.
public record EndpointDefinition(
    string Id,
    string ServiceId,
    string Method,
    string PathTemplate,
    string? BodyTemplate,
    IReadOnlyDictionary<string, string> Produces,
    IReadOnlyList<string> Consumes,
    string Description);
