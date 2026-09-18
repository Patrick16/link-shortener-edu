namespace ControlApi.Models;

// ContainerNumber distinguishes replicas of the same ServiceId (docker compose's own
// com.docker.compose.container-number label, 1-based) - always 1 for non-scaled services.
public record ManagedContainer(string ServiceId, string ContainerId, string State, string Status, int ContainerNumber);
