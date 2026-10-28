# `ip_hash` rehashes modulo the live peer count, not a stable ring — fragile on an upstream meant to be scaled live

**Category:** config-tuning **Status:** fixed

Watch for this any time nginx's plain `ip_hash;` is chosen for sticky routing on an upstream that
also gets scaled up/down at runtime — load-test traffic shaping, a control-plane "scale" button,
anything that changes the live peer count outside of a one-time deploy. `ip_hash` picks a backend
as `hash(client_ip) mod N` over the *currently known* peer count `N`. That's a perfectly stable
choice as long as `N` never changes — but the moment it does (a scale event, or any replica
restart that picks up a new resolved IP), nearly every client's hash can remap to a different
backend than before, purely because `N` changed, not because the client moved. A stable hash ring
(`hash ... consistent;`, ketama hashing) only remaps the minimal necessary fraction of clients
when peers are added or removed — the difference matters specifically when the upstream is one
that's actually expected to scale while serving traffic.

**Concrete instance in this project:** `reporting_api_upstream` needs sticky-by-client routing in
the first place because SignalR's hub negotiates over two separate HTTP requests before the real
transport opens (`POST /negotiate` allocates a server-side `connectionId`, then the actual
WebSocket/SSE/long-polling request has to land on the *same* replica that allocated it — plain
round-robin sent those two requests to different replicas and every connection failed with "No
Connection with that ID"). `ip_hash` was the first fix for that split-negotiate failure. But this
same commit also wired `reporting-api` into the control plane's live-scaling capability — the
exact action that changes `ip_hash`'s peer count `N` while real users have open dashboards. Scaling
`reporting-api` up or down (or a replica being recreated with a new resolved IP) can remap a
client's *next new* connection attempt to a different backend than its already-established
connections used, transiently reintroducing the very split-negotiate failure this same `location`
block exists to prevent — self-healing on the next successful reconnect, but a real, avoidable
fragility right on the one upstream this feature specifically made scalable.

⚠️ **Mistake** — `sandbox/infra/nginx/nginx.conf` (before commit `335d8a5`):

```nginx
# ip_hash - unlike link_api_upstream/redirect_api_upstream above, this upstream also carries
# SignalR's hub connections, which negotiate over TWO separate HTTP requests before the real
# transport ever opens ... without this, round-robin sent those two requests to two DIFFERENT
# replicas and every connection failed ("No Connection with that ID: 404") ...
upstream reporting_api_upstream {
    zone reporting_api_zone 64k;
    ip_hash;
    server reporting-api:8080 resolve;
    keepalive 64;
}
```

✅ **Do this instead** — a stable hash ring instead of a peer-count-modulo hash, so a scale event
only remaps the small fraction of clients it actually needs to, not nearly everyone:

```nginx
# hash $remote_addr consistent - unlike link_api_upstream/redirect_api_upstream above, this
# upstream also carries SignalR's hub connections, which negotiate over TWO separate HTTP
# requests before the real transport ever opens (a POST /negotiate that allocates a
# connectionId server-side, THEN the actual WebSocket/SSE/long-polling request using that id) -
# found live, not assumed: without sticky-by-client routing, round-robin sent those two
# requests to two DIFFERENT replicas and every connection failed ("No Connection with that ID:
# 404"), exactly the failure SignalR's own client-side error message names ("check that sticky
# sessions are enabled"). `consistent` (ketama hashing), not plain `ip_hash` - plain ip_hash
# recomputes hash(ip) mod N over the CURRENT peer count, so a `docker compose --scale
# reporting-api=N` change (exactly what this item wires into the control-plane UI) remaps most
# existing clients' NEXT new connection to a different backend than their prior ones, transiently
# reintroducing this same split-negotiate failure right as the stack scales (found in review);
# consistent hashing only remaps ~1/N of clients on a peer-count change instead of nearly all of
# them.
upstream reporting_api_upstream {
    zone reporting_api_zone 64k;
    hash $remote_addr consistent;
    server reporting-api:8080 resolve;
    keepalive 64;
}
```

Already-established long-lived SignalR connections are unaffected either way — routing is decided
once per connection, not per packet — so this was always, at worst, a few-seconds self-healing
transient around a scale event, never a persistent break. Still worth fixing on the one upstream
this project specifically built live scaling for, rather than leaving a config choice that's
fragile exactly where it's most likely to be exercised.

**Provenance:** found by `feature-review` on the commit that first fronted this service with nginx
(see `.notes/review-reports/2026-10-28-1316-reporting-api-nginx-signalr-backplane.md`, F3), fixed
in commit `335d8a5`.

## Relatives

### Nodes

- [ReportingApi](node:reporting-api)

### Patterns
