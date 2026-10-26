# Sandbox User Guide

> A screenshot walkthrough of everything the `sandbox/` learning harness gives you: the scripts
> that start/stop it, the product app, the architecture-map control plane, every infrastructure
> browser UI, and the observability stack. For *why* each pattern exists and the mechanics behind
> it, see [`architecture.md`](architecture.md) and [`scenarios/`](scenarios/) — this guide is about
> *using* the sandbox, not the theory behind it.
>
> Don't confuse the two frontends: `src/frontend/app` is **the product** (the thing an end user of
> a link shortener would actually use). `sandbox/frontend/architecture-map` is **the sandbox's own
> control panel** — a second, separate app for driving and observing the stack. Both are covered
> below.

## Contents

1. [Starting and stopping the stack](#1-starting-and-stopping-the-stack)
2. [What's running, and on which port](#2-whats-running-and-on-which-port)
3. [The product app](#3-the-product-app)
4. [The architecture map](#4-the-architecture-map)
   - [4.1 Layout](#41-layout)
   - [4.2 Reading the graph](#42-reading-the-graph)
   - [4.3 The node panel and generic controls](#43-the-node-panel-and-generic-controls)
   - [4.4 Chaos: Degrade / Heal](#44-chaos-degrade--heal)
   - [4.5 Scaling a service](#45-scaling-a-service)
   - [4.6 Component-specific controls](#46-component-specific-controls)
   - [4.7 Connections (edges)](#47-connections-edges)
   - [4.8 Access info and Learn](#48-access-info-and-learn)
   - [4.9 Pinned metrics](#49-pinned-metrics)
   - [4.10 Load testing with k6](#410-load-testing-with-k6)
   - [4.11 Run history, reuse, and compare](#411-run-history-reuse-and-compare)
   - [4.12 Presets](#412-presets)
   - [4.13 Demonstrating degraded modes](#413-demonstrating-degraded-modes)
5. [Infrastructure browser UIs](#5-infrastructure-browser-uis)
6. [Observability](#6-observability)
7. [API docs (Scalar)](#7-api-docs-scalar)
8. [Database dump / restore](#8-database-dump--restore)
9. [A worked example, start to finish](#9-a-worked-example-start-to-finish)
10. [Where to go next](#10-where-to-go-next)

---

## 1. Starting and stopping the stack

Everything lives under `sandbox/scripts/`. Each `.ps1` (Windows) has a `.sh` twin (Linux/macOS)
with the same flags, kebab-cased (`-SkipMongoUi` → `--skip-mongo-ui`).

```powershell
sandbox\scripts\start-stack.ps1
```

Run with no flags, it's interactive: it asks which **observability** tier to route telemetry to,
and whether to start each optional browser UI (pgweb, RedisInsight, Mongo Express, the two
sqlite-web fallback-queue viewers, the RabbitMQ management UI) and the product frontend. Every
question defaults to **yes** on Enter. Useful flags to skip the prompts:

| Flag | What it does |
|---|---|
| `-Build` | Rebuild backend Docker images first (after changing .NET code) |
| `-Observability Full \| Aspire \| None` | Telemetry sink — see [§6](#6-observability) |
| `-SkipFrontend` | Don't launch *either* frontend dev server |
| `-SkipProductUi` | Skip only the product app (`src/frontend/app`); architecture-map still starts — it's the panel you drive everything else from |
| `-SkipPostgresUi` / `-SkipRedisUi` / `-SkipMongoUi` / `-SkipSqliteUi` / `-SkipRabbitMqUi` | Skip one optional browser UI |
| `-NoBrowser` | Don't auto-open a browser tab once the frontends are up |

The first run also copies two gitignored config files from their `.example` templates:
`sandbox/infra/pgcat/pgcat.toml` (rewritten live by the PgCat pool-settings control — see
[§4.6](#46-component-specific-controls) — so it can't be tracked as-is) and each frontend's
`.env.local`. If you run `docker compose up` by hand instead of the script, copy those yourself
first.

```powershell
sandbox\scripts\stop-stack.ps1            # backend down, Postgres volume kept
sandbox\scripts\stop-stack.ps1 -Wipe      # also drops the Postgres volume (fresh DB next start)
```

`stop-stack` always tears down every optional profile (observability, each UI), regardless of
which ones the run that started them actually used — so it never leaves an orphaned container
behind from an earlier run with different flags.

Manually, from `sandbox/`: `docker compose --profile ui-postgres --profile ui-redis ... up -d` —
each optional piece is its own Compose profile (`otel`, `observability`, `ui-postgres`, `ui-redis`,
`ui-mongo`, `ui-sqlite`); see `docker-compose.yml` for the full list.

---

## 2. What's running, and on which port

| Component | Port | Always on? |
|---|---|---|
| Product app (`src/frontend/app`) | 5173 | unless `-SkipProductUi` |
| Architecture map (control plane) | 5174 | unless `-SkipFrontend` |
| AuthApi / LinkApi / RedirectApi (Scalar docs at `/scalar/v1`) | 8081 / 8082 / 8083 | yes |
| nginx (fronts LinkApi/RedirectApi for load balancing) | 8082 / 8083 | yes |
| Postgres primary / replica 1 / replica 2 | 5432 / 5433 / 5434 | yes |
| HAProxy (pooler entry point, load-balances 3 PgCat instances) | 6432 | yes |
| Redis master (via Sentinels, not dialed directly) | 6379 | yes |
| Redis Sentinels 1 / 2 / 3 | 26379 / 26380 / 26381 | yes |
| RabbitMQ (AMQP / management UI) | 5672 / 15672 | UI: unless `-SkipRabbitMqUi` |
| Mongo 1 / 2 / 3 (replica set `rs0`) | 27017 / 27018 / 27019 | yes |
| control-api (drives everything above) | 127.0.0.1:5299 | yes |
| pgweb | 8084 | unless `-SkipPostgresUi` |
| RedisInsight | 5540 | unless `-SkipRedisUi` |
| Mongo Express | 8085 | unless `-SkipMongoUi` |
| LinkApi / RedirectApi fallback-queue viewers (sqlite-web) | 8086 / 8087 | unless `-SkipSqliteUi` |
| Aspire Dashboard | 18888 | `-Observability Aspire` (or `Full`, idle) |
| Grafana / Prometheus / Jaeger / Loki | 3000 / 9090 / 16686 / 3100 | `-Observability Full` |

`control-api` is bound to `127.0.0.1` only, not every interface — it holds the Docker socket
(root-equivalent control over the whole stack) with no authentication on top, so it's reachable
only from the machine running it.

This table ties ports to the start-script flags that gate them; it skips a few things that aren't
flag-gated (ClickHouse, ReportingApi, RabbitMQ's Prometheus metrics port, individual observability
services). For the exhaustive list straight from `docker-compose.yml`, see
[`ports.md`](./ports.md).

---

## 3. The product app

This is "the product" — what you'd actually deploy. Three things it can do:

**Shorten a link**, anonymously or signed in:

![Product app home page — shorten a link](assets/user-guide/product-app-home.jpg)

After submitting, the short link appears immediately together with a note that creation is
asynchronous — `LinkApi` returns synchronously, but `ShortenerService` persists the row a moment
later off a RabbitMQ event, so visiting the link *immediately* can occasionally 404 for a few
milliseconds:

![A freshly created short link, with the eventual-consistency note](assets/user-guide/product-app-link-created.jpg)

**Register / sign in** — a JWT is issued and stored client-side; no token is ever required to
create a link (`userId` just stays empty on it):

![Sign-in and registration forms](assets/user-guide/product-app-register.jpg)

**My links**, once signed in — every link you've created, with a **Stats** button per link:

![My links list for a signed-in user](assets/user-guide/product-app-my-links.jpg)

Opening Stats pulls from the click-tracking pipeline (`RedirectApi` → RabbitMQ → `TrafficService`
→ Postgres + Mongo): total clicks, a by-day breakdown, and top countries/devices parsed from the
User-Agent and geo-IP:

![Per-link click statistics](assets/user-guide/product-app-stats.jpg)

---

## 4. The architecture map

Open `http://localhost:5174`. This is the sandbox's own control plane: a live graph of every
container, with a panel per node for inspecting it, controlling it, injecting chaos into it, and —
separately — a load-testing tool for driving traffic through the whole stack and analyzing the
result.

### 4.1 Layout

Three fixed zones: a header (title, connection status, **Topology** and **Presets** buttons, and a
strip showing the current/last load-test run), a resizable **left sidebar** whose content depends
entirely on what's selected, and the **graph** itself in the center, with a metrics overlay that
can float on top of it.

![The full stack rendered as a graph](assets/user-guide/architecture-map-overview.jpg)

The sidebar shows exactly one of: **run history** (nothing selected — the default), the **k6
config panel** (the k6 node selected), a **node panel** (any other node), or a **connection
detail** panel (an edge selected). Selecting anything expands the sidebar if it was collapsed.

### 4.2 Reading the graph

Node positions come from an automatic layout (dagre), not hand-placed coordinates, so the exact
arrangement can shift slightly between loads — the topology itself doesn't. Each node shows an
icon, a status dot, a replica-count badge if scaled past 1, and — for Redis/Mongo — a live
role badge (`MASTER`/`REPLICA`, `PRIMARY`/`SECONDARY`), since those roles can change on failover
independently of the node's static id. Click a node to open its panel; click an edge (the
connection between two nodes) to see what that connection is for.

### 4.3 The node panel and generic controls

Every container gets the same four buttons for free, straight off Docker — nothing to configure
per node for these:

![PgCat's node panel: header, quick links, and the generic Stop / Restart / Heal / Degrade row](assets/user-guide/pgcat-panel-1.jpg)

- **Start / Stop** — one button whose label flips with current state.
- **Restart** — straightforward container restart.
- **Heal** — reverses an active chaos injection on this service early (see [§4.4](#44-chaos-degrade--heal)).
- **Degrade** — opens the chaos sub-panel.

Below that: a **Pin metrics** toggle (adds this node's live CPU/RAM to the floating overlay — see
[§4.9](#49-pinned-metrics)), instance status, then any capability-specific controls this node
declares (see [§4.6](#46-component-specific-controls)), small per-instance CPU/Memory sparklines
if history exists, and finally a static **details card** pulled straight from
`architecture.json` — purpose, technologies, port, and links to the relevant config/code:

![LinkApi's panel: Scale control, live sparklines, and its static details card](assets/user-guide/linkapi-panel-scale-sparkline.jpg)

A node with no controllable container (e.g. a database considered as a logical entity rather than
a container) just shows the details card.

### 4.4 Chaos: Degrade / Heal

Clicking **Degrade** expands a small form. Pick a chaos type from the dropdown first — the panel
shows a one-line explanation of what that type actually does right below it, and the field(s)
under that change to match: an amount field labeled for the type you picked (**Delay (ms)** or
**Loss (%)** — **Partition** has none, since it's always a full 100% cut), plus a **Duration (s)**
field that applies to all three. Hit **Start** once both look right:

![The Degrade sub-panel, expanded on PgCat, with Loss selected](assets/user-guide/pgcat-degrade-chaos.jpg)

**The three chaos types, in order of severity:**

- **Delay** adds latency to every packet in and out of the container — nothing is dropped, every
  request still arrives, just later. Good for feeling what a slow dependency (a distant service, a
  congested link) does to the rest of the system without anything actually failing. The amount is
  milliseconds of added latency per packet (1–10000).
- **Loss** randomly drops the given percentage of packets in and out of the container. Most
  requests still get through; a random subset silently don't, which is what makes it a good stand-in
  for a flaky, unreliable network link (as opposed to Delay's "slow but reliable" or Partition's
  "reliably nothing"). The amount is a percentage (1–100).
- **Partition** cuts all network traffic in and out of the container — 100% loss, no amount field
  because there's nothing to tune. This is the closest thing to actually unplugging the container's
  network cable: the harshest of the three, useful for seeing what a clean total outage looks like
  (as opposed to Loss's partial, intermittent failures).

Under the hood this spins up a throwaway [Pumba](https://github.com/alexei-led/pumba) container
that runs `pumba netem` against the target container's network namespace for the given duration,
then reverts automatically — Duration is how long the effect lasts, not a delay before it starts.
**Heal** (next to Degrade on the same row) stops that Pumba container early if you don't want to
wait out the timer. This applies uniformly to any node with the generic controls (PgCat, Redis,
RabbitMQ, or any service) — degrade one, then watch the graph, the metrics, and a k6 run react.

### 4.5 Scaling a service

Any node whose component declares the `scalable` capability (LinkApi, RedirectApi,
ShortenerService, TrafficService) gets a **Replicas** field and a **Scale to N** button — visible
in the LinkApi screenshot above. Scaling above 20 replicas asks for confirmation first (resource
exhaustion on a laptop-class Docker host is a real risk); the backend caps it at 100 regardless.
nginx re-resolves `link-api`/`redirect-api`'s DNS on a short TTL, so new replicas start receiving
traffic without a config reload.

### 4.6 Component-specific controls

These are declared per node in `architecture.json`'s `capabilities` array and rendered
automatically — see [`adding-a-component.md`](adding-a-component.md) if you want to add a new one.
Here's what each one actually does, grouped by the node that carries it.

**Topology** (header button, global — not tied to any node) is the odd one out: it used to be four
single-purpose per-node toggles (nginx bypass, PgCat on/off, Redis caching on/off, messaging
transport), each applying the instant it was flipped from its own node's panel. They were retired
and replaced with one panel that stages every change locally and only applies them together on
**Save** — so a half-finished edit never mid-air recreates containers. It has grown since: eight
toggles today — load balancing (nginx), connection pooling (PgCat) and HAProxy in front of it,
Postgres read replicas, Redis and Redis Sentinel (HA), MongoDB replicas, and the messaging
transport (RabbitMQ ⇄ gRPC) — each staged independently and diffed against the live configuration
before Save does anything.

![The Topology panel: Messaging transport set to gRPC (sync), with its warning visible](assets/user-guide/topology-panel-messaging.jpg)

Switching **Messaging transport** to **gRPC (sync)** shows this warning right there in the panel:
it's a demo-only mode (built to let you *feel* synchronous coupling's latency cost against the
default async path), not a production option, and it silently breaks RabbitMQ's pub/sub fan-out —
`LinkApi`/`RedirectApi` dispatch straight to the downstream worker instead of publishing, so
`ShortenerService`'s click-count bump and `ReportingService`'s ClickHouse write stop receiving
events while it's active. The same warning reappears on `RedirectApi`'s, `ShortenerService`'s and
`ReportingService`'s own node panels for as long as gRPC mode stays on, so it isn't forgotten if
you come back to one of them later without revisiting Topology:

![RedirectApi's node panel with the persistent gRPC-mode warning](assets/user-guide/redirect-api-grpc-warning.jpg)

**PgCat** — three capabilities on one node (connection pooling on/off moved into the Topology panel
above):

- *Pool settings* — pool mode (Transaction/Session), read/write splitting on/off, and pool size per
  logical database — all three pools (`users_db`/`links_db`/`clicks_db`) at once. Hot-reloaded by
  PgCat itself in ~15s, no container recreate.
- *Npgsql client pool size* — the **client-side** pool (distinct from PgCat's own server-side one),
  across every DB-touching service. Recreates those 5 services.
- *Connections* — a read-only table, polled every 3s, of client vs. server connection counts per
  pool, **broken out per replica** (`pgcat-1`/`pgcat-2`/`pgcat-3`) since PgCat is 3 identical
  instances behind HAProxy now, not one — see
  [Pooler scaling](scenarios/patterns/pooler-scaling.md).

![Pool settings: mode, read/write splitting, pool size, Npgsql pool size](assets/user-guide/pgcat-panel-2.jpg)

![PgCat's live per-replica connections table and CPU/Memory sparklines](assets/user-guide/pgcat-panel-3-connections.jpg)

**HAProxy** — the load balancer in front of PgCat's 3 replicas gets one read-only capability: live
per-backend status and session counts, polled from HAProxy's own stats endpoint (no docker-exec,
unlike every other capability above):

![HAProxy's panel: 3 pgcat backends UP, 3 reserved slots DOWN (unused headroom)](assets/user-guide/haproxy-panel-backends.jpg)

`pgcat4`/`pgcat5`/`pgcat6` showing `DOWN` is expected, not a problem — `server-template` reserves 2×
headroom over the 3 replicas that actually exist (see [HAProxy](scenarios/nodes/haproxy.md)), and an
unused slot has nothing to connect to.

**Redis (master)** — a manual flush (the caching on/off toggle moved into the Topology panel above,
in lockstep with Redis on/off itself — see that toggle's own description):

![Redis (master)'s node panel: generic controls plus "Flush cache (cold start)"](assets/user-guide/redis-cache-toggle-flush.jpg)

**Flush cache** just empties Redis, simulating a cold start under load.

**Redis Sentinels** — failover tuning, shared across all three Sentinel containers at once:

![Down-after / Quorum / Failover timeout](assets/user-guide/sentinel-config.jpg)

**Mongo (any of the 3 members)** — read preference for `traffic-service`'s reads only (writes
always go to whichever member is currently primary):

![Primary vs. Secondary-preferred reads](assets/user-guide/mongo-read-preference.jpg)

**Postgres replicas** — inject an artificial replication delay on one specific standby
(`ALTER SYSTEM SET recovery_min_apply_delay` + reload — no restart, independent per replica):

![Replication lag control on Postgres Replica 1](assets/user-guide/replication-lag.jpg)

**Logical databases** (`users_db` / `links_db` / `clicks_db`) — a read-only live connection count
for that specific database:

![links_db: 4 backend connections, live](assets/user-guide/postgres-connections.jpg)

**RabbitMQ** — consumer prefetch (QoS), shared by `shortener-service` and `traffic-service`. Only
read once at consumer startup, so changing it recreates both services:

![RabbitMQ's prefetch control](assets/user-guide/rabbitmq-prefetch.jpg)

**nginx** has no component-specific control of its own anymore — its load-balancing bypass moved
into the Topology panel above (off routes k6's generated traffic straight to a single
link-api/redirect-api container, bypassing nginx, which keeps running regardless — useful for
isolating "is nginx itself the bottleneck"). Its node panel is just the generic Stop/Restart/Heal/
Degrade row, CPU/memory, and the static details card:

![nginx's node panel: only the generic controls, no toggle of its own](assets/user-guide/nginx-panel.jpg)

### 4.7 Connections (edges)

Click any edge in the graph to see what that connection actually is — its purpose, protocol,
payload format (when relevant), whether it's synchronous or asynchronous, and any extra note:

![nginx → LinkApi: Proxy / create / look up a link](assets/user-guide/connection-detail.jpg)

### 4.8 Access info and Learn

Every controllable node has two buttons next to its name. **Access info** opens a modal with
ready-to-copy connection details — host/port, a `psql`/`redis-cli`/etc. command, credentials, and a
full connection string, all dev-only values (never real secrets):

![PgCat's Access Info modal — psql command, password, connection string](assets/user-guide/access-info-modal.jpg)

**Learn** opens a documentation panel rendered straight from the educational docs under
`sandbox/docs/scenarios/` — what the component solves, how it works, how it's actually configured,
and known pitfalls, each as an internal link you can click through to the full write-up:

![The Learn modal for PgCat, showing its pitfalls list](assets/user-guide/learn-modal.jpg)

### 4.9 Pinned metrics

Clicking **Pin metrics** on any node panel adds that node's live CPU/RAM/connection numbers to a
small overlay panel fixed to the top-right of the graph canvas — independent of whatever you have
selected in the sidebar, so you can keep an eye on a handful of nodes while clicking around
elsewhere. Columns are click-to-sort; each row can be unpinned individually.

### 4.10 Load testing with k6

Click the **k6 (load test)** node to open its configuration panel. It isn't a real
docker-compose service with persistent state — it's a one-off container control-api spins up per
run — so selecting it only configures the *next* run, nothing live. For what actually matters once
you're pushing real throughput (config knobs, real bugs only load reveals), see
[`load-testing.md`](load-testing.md) — this section is only the UI mechanics.

**What to call** — pick a service and one of its real endpoints, then **+ Add step** to append it
to an ordered sequence (order matters: a "Create link" step produces a hash a later "Resolve link"
step can consume). Each step gets a configurable pause-after and can be reordered or removed:

![Picking an endpoint and building a sequence](assets/user-guide/k6-config-panel-1.jpg)

![A sequence with one step added, showing pause/reorder/remove controls](assets/user-guide/k6-config-step-added.jpg)

**Test data** — an opt-in "Preload real data for steps that need it." If your sequence has no
"Create" step of its own (e.g. you're only testing "Resolve link"), every iteration would otherwise
hit the same single fixture record; this preloads a configurable number of real records from the
live app first.

**How much load** — either run for a fixed duration (pick a ramp preset, then fine-tune it in an
interactive point graph: click empty space to add a ramp point, drag to reshape, double-click a
point to remove it) or run a fixed number of iterations regardless of elapsed time:

![The stage-graph editor: VUs ramping over time](assets/user-guide/k6-stage-graph-editor.jpg)

**Save this combination** — a **custom scenario** is simply this whole combination (sequence +
load shape + data-pool settings) saved under a name you can reload later. This is a different,
narrower concept than a **preset** (see [§4.12](#412-presets)), which snapshots the *infra*
configuration instead and can optionally link to one of these scenarios:

![Saving the current combination as a named custom scenario](assets/user-guide/k6-save-scenario.jpg)

Hit **Run traffic**. While it runs, the header strip shows a live progress bar and charts (active
VUs, iterations/sec); **Cancel run** is available the whole time. The moment the run finishes, its
report pops up automatically in a modal (the same one described in [§4.11](#411-run-history-reuse-and-compare)
below) instead of growing the header strip inline; close it and reopen it later with the **View
results** button that replaces the progress toggle once a run is no longer active.

### 4.11 Run history, reuse, and compare

With nothing selected, the sidebar defaults to **run history** — every past run, newest first,
with its scenario name, timestamp, request count/rate, and failure count:

![Past runs list, with real timestamps and rates](assets/user-guide/run-history-panel.jpg)

Opening one pops up its full detail in a modal over the list — the list itself stays visible
underneath, dimmed; close the modal (click outside it, press Escape, or hit the **×**) to get back
to it. The modal shows the run's step sequence, the **infra configuration it ran under** (load
balancing/pooling/caching on/off, any non-default replica counts, PgCat pool settings, Postgres
connection counts at the time) — useful for understanding *why* two runs performed differently —
and a **Reuse config** button that reloads that run's sequence and ramp straight back into the k6
panel and reapplies its infra settings:

![A run's detail: steps and the infra snapshot at the time it ran](assets/user-guide/run-detail-top.jpg)

Below that, inside the same modal, is the full report: stat tiles, a latency breakdown, status
codes per endpoint, the raw k6 output, and — the most useful part for actually learning from a run
— the **bottleneck panel**:

![Stat tiles: 32000 requests, 0 failed, 10 max VUs](assets/user-guide/traffic-report-stats.jpg)

![The "How to find the bottleneck" teaching checklist, plus checks and status codes](assets/user-guide/bottleneck-panel.jpg)

The bottleneck panel is deliberately two things at once: an always-open numbered checklist that
*teaches the diagnostic method* (resource usage per node, pool/queue saturation, trace hop
latency, ...), and a collapsed automatic verdict ranking suspect services with evidence — useful
once you already know roughly what to look for. Expanding the verdict on a run saved after this
feature shipped shows a one-line caption naming the exact thresholds it was judged against (e.g.
"Judged against: CPU > 85% · memory > 90% · hop p95 > 300ms · error rate > 5%") — these come from
`control-api`'s `appsettings.json` (`BottleneckThresholds` section) rather than being hardcoded, so
tune them there and restart `control-api` if the defaults don't fit your machine or scenario.

If you've run the same scenario before, a **vs previous run of this scenario** panel appears
automatically right above the bottleneck panel, with no extra steps — it's computed server-side
when the run is saved, not on demand. It's a table of throughput, error rate, and p95 latency (p95
only when both runs have a k6 summary), each row showing this run's value, the previous run's
value, and a percent-change cell colored red (regressed), green (improved), or gray (unchanged).
This is distinct from the manual **Compare** below: it's always "this run vs. the one immediately
before it in the same scenario," with no run picker, and it only ever compares results, not infra
configuration. The very first run of a scenario simply shows no such panel yet.

Select two or more runs with their checkboxes and hit **Compare** for a side-by-side table split
into Configuration and Results rows, with every differing value highlighted:

![Comparing two runs' configuration side by side](assets/user-guide/compare-runs-modal.jpg)

### 4.12 Presets

The header's **Presets** button is global, not tied to any node. A preset snapshots the *entire*
infra configuration (every toggle and pool setting above, replica counts, ...) under a name, and
optionally links to one custom scenario so applying it also loads that load profile:

![The Presets list, with "Save current config as new preset"](assets/user-guide/presets-modal.jpg)

**Preview & apply** diffs the preset against the stand's current live configuration before doing
anything — nothing changes until you confirm:

![Previewing a preset — here, the stand already matches it](assets/user-guide/presets-preview.jpg)

### 4.13 Demonstrating degraded modes

Two of this project's dependencies are explicitly allowed to fail without taking the product down
— graceful degradation is a feature here, not an accident, and it's worth seeing on purpose rather
than only reading about:

- **Stop `redis-master`** ([§4.3](#43-the-node-panel-and-generic-controls)'s generic Stop button).
  Link creation and redirects keep working — `LinkApi`/`RedirectApi` fall back to reading/writing
  Postgres directly, just slower. This used to 500 instead before a real bug was found and fixed;
  see [the caching pattern doc](scenarios/patterns/caching.md) and
  [that pitfall's before/after](scenarios/pitfalls/cache-read-no-error-handling.md).
- **Stop `rabbitmq`**. `POST /Links` and clicks both keep returning successfully —
  `LinkApi`/`RedirectApi` spill into a local SQLite fallback queue instead of blocking on the
  publish. Watch it fill via the fallback-queue viewers ([§5](#5-infrastructure-browser-uis),
  ports 8086/8087); restart `rabbitmq` and `RabbitMqRetryWorker` drains the queue within 30s. See
  [the async-messaging pattern doc](scenarios/patterns/async-messaging.md) for the mechanism, and
  its Pitfalls section for two real bugs found in this exact fallback path.

Either way, the product app and `k6` traffic keep running throughout — that continuity *is* the
demonstration. Compare with stopping `postgres` itself, which has no fallback by design, to see
what the absence of a degraded mode looks like instead.

---

## 5. Infrastructure browser UIs

Each of these is a real third-party admin tool, wired up to point at the sandbox's own containers
— not built by this project, just configured for it.

**RabbitMQ management UI** (`:15672`, `guest`/`guest`) — overview with live message-rate graphs,
and a list of every real queue the application declares at connection time (not from a static
definitions file — see [`02-async.md`](scenarios/02-async.md)):

![RabbitMQ overview: queued messages and message rates](assets/user-guide/rabbitmq-ui-overview.jpg)

![The real queue topology — one queue per consumer per event type](assets/user-guide/rabbitmq-queues.jpg)

**pgweb** (`:8084`) — connects straight to the Postgres primary (not through PgCat — there's
nothing pooler-specific to browse), opened on `links_db` by default; switch databases from its own
connection screen:

![Browsing the links table in pgweb](assets/user-guide/pgweb-rows.jpg)

**RedisInsight** (`:5540`) — no database is pre-registered (it isn't Sentinel-aware, so point it at
whichever node is currently master — `redis-master`, port `6379` — and re-add if a real failover
promotes a different node):

![RedisInsight's empty state before adding a connection](assets/user-guide/redisinsight-empty.jpg)

![Connected and browsing keys](assets/user-guide/redisinsight-browser.jpg)

**Mongo Express** (`:8085`) — browse `clicks_meta_db`, including the actual click-metadata
documents TrafficService writes (parsed User-Agent, IP, geo):

![Mongo Express home — clicks_meta_db and its collection](assets/user-guide/mongo-express-home.jpg)

![Click-metadata documents, with real UserAgent/IpAddress data from a load test](assets/user-guide/mongo-express-clicks.jpg)

**sqlite-web fallback-queue viewers** (`:8086` LinkApi, `:8087` RedirectApi) — browse the local
SQLite file each API falls back to writing when RabbitMQ is briefly unreachable. Deliberately
**read-only** (`-r` flag plus a `:ro` volume mount, belt and suspenders): a fallback row deleted
through the viewer would be a message permanently lost, defeating the whole point of the
outage-recovery feature it's there to let you observe:

![sqlite-web, opened read-only against the fallback queue](assets/user-guide/sqlite-web-fallback.jpg)

---

## 6. Observability

Every .NET service ships logs/metrics/traces (OpenTelemetry, OTLP) to a shared
`otel-collector`, which fans out differently depending on `-Observability`:

- **`Aspire`** (lightweight) — straight to a standalone Aspire Dashboard (`:18888`). Fine for
  everyday dev; its in-memory store isn't meant for load-test volume.
- **`Full`** (default, meant for load-test runs) — Prometheus + Jaeger + Loki + Grafana instead.
  Grafana ships three pre-provisioned dashboards (ASP.NET Core, Logs, Metrics, Traces) wired to
  those sources:

![Grafana's provisioned dashboard list](assets/user-guide/grafana-dashboards-list.jpg)

![The ASP.NET Core dashboard — requests, errors, connections per service](assets/user-guide/grafana-aspnet-dashboard.jpg)

- **`None`** — services still run; they just drop their telemetry on the floor.

RabbitMQ publish/consume spans are hand-instrumented (no mainstream auto-instrumentation exists for
`RabbitMQ.Client`), so a single trace spans the async boundary end to end — one of the clearest
ways to actually *see* what "decoupled write path" means. Open **Jaeger** (`:16686`), search
service `LinkApi`, operation `POST Links`:

![A full trace: LinkApi → link.created publish → ShortenerService, across the async boundary](assets/user-guide/jaeger-trace-waterfall.jpg)

---

## 7. API docs (Scalar)

AuthApi, LinkApi, and RedirectApi each expose interactive API docs at `/scalar/v1` — every
endpoint, request/response shapes, and a built-in client to try requests directly from the browser
(curl, or a language of your choice):

![LinkApi's Scalar reference](assets/user-guide/scalar-api-docs.jpg)

---

## 8. Database dump / restore

One pair of scripts per store this project writes to — same shape, same `sandbox/backups/` output
directory (gitignored), Linux/macOS `.sh` twins alongside every `.ps1` below.

```powershell
sandbox\scripts\dump-db.ps1                                   # -> sandbox/backups/postgres-<timestamp>.sql
sandbox\scripts\restore-db.ps1                                 # restores the newest file in sandbox/backups/
sandbox\scripts\restore-db.ps1 -DumpFile '.\sandbox\backups\postgres-20260918-120000.sql'

sandbox\scripts\dump-mongo.ps1                                 # -> sandbox/backups/mongo-<timestamp>.archive.gz
sandbox\scripts\restore-mongo.ps1

sandbox\scripts\dump-clickhouse.ps1                            # -> sandbox/backups/clickhouse-reports_db.clicks-<timestamp>.native
sandbox\scripts\restore-clickhouse.ps1

sandbox\scripts\dump-all.ps1                                   # runs all three dump-*.ps1 in sequence
sandbox\scripts\restore-all.ps1                                # runs all three restore-*.ps1 against the newest dump of each kind
```

`dump-db` runs `pg_dumpall` against the running `postgres` container — all three databases
(`users_db`/`links_db`/`clicks_db`) in one `.sql` file, for archiving or moving to another machine.
`restore-db` replays it against the `postgres` maintenance database (the dump already contains its
own `CREATE DATABASE` statements).

`dump-mongo`/`restore-mongo` use `mongodump`/`mongorestore --archive --gzip` against `clicks_meta_db`,
connected via the full `rs0` replica-set URI rather than straight to `mongo1` — Mongo can fail over
to any of the three nodes with zero involvement from this project (same as Redis Sentinel), so the
URI is what lets the driver find whichever node is actually primary right now instead of assuming
it's always `mongo1`.

`dump-clickhouse`/`restore-clickhouse` export/import `reports_db.clicks` in ClickHouse's own Native
format (`restore` `TRUNCATE`s the table first, a full replace rather than a merge on top of
whatever's already there).

---

## 9. A worked example, start to finish

A short loop that touches most of what's above:

1. `sandbox\scripts\start-stack.ps1` (defaults are fine).
2. In the product app (`:5173`), create a link and visit it a couple of times.
3. In architecture-map (`:5174`), click **RedirectApi** → **Access info**, or just watch the
   **Pin metrics** overlay react.
4. Open **Jaeger** (`:16686`) and find the trace for your request — see the full hop from
   `RedirectApi` through RabbitMQ to `TrafficService`.
5. Back in architecture-map, click **PgCat** → **Degrade** → Delay 500 ms, 30 s, **Start**.
6. Click the **k6** node, add a `redirect-api` → *Resolve link* step, enable **Preload real data**,
   run for a short duration.
7. Watch the live VUs/iterations chart, then open the finished report's **bottleneck panel** — with
   PgCat artificially slowed, the latency hop should point straight at it.
8. **Heal** PgCat, re-run the same scenario, and **Compare** the two runs.

---

## 10. Where to go next

- [`architecture.md`](architecture.md) — target end-state architecture and current implementation status.
- [`scenarios/`](scenarios/) — per-scenario deep dives (`01-minimal.md`, `02-async.md`, ...), plus
  `scenarios/nodes/` (per-component docs, the same content the **Learn** modal renders),
  `scenarios/patterns/`, and `scenarios/pitfalls/` (real bugs found and fixed, with before/after
  snippets).
- [`pgcat-pool-sizing.md`](pgcat-pool-sizing.md) — the reasoning behind PgCat's default pool sizes.
- [`load-testing.md`](load-testing.md) — config that actually matters for real throughput, and real
  bugs found only by running a load test against the live stack.
- [`adding-a-component.md`](adding-a-component.md) — how to add a new node or a new
  component-specific control to architecture-map yourself.
