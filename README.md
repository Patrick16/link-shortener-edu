# Link Shortener — a high-load patterns playground

An **educational project** built around a deliberately simple product — a link shortener — whose
real purpose is to let you study the patterns and solutions used when designing high-load systems:
caching, async messaging, read replicas, sharding, connection pooling, failover, observability.

The product stays small on purpose. Each scenario layers **one** high-load pattern on top of the
previous one, and you can watch it work (and break) live.

> **A note on the solutions shown here.** Everything in this repository is an *example* of how a
> given pattern can be applied — not a claim that it is the only correct way, or even the best one
> for your case. Trade-offs, alternatives and the mistakes made along the way are part of the
> material; treat the code as a starting point for discussion, not a reference implementation.

## What makes this project different

- **A sandbox with a live architecture diagram.** The whole running system is drawn as an
  interactive graph. Click any node (a service, a database, a queue, a cache, a proxy) to see its
  details, live metrics and controls.
- **Built-in load testing.** Run k6 scenarios against the stack straight from the sandbox UI,
  configure traffic profiles, compare runs and watch the bottleneck move as you change the system.
- **Report analysis and bottleneck hunting.** Every run is saved with the infrastructure settings
  it ran under. You can compare runs side by side, and a guided checklist walks you through finding
  the bottleneck — CPU/memory peaks per node, pool and queue saturation, per-hop trace latency —
  with an optional automatic verdict.
- **Chaos and tuning controls.** Stop, restart, degrade or scale containers, resize connection
  pools, fail over Redis, change replication lag — and see what the system does about it.
- **A real product frontend.** A working UI (register, log in, create a short link, view click
  stats) that you can test by hand, so you can feel what the load tests are doing to a real user.
- **Explanation cards for every component.** Each node has a "Learn" card: what it is, what role it
  plays in the system, and — importantly — **what can go wrong** when you build it, with
  before/after code from real bugs found in this repository.

## A look inside

*Click any image to open it at full resolution.*

### The interactive architecture map

The whole running system as a live graph. Dashed purple lines are traffic flowing right now.

![Architecture map](docs/images/architecture-map.png)

### Node panels: details, metrics and controls

Every node has its own panel — status, CPU/memory, start/stop/degrade, plus component-specific
controls (here: pgcat pooling mode, read/write splitting, pool sizes, live connection counts).

<img src="docs/images/node-panel.png" alt="PgCat node panel" width="380">

### Built-in load testing

Pick the endpoints to hit, preload real data, shape the load profile and run it — with live
VU and iteration charts while the graph animates.

![Load testing](docs/images/load-testing.png)

### Test report

Every run produces a report: throughput, failures, latency percentiles, checks and status codes.
Reports can be exported as Markdown or HTML, and past runs can be compared.

![Test report](docs/images/test-report.png)

### Analyzing a run and finding the bottleneck

A finished run is a saved snapshot: the scenario, the load shape and the state of the infrastructure
at that moment (replicas, pool sizes, caching, prefetch, ...). Two things help you make sense of it:

- **Guided bottleneck search.** A four-step checklist — resource maxima per node, pool/queue
  saturation, per-hop trace latency, the full request waterfall in the tracing UI — with the
  findings for *this* run filled in, plus a table of the slowest hops taken from traces. An
  automatic verdict is available behind a click, deliberately collapsed so that you can try to find
  the culprit yourself first.

<img src="docs/images/bottleneck-analysis.png" alt="Bottleneck analysis panel" width="420">

- **Run comparison.** Tick any two runs and compare them: the rows that differ (load shape, replicas,
  prefetch, ...) are highlighted next to the resulting throughput and latency — which makes "what
  did changing X actually do?" a one-glance answer.

<img src="docs/images/compare-runs.png" alt="Comparison of two runs" width="620">

### Explanation cards

A "Learn" card per component: what it is, what it solves, how it is implemented here (with code),
and the pitfalls hit along the way.

<img src="docs/images/learn-card.png" alt="Learn card for RedirectApi" width="620">

### The product's own frontend

A real UI you can test by hand: register, shorten a link, watch the click counter grow.

![Product UI](docs/images/product-ui.png)

### Observability

Dashboards in Grafana (`-Observability Full`)...

![Grafana dashboard](docs/images/grafana-dashboard.png)

...or traces in the Aspire Dashboard (default), following a request across the RabbitMQ boundary
from LinkApi to ShortenerService.

![Trace in Aspire Dashboard](docs/images/observability.png)

## Scenarios

| # | Scenario | Pattern |
|---|---|---|
| 1 | Minimal stack | Caching (Redis), one Postgres server with isolated databases |
| 2 | Async processing | RabbitMQ between the APIs and their workers |
| 3 | Postgres replicas | Read scaling |
| 4 | pgcat + 2 shards | Write scaling |
| 5 | Multiple pgcat instances | Pooler scaling |

Per-scenario walkthroughs (what is running, request flow, things to try by hand) are in
[`sandbox/docs/scenarios/`](sandbox/docs/scenarios/); the target architecture and current
implementation status are in [`sandbox/docs/architecture.md`](sandbox/docs/architecture.md).

## Tech stack

| Area | Technologies |
|---|---|
| Backend | .NET 10, ASP.NET Core, EF Core, xUnit |
| Product frontend | React 19, TypeScript, Vite, React Router |
| Sandbox frontend | React 19, TypeScript, Vite, React Flow (`@xyflow/react`), SignalR |
| Data | PostgreSQL 16, MongoDB 7 (replica set), Redis 7 (+ Sentinel), SQLite (fallback queue) |
| Messaging | RabbitMQ |
| Infrastructure | Docker Compose, nginx, pgcat (pooler / sharding) |
| Load & chaos | k6, Pumba |
| Observability | OpenTelemetry, Aspire Dashboard — or Prometheus, Jaeger, Loki, Grafana |

## Requirements

- **Docker Desktop** (or Docker Engine with the Compose plugin), running.
- **Node.js** — a version supported by Vite 8 (20.19+ or 22.12+) — and npm, for the two frontends.
- **PowerShell** to run the helper scripts (Windows PowerShell or PowerShell 7+).
- **Free RAM / CPU:** the stack runs a few dozen containers (databases, replicas, Sentinel, Mongo
  replica set, five .NET services, exporters). Plan for roughly **16 GB of RAM** available to
  Docker, more if you enable the full observability stack and run load tests.
  *(This is a rough guideline, not a measured minimum.)*
- The **.NET SDK is only needed if you want to build or test the backend outside Docker**; the
  stack builds its images inside containers.

## Quick start

```powershell
# from the repository root
sandbox\scripts\start-stack.ps1
```

The script starts the backend via Docker Compose, installs frontend dependencies on first run,
launches both frontend dev servers and opens them in the browser. It will ask which observability
stack to use (Aspire Dashboard by default; `Full` for load-test runs).

Once it is up:

| What | URL |
|---|---|
| Product frontend | http://localhost:5173 |
| Sandbox architecture map | http://localhost:5174 |
| AuthApi / LinkApi / RedirectApi docs (Scalar) | http://localhost:8081/scalar/v1 · :8082 · :8083 |
| RabbitMQ UI | http://localhost:15672 (`guest` / `guest`) |
| Aspire Dashboard | http://localhost:18888 |
| Grafana, Prometheus, Jaeger (`-Observability Full`) | http://localhost:3000 · :9090 · :16686 |

Useful variants:

```powershell
sandbox\scripts\start-stack.ps1 -Build                  # rebuild backend images after .NET code changes
sandbox\scripts\start-stack.ps1 -SkipFrontend           # backend only
sandbox\scripts\start-stack.ps1 -Observability Full     # Prometheus + Jaeger + Loki + Grafana
sandbox\scripts\stop-stack.ps1                          # stop everything
sandbox\scripts\stop-stack.ps1 -Wipe                    # also drop the Postgres volume
```

**Where to go first:** open the product frontend, create a link and click it; then open the
architecture map, click through the nodes, and start a small load test.

## Documentation

- [Repository map](docs/repository-map.md) — how the repository is laid out and why.
- [`sandbox/docs/`](sandbox/docs/) — scenarios, node and pattern explanations, pitfalls.
- [`src/docs/`](src/docs/) — documentation of the product's own code (API shapes, DB schema).
