## What it is

A TCP load balancer in front of [PgCat](node:pgcat)'s 3 replicas — the single entry point every
DB-touching service connects to for Postgres. Nothing connects to a `pgcat` replica directly
anymore; `DB_HOST` points at `haproxy` everywhere (see [Pooler scaling](pattern:pooler-scaling)).

## What it solves

Scaling PgCat to several replicas (see [Pooler scaling](pattern:pooler-scaling) for why) means
something has to pick which replica a given connection actually goes to, and detect one that's
stopped working. A bare round-robin or a DNS round-robin can't tell a pgcat instance that merely
accepts a TCP connection from one that's up but wedged (out of file descriptors, deadlocked,
etc.) — this needs a health check that actually speaks the protocol being proxied.

## How it works

HAProxy runs in `mode tcp` for the real Postgres traffic — it doesn't need to understand Postgres's
wire protocol to balance it, just to pick a backend and pass bytes through unmodified. Health
checking is the one place protocol awareness matters: `option pgsql-check` sends a real Postgres
startup packet and only counts a reply as healthy, which catches a wedged instance a plain TCP
connect check cannot. Balancing is `leastconn` — send the next connection to whichever backend
currently holds the fewest.

A separate HTTP frontend (port 8405) carries operator/observability surfaces only — HAProxy's
built-in stats page (`/stats`) and its native Prometheus exporter (`/metrics`, no sidecar container
needed) — kept off the Postgres-traffic port entirely.

## How it's implemented here

```
# sandbox/infra/haproxy/haproxy.cfg
backend pgcat_back
    balance leastconn
    option pgsql-check user postgres
    # pgcat is one Compose service at deploy.replicas: 3, not 3 separately-named services - its DNS
    # name resolves to however many A records currently exist, not to individually-addressable
    # hostnames. server-template tracks that at runtime instead of a fixed server list.
    server-template pgcat 6 pgcat:6432 check inter 3s fall 3 rise 2 resolvers docker_dns init-addr none
```

`server-template`, not 3 static `server` lines, is load-bearing here, not a style choice — see this
node's Pitfalls for the two real DNS bugs that proved why.

Every DB-touching service's `DB_HOST` default points here (`haproxy:6432`), not at `pgcat` — one
line in `InfraToggleService` is what the pgcat-bypass control actually pivots on.

**The standing "stop/start this container" toggle.** Unlike the pre-existing standing toggles
(nginx bypass, pgcat bypass, cache, messaging mode — each of which reroutes *consumers* around the
thing being bypassed via an env-var recreate), HAProxy also gets a toggle that stops/starts the real
`haproxy` container itself (`InfraToggleService.SetHaproxyEnabledAsync`), so a user can watch every
DB-touching request fail even though PgCat's 3 replicas underneath are perfectly healthy — a live
single-point-of-failure demo for the whole pooler layer, not a graceful bypass. It lives in the
architecture-map UI's global "Topology" modal, nested directly under "Connection pooling (pgcat)"
(not on this node's own panel) — reflecting that it's only meaningful while pgcat is in play. Three
rules govern the coupling: turning pgcat off force-stops haproxy as a side effect; turning pgcat back
on does **not** auto-start haproxy (bringing a node up is always an explicit action); and the
standalone toggle rejects turning haproxy on while pgcat is off. `InfraStatus.HaproxyEnabled` is
deliberately **not cached** — see this node's Pitfalls below for why.

**This toggle also scales [PgCat](node:pgcat)'s replica count, in lockstep.** Turning HAProxy off
collapses `pgcat` to 1 replica; turning it back on scales it back to 3 — see that node's own
"Scales with the HAProxy toggle" section for the mechanics and why (the `×N` badge should always
mean load-balancing is actually happening, not just that containers exist). Scaling `pgcat` *up*
surfaced a real path-resolution bug: `docker compose up --scale` run from inside control-api's own
container needs pgcat.toml's bind-mount path resolved against the Docker daemon's view of the host
filesystem, not control-api's own — fixed by resolving that path live via Docker's own
container-inspect metadata (`GetOwnPgcatConfigHostPathAsync`) rather than a hardcoded relative path.

## Pitfalls

- 🐛 [A statically-resolved backend hostname failed on first boot against Docker's embedded DNS](pitfall:haproxy-static-resolve-races-docker-dns)
  (infrastructure-bug) — fixed
- 🐛 [Scaling a Compose service made per-replica hostnames stop resolving entirely](pitfall:haproxy-server-template-replaces-static-server-list)
  (infrastructure-bug) — fixed
- ⚠️ [A cached "is it running" flag went stale the instant a second, unguarded control path touched the same container](pitfall:haproxy-toggle-cached-state-bypassed-by-generic-controls)
  (concurrency) — fixed
- ⚠️ [A docker compose up --scale call without --no-deps silently restarts whatever it depends on](pitfall:pgcat-scale-missing-no-deps-restarts-stopped-replicas)
  (infrastructure-bug) — fixed

## Relatives

### Nodes

- [PgCat](node:pgcat) — the backend this balances across
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Pooler scaling](pattern:pooler-scaling)
