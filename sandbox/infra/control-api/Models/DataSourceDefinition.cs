namespace ControlApi.Models;

// A bulk, paginated read used to preload a pool of real values before a run starts (see
// DataPoolRequest) - distinct from EndpointDefinition/FlowStep, which describe one call made once
// per k6 iteration. This runs once, outside k6, straight against the real app, and hands the whole
// pool to flow.js as the starting value for whichever variable ProducesVar names (e.g. "hash"), so
// a step consuming that variable without an earlier step in the same sequence producing it fresh
// (testing "Resolve link" on its own, say) draws from real, varied records instead of hammering the
// one fixture link every iteration.
public record DataSourceDefinition(string Id, string ServiceId, string ProducesVar, string Description);
