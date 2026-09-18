namespace ControlApi.Models;

public record ScaleRequest(int Replicas);

public record ScaleResult(string ServiceId, int Replicas, bool Success, string Output);
