# ClickHouse's healthcheck could pass before its init script finished creating the table

**Category:** infrastructure-bug **Status:** fixed

Watch for a healthcheck that only proves "the server process is accepting connections," not "the
thing depending on it can actually do useful work yet." `clickhouse`'s `docker-compose.yml`
healthcheck originally probed `GET /ping` — which ClickHouse's HTTP listener answers as soon as it
starts, before `/docker-entrypoint-initdb.d/init.sql` (this project's own schema file, see
`sandbox/infra/clickhouse/init.sql`) has necessarily finished running and created
`reports_db.clicks`. `reporting-service`/`reporting-api` both gate their own startup on
`clickhouse: condition: service_healthy`, so the dependency was declared correctly — the
healthcheck itself just wasn't checking the right thing.

On a genuinely fresh volume (first `start-stack.ps1` run, or after `-Wipe`), this let the listener
answer "healthy" while the init script was still mid-run, so `reporting-service`'s first consumed
batch (or `reporting-api`'s first query) could hit "table `reports_db.clicks` doesn't exist" —
exactly once, on cold start, hard to reproduce once the volume already has data. This project had
already hit the identical shape of bug once before with Postgres streaming replicas (a healthcheck
that proves the process is up, not that the thing it's gating on is actually ready) — same lesson,
a different component this time.

**No real before/after diff exists for this one** — caught and fixed in the same uncommitted
working-tree session the feature was built in, before the first commit, so there's no prior commit
showing the buggy healthcheck. Reconstructed from the finding's own description.

⚠️ **Mistake** — a liveness probe used to gate a readiness dependency:

```yaml
# reconstructed shape, not a real diff — see note above
healthcheck:
  test: ["CMD", "wget", "--spider", "http://localhost:8123/ping"]
```

✅ **Do this instead** — `sandbox/docker-compose.yml`'s `clickhouse` service probes the actual
table the dependents need, which can only succeed once `init.sql` has created it:

```yaml
healthcheck:
  test: ["CMD", "clickhouse-client", "--query", "SELECT 1 FROM reports_db.clicks LIMIT 0"]
```

(see review-reports/2026-10-05-2356-cqrs-reporting-clickhouse.md, F6 — fixed before first commit,
no diffable hash; see note above)

## Relatives

### Nodes

- [ReportingService](node:reporting-service)
- [ReportingApi](node:reporting-api)

### Patterns

- [CQRS fan-out](pattern:cqrs-fanout)
