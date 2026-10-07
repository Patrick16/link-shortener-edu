using Docker.DotNet;
using Docker.DotNet.Models;

namespace ControlApi.Services;

// Shared low-level Docker access every domain service builds on: one DockerClient instance, compose
// project/network identity, and the "shell out / exec into a container" primitives that show up in
// nearly every domain service (FindAsync, ExecAsync, EnsureImageAsync, ...). Extracted out of the
// former god-class DockerService so each domain service (chaos, pgcat, postgres, redis, mongo,
// rabbitmq, ...) depends on exactly this, not on every unrelated capability bundled together.
public interface IContainerRuntime
{
    DockerClient Client { get; }

    string ComposeProject { get; }

    string ComposeNetwork { get; }

    // For an unscaled service this is Docker's only match. For a scaled one, the lowest
    // container-number wins (deterministic, matches the frontend's own instances[0] convention).
    Task<ContainerListResponse?> FindAsync(string serviceId, CancellationToken ct);

    // Runs a command inside a container via Docker's exec API and returns whichever of
    // stdout/stderr actually has content.
    Task<string> ExecAsync(string containerId, IList<string> cmd, CancellationToken ct);

    // Like ExecAsync, but also returns the exit code - needed by write-paths that mutate container
    // state (ALTER SYSTEM, SENTINEL SET), where docker exec always succeeds at *starting* the
    // command, so a failed one only shows up in this exit code, never as an exception.
    Task<(long ExitCode, string Output)> ExecWithExitCodeAsync(string containerId, IList<string> cmd, CancellationToken ct);

    // Pulls the image only if it isn't already present locally.
    Task EnsureImageAsync(string image, CancellationToken ct);

    Task<string?> GetContainerIpAsync(string containerId, CancellationToken ct);
}
