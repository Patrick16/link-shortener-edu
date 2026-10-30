# A docker-compose `${VAR}` substitution name never appears on the running container — only the real, rendered env var does

**Category:** infrastructure-bug **Status:** fixed

Watch for this any time code inspects a *running* container's environment and the author's first
instinct is to look for the same names used on the `docker-compose.yml` side, e.g.
`${CACHE_ENABLED:-true}`. Those `${VAR}` names are docker-compose's own *substitution* syntax —
resolved away entirely by `docker compose up` before the container ever starts. What the container
actually carries in its `Config.Env` is whatever the *value* of that substitution got assigned to
on the right-hand side of the YAML key — a different name, picked by whoever wrote the service's
own configuration binding (here, ASP.NET Core's `:`-to-`__` env var convention, e.g.
`Cache__Enabled`), not the compose variable name at all.

This is easy to get wrong quietly: a test that mocks the container-inspect call will happily encode
the same wrong assumption the implementation makes, so unit tests pass regardless of which name is
right. Only a real `docker inspect` against an actually-running container exposes the mismatch.

**Concrete instance in this project:** the first implementation of `InfraToggleService.
ReconcileFromLiveContainersAsync` (see the companion pitfall,
[cached control-plane state desyncing from live containers on restart](pitfall:infra-toggle-state-resets-on-restart-desyncs-live-containers),
for why this method exists at all) looked for `CurrentStandingEnv()`'s own key names —
`DB_HOST`, `CACHE_ENABLED`, and so on — directly on the inspected container. Every unit test for it
passed, because the test doubles supplied exactly that shape. Against the real stack it silently
matched nothing: toggling pgcat off, restarting control-api, and checking the reconciliation log
line still showed `pgcat=True` — every field quietly fell back to its hardcoded default instead of
picking up the real state, the opposite of what the method exists to do.

⚠️ **Mistake** — reconstructed (no separate commit shows this broken version; it was caught and
rewritten before `5637b49` was ever committed, so there is no diffable "before" hash — the
mismatch is described verbatim in that commit's own code comment on
`ReconcileFromLiveContainersAsync`):

```csharp
// WRONG: these are docker-compose's ${VAR} substitution names (the left side of
// "Cache__Enabled: ${CACHE_ENABLED:-true}" in docker-compose.yml) - resolved away by
// `docker compose up` before the container ever sees them. They never appear in
// inspect.Config.Env under these names.
if (env.TryGetValue("CACHE_ENABLED", out var cacheEnabled)) _cacheEnabled = cacheEnabled == "true";
if (env.TryGetValue("DB_HOST", out var dbHost)) _pgcatEnabled = dbHost == "haproxy";
```

✅ **Do this instead** (`5637b49`) — read the real, rendered env var name the application code
itself binds to (ASP.NET Core's double-underscore convention here), the one that actually appears
in `docker inspect`'s output:

```csharp
// RIGHT: Cache__Enabled/ConnectionStrings__Postgres are what the container's Config.Env actually
// contains - the right-hand side of docker-compose.yml's substitution, after ASP.NET Core's own
// configuration-binding convention is applied, not the compose variable name.
if (env.TryGetValue("Cache__Enabled", out var cacheEnabled)) _cacheEnabled = cacheEnabled == "true";
if (env.TryGetValue("ConnectionStrings__Postgres", out var postgresConnStr))
    _pgcatEnabled = postgresConnStr.Contains("Host=haproxy", StringComparison.OrdinalIgnoreCase);
```

Caught only by live-verifying against the real stack, not by the unit test suite: toggle pgcat off,
restart control-api, and check the log line — before the fix it said `pgcat=True` regardless; after,
`pgcat=False`, correctly restored once pgcat was re-enabled.

**The reusable lesson:** when code is going to read a container's *actual* environment (via
`docker inspect`, `/proc/<pid>/environ`, or anything else that looks at the rendered process, not
the compose file), verify the real key names against a real running container at least once — don't
trust the compose-file-side name to also be the runtime-side name, and don't trust a test whose mock
was written by the same person who wrote the (possibly wrong) assumption being tested.

(not sourced from a `.notes/review-reports/` finding — caught and fixed pre-commit while building
`5637b49`; recorded in `.notes/PLAN.md` as its own named lesson,
"compose-substitution-vs-rendered-env-var-names"; see
`.notes/feature-docs/2026-10-30-1524-external-review-10-bugs.md`, bug 2, for the fuller narrative)

## Relatives

### Nodes

- [PgCat](node:pgcat) — the field whose mismatch was actually live-verified end to end

### Patterns

- [Pooler scaling](pattern:pooler-scaling)
