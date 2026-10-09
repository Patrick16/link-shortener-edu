namespace ControlApi.Models;

public sealed record HaproxyStats(IReadOnlyList<HaproxyServerStats> Servers);

public sealed record HaproxyServerStats(string Name, bool Up, int CurrentSessions);
