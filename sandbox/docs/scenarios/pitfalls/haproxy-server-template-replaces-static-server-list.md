# Scaling one Compose service made per-replica hostnames stop resolving entirely

**Category:** infrastructure-bug **Status:** fixed

Docker Compose gives two different things the same vague name, "scaling," with genuinely different
DNS behavior: several separately-named services each resolve their own hostname; one service scaled
to N replicas (`deploy.replicas: N`) does **not** get N individually-resolvable hostnames — only the
one service-level DNS name resolves, and it returns every replica's IP as a separate A record. A
load balancer's static, per-hostname `server` list assumes the first shape. Switching the backend
from "N named services" to "one service, N replicas" without also changing the load balancer's
config silently breaks every one of those `server` lines at once.

**Concrete instance in this project:** [Pooler scaling](pattern:pooler-scaling) was corrected
mid-build from 3 separately-named `pgcat-1`/`pgcat-2`/`pgcat-3` services to one `pgcat` service at
`deploy.replicas: 3` (see that pattern doc for why — a graph-rendering problem, not a DNS one).
HAProxy's `pgcat_back` backend still had 3 static `server pgcat-1 pgcat-1:6432` / `pgcat-2 ...` /
`pgcat-3 ...` lines from the previous design. Confirmed live: `docker compose exec haproxy getent
hosts pgcat-1` returned nothing at all — not a transient failure, a name that simply doesn't exist
once `pgcat-1` isn't a real service, just a replica of one — while `getent hosts pgcat` returned all
3 replicas' IPs as separate A records. Every static `server` line went to `MAINT (resolution)`.
[A previous fix](pitfall:haproxy-static-resolve-races-docker-dns) (the `resolvers` section) made the
*timing* of resolution robust; it couldn't fix a hostname that was never going to resolve to begin
with — a different failure mode than the one that fix targeted.

Mistake (static per-replica `server` lines, broken by the service-shape change):

```
backend pgcat_back
    server pgcat-1 pgcat-1:6432 check resolvers docker_dns init-addr none
    server pgcat-2 pgcat-2:6432 check resolvers docker_dns init-addr none
    server pgcat-3 pgcat-3:6432 check resolvers docker_dns init-addr none
```

Do this instead — `server-template`, which resolves the one real DNS name and creates backend
slots from however many A records come back, the same "one name, several addresses" technique
`nginx.conf` already needed for its own scaled upstreams (via its `resolve` keyword):

```
# sandbox/infra/haproxy/haproxy.cfg
backend pgcat_back
    balance leastconn
    option pgsql-check user postgres
    server-template pgcat 6 pgcat:6432 check inter 3s fall 3 rise 2 resolvers docker_dns init-addr none
```

`6` reserves 2× headroom over today's 3 replicas — unused slots just show `MAINT (resolution)`
harmlessly rather than erroring. Raise it if `pgcat` is ever scaled past 6 replicas.

**Provenance:** found live, while verifying the mid-build design correction — not a code-review
finding. Reproduced via `docker compose exec haproxy getent hosts pgcat-1` (empty) vs. `getent hosts
pgcat` (3 A records) before the fix; confirmed fixed by re-running real `psql` queries through
`haproxy:6432` and checking HAProxy's own stats CSV showed `pgcat1`/`pgcat2`/`pgcat3` all `UP`.

## Relatives

### Nodes

- [HAProxy](node:haproxy)
- [PgCat](node:pgcat)

### Patterns

- [Pooler scaling](pattern:pooler-scaling)
