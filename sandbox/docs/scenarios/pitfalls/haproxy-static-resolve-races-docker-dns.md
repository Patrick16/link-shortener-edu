# A statically-resolved backend hostname failed on first boot against Docker's embedded DNS

**Category:** infrastructure-bug **Status:** fixed

By default, a load balancer that resolves a backend's hostname does it once, at config-parse time.
That's fine for a name that's already resolvable the instant the process starts — it's a race
against a container's DNS registration appearing when the balancer and the backend it points at are
created by the same `docker compose up`, with no guaranteed ordering between "container exists" and
"container registered in Docker's embedded DNS."

**Concrete instance in this project:** HAProxy's `pgcat_back` backend pointed at `pgcat-1`/`pgcat-2`/
`pgcat-3` (3 separately-named services, from [Pooler scaling](pattern:pooler-scaling)'s first
build). On a fresh `docker compose up`, HAProxy sometimes started before Docker's embedded DNS
(`127.0.0.11`) had the `pgcat-N` names registered yet, and logged `could not resolve address
'pgcat-1'` — the backend came up with that server permanently excluded, not retried, since HAProxy
had already given up resolving it at parse time.

Mistake (static `server` lines, resolved once):

```
backend pgcat_back
    server pgcat-1 pgcat-1:6432 check
    server pgcat-2 pgcat-2:6432 check
    server pgcat-3 pgcat-3:6432 check
```

Do this instead — a `resolvers` section that re-resolves at runtime, repeatedly, plus
`init-addr none` so startup itself doesn't require the name to already be resolvable:

```
# sandbox/infra/haproxy/haproxy.cfg
resolvers docker_dns
    nameserver dns1 127.0.0.11:53
    resolve_retries 3
    timeout retry 1s
    hold valid 10s

backend pgcat_back
    server pgcat-1 pgcat-1:6432 check resolvers docker_dns init-addr none
    # ... same for pgcat-2/pgcat-3
```

This fix alone was enough under the 3-separately-named-services design. It stopped being enough
once that design changed — see this node's other DNS pitfall for what broke next.

**Provenance:** found live, during this feature's own initial build and verification — not a code-
review finding. `docker compose up` on a fresh stack reproduced `could not resolve address` in
HAProxy's logs before the fix, and didn't after.

## Relatives

### Nodes

- [HAProxy](node:haproxy)
- [PgCat](node:pgcat)

### Patterns

- [Pooler scaling](pattern:pooler-scaling)
