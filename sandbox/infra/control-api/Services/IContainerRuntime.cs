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

    // Shells out to the real `docker compose` CLI rather than reimplementing its logic - shared by
    // ScaleAsync and every standing-env toggle, just with different args/env. extraEnv is merged
    // into the child process's own environment so docker compose's `${VAR}` interpolation in
    // docker-compose.yml picks it up when re-rendering the target services' config. Every
    // invocation is serialized through one instance-wide gate (ContainerRuntime is a singleton, so
    // one gate covers every caller): `docker compose up --scale`/`up --force-recreate` isn't
    // designed to run concurrently against the same project - two overlapping invocations can race
    // on container-number slots or on which value docker-compose's ${VAR} interpolation actually
    // picks up for a service both calls happen to touch.
    Task<(int ExitCode, string Output)> RunComposeAsync(IEnumerable<string> args, IDictionary<string, string>? extraEnv, CancellationToken ct);

    Task<int> CountReplicasAsync(string serviceId, CancellationToken ct);

    // `up --force-recreate` with no `--scale` tells compose the desired replica count for each
    // service is whatever the compose file says (1) - it has no memory of a scale-up done in a
    // previous `up` invocation. Any standing-env toggle that force-recreates a scalable service
    // needs to re-assert its current replica count via this, or a scale-up done through
    // IContainerLifecycleService.ScaleAsync collapses back to a single instance the moment that
    // toggle flips.
    Task<List<string>> BuildPreserveScaleArgsAsync(IReadOnlyList<string> services, CancellationToken ct);

    // Runs `action` under a second gate, separate from the one RunComposeAsync holds internally:
    // that one only serializes the docker-compose subprocess itself, not a whole
    // read-current-state-then-recreate-then-write-state-back sequence around it. Every standing-env
    // toggle handler (read CurrentStandingEnv, recreate, write the field back) and
    // IContainerLifecycleService.ScaleAsync (whose write is exactly what a toggle handler's
    // replica-count read can otherwise go stale against) share this same gate, so each one's full
    // read-recreate-write is atomic relative to every other one - without it, two concurrent calls
    // could each snapshot state before either writes back, and whichever recreates second does so
    // with a stale snapshot that silently reverts the first call's change even though it already
    // reported success.
    Task<T> RunExclusiveToggleAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}
