# A WebSocket-upgrade header pattern applied to a whole location also closes every plain REST connection on it

**Category:** connection-pooling **Status:** fixed

Watch for this any time one nginx `upstream`/`location` serves both WebSocket-upgrade traffic and
ordinary REST traffic, and the standard `map $http_upgrade $connection_upgrade { default upgrade;
'' close; }` recipe is applied uniformly across the whole block. That recipe is correct, standard
practice for the WebSocket requests it's meant for — but `$connection_upgrade` evaluates to
`close` for every request that is *not* itself an upgrade. Applied to the catch-all location that
also handles plain REST calls, it tells the upstream to close the TCP connection after every single
one of those ordinary requests, which silently defeats a `keepalive` pool directive configured on
the exact same upstream — the pool directive isn't wrong, it's just never reachable for that
traffic.

**Concrete instance in this project:** `reporting_api_upstream` in `nginx.conf` carries both
ReportingApi's `GET /reports/{hash}/summary` REST endpoint and its SignalR hub
(`/hub/top-links`). The WebSocket-proxying `map`/`$connection_upgrade` pair was wired onto one
catch-all `location /` covering both. For the hub path this is exactly right. For the REST path,
every request got `Connection: close` sent to the upstream, so Kestrel closed the connection after
responding and nginx could never return it to the `keepalive 64;` pool already configured on that
same upstream — unlike the sibling `link_api_upstream`/`redirect_api_upstream`, which send
`Connection: ""` and genuinely reuse pooled connections for their REST traffic. `keepalive 64;` had
simply been copied from those siblings' template without accounting for the one thing that made
this upstream's `location` different: it also serves an upgrade-aware path. Not a correctness bug
— every request still succeeded — but a real, concrete inconsistency: a fresh TCP handshake to the
upstream on every plain REST call instead of a pooled one, which only gets more relevant the moment
this upstream is load-tested through nginx expecting the same pooled-connection behavior its
siblings get.

⚠️ **Mistake** — `sandbox/infra/nginx/nginx.conf` (before commit `335d8a5`):

```nginx
server {
    listen 8088;

    location / {
        proxy_pass http://reporting_api_upstream;
        proxy_http_version 1.1;
        # $connection_upgrade (see the map above), not a bare "" like link_api_upstream/
        # redirect_api_upstream - those two never carry a WebSocket upgrade, this one does.
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
    # Every request through this one location - including plain REST calls to
    # /reports/{hash}/summary - gets Connection: $connection_upgrade, which resolves to "close"
    # for anything that isn't itself a WebSocket upgrade. keepalive 64 on this upstream never
    # gets a connection back to pool.
}
```

✅ **Do this instead** — split the hub's upgrade-aware path out from the catch-all REST location,
so only the path that actually needs upgrade-aware headers pays their cost:

```nginx
server {
    listen 8088;

    # SignalR's hub path only - carved out from the catch-all location below (not applied
    # uniformly) so plain REST requests keep reusing pooled upstream connections exactly like
    # link_api_upstream/redirect_api_upstream do. $connection_upgrade (see the map above) sends
    # "Connection: close" for every non-upgrade request it touches - fine for a WebSocket
    # handshake (which is a single long-lived connection anyway, never pooled), but it would
    # silently make `keepalive 64;` above dead configuration for ReportingApi's REST traffic if
    # applied to the catch-all location too (found in review).
    location /hub/top-links {
        proxy_pass http://reporting_api_upstream;
        proxy_http_version 1.1;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }

    location / {
        proxy_pass http://reporting_api_upstream;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

**Provenance:** found by `feature-review` on the commit that first fronted this service with nginx
(see `.notes/review-reports/2026-10-28-1316-reporting-api-nginx-signalr-backplane.md`, F2), fixed
in commit `335d8a5`.

## Relatives

### Nodes

- [ReportingApi](node:reporting-api)

### Patterns
