# Repository Map

This repository has three top-level parts, kept deliberately separate:

```
src/
├── backend/     .NET solution — the actual product services (AuthApi, LinkApi, RedirectApi,
│                ShortenerService, TrafficService) plus shared libraries and tests.
│                Self-contained: LinkShortener.sln lives here, open it directly.
├── frontend/
│   └── app/     The product's own UI — register/login, create a short link, view stats.
└── docs/        Documentation about the product's own code: API shapes, DB schema, per-service
                 notes. Not about running or observing the stack — see sandbox/docs/ for that.

sandbox/
├── docker-compose*.yml   Orchestrates the whole stack for local runs.
├── infra/       Config consumed by docker-compose: nginx, pgcat, postgres init scripts,
│                rabbitmq definitions.
├── frontend/
│   └── architecture-map/   Educational interactive diagram of the running system — click a
│                            node for details/metrics, drive live chaos/load controls. Always
│                            shows the whole graph — no per-scenario switcher.
├── scripts/     start-stack.ps1 / stop-stack.ps1 / dump-db.ps1 / restore-db.ps1.
└── docs/        How to run and observe each scenario (01-minimal.md, 02-async.md, ...),
                 plus the target architecture.md.

docs/            You are here — repository-wide documentation (this file only, for now).
```

## Why split this way

`src/` is "the product" — what you'd deploy if this were a real service, and what a contributor
touches when changing behavior. `sandbox/` is "how to run and learn from it locally" — scenario
progression, infrastructure config, load testing, and (see `sandbox/docs/`) the interactive map
used to explore the architecture. Splitting them means the product's own structure doesn't get
tangled up with the learning harness built around it.

## Where to start

- Want to run the stack? `sandbox/scripts/start-stack.ps1`, then read
  [`sandbox/docs/scenarios/01-minimal.md`](../sandbox/docs/scenarios/01-minimal.md).
- Want the target architecture? [`sandbox/docs/architecture.md`](../sandbox/docs/architecture.md).
- Want to read or change product code? `src/backend/LinkShortener.sln` and `src/frontend/app/`.

## Doc layers

Two kinds of documentation live in this repo, and they follow different rules:

- **Explanation layer** — this file, `sandbox/docs/architecture.md`'s target-architecture
  sections, `src/docs/README.md`. Answers "why is it built this way." Links at
  directory/service granularity only (`src/backend/Shared/Common`, not
  `EntityCacheService.cs:79`) and never describes current build status in detail — that drifts
  fast and there's already a place for it (see below). Expected to change rarely.
- **Scenario/node/pattern layer** — `sandbox/docs/scenarios/*.md`, and per-node/per-pattern docs
  surfaced in `architecture-map`'s "Learn" panel (`sandbox/docs/scenarios/{nodes,patterns}/*.md`).
  Answers "how does this specific piece work, and what broke while building it." `file:line`
  references and code snippets belong here, not in the explanation layer.

**Current implementation status** (what's actually built vs. planned) lives in
[`sandbox/docs/architecture.md`](../sandbox/docs/architecture.md) — not duplicated into
explanation-layer docs, which go stale the moment a status claim is copied out of the one place
that's actually kept up to date.
