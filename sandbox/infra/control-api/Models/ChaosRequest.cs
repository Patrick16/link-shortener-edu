using System.Text.Json.Serialization;

namespace ControlApi.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ChaosType>))]
public enum ChaosType
{
    Delay,
    Loss,
    Partition,
}

// Amount means delay in milliseconds for Delay, loss percentage for Loss, ignored for Partition
// (partition is always a full 100% cut).
public record ChaosRequest(ChaosType Type, int Amount, int DurationSeconds);

public record ChaosAction(string ServiceId, string ChaosContainerId, ChaosType Type, int DurationSeconds, DateTimeOffset StartedAt);
