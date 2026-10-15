# Product Code Map

Documentation about this codebase itself — what lives where and why. For how to run the stack,
scenario walkthroughs, and the target system architecture, see `sandbox/docs/` instead.

## Backend (`src/backend/`)

.NET solution, `LinkShortener.sln`:

- `Services/AuthApi` — registration/login, issues JWTs, owns `users_db`
- `Services/LinkApi` — creates/reads short links, owns no database (reads `links_db`)
- `Services/RedirectApi` — resolves a hash and redirects, reads `links_db`
- `Services/ShortenerService` (worker) — consumes `LinkCreatedEvent`, owns `links_db`
- `Services/TrafficService` (worker) — consumes `ClickTrackedEvent`, owns `clicks_db`
- `Services/ReportingApi` — read side: ClickHouse summaries (owner-scoped — `GET
  /reports/{hash}/summary` returns 404, not 403, for a hash that exists but isn't the caller's),
  plus the live top-links SignalR hub (`Realtime/`: hub, per-user subscription registry, throttled
  push service and scheduler)
- `Shared/Contracts` — event DTOs shared between publishers and consumers (single source of truth
  — never duplicate these on either side)
- `Shared/Infrastructure` — RabbitMQ client/publisher/consumer, shard resolver, Redis helpers
  (`TopLinks.cs` holds the per-user day-bucket key format and the 7-day window shared by
  RedirectApi and ReportingApi; `RedisClickCounterService` writes the buckets, `TopLinksReader` reads them)
- `Shared/Common` — cross-service utilities (e.g. `LinkCacheService`, since LinkApi and RedirectApi
  must agree on the same Redis key format)
- `Shared/ServiceDefaults` — OpenTelemetry wiring shared by every service
- `Shared/WebDefaults` — shared web-host setup, used by the HTTP-facing services (and by
  `control-api`'s tests, outside this solution)
- `tests/` — one test project per service under test, plus `Integration.Tests`

**Rule:** each database has exactly one owning service that runs migrations against it (see
`sandbox/docs/architecture.md` for the current owner list). Other services may read the same
database but never migrate it.

## Frontend (`src/frontend/app/`)

React + TypeScript + Vite. The actual product UI: register/login, create a short link, view your
links, and the live top-10 panel. Talks to `AuthApi`/`LinkApi`/`RedirectApi` over HTTP, and to
`ReportingApi` for the SignalR feed (`src/api/topLinksApi.ts`; the dashboard uses
`src/hooks/useTopLinks.ts` and `src/components/TopLinksPanel.tsx`). See `.env.example` for base URLs.

Not to be confused with `sandbox/frontend/architecture-map/` — that one visualizes and controls
the *running stack*, it isn't part of the product.
