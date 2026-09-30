# Docker-internal IPs were misclassified as public, hitting a real external API on every click

**Category:** cpu-leak **Status:** fixed

`IpApiGeoIpResolver` skips the external geo-lookup call entirely for private/loopback addresses —
the common case for every click in local docker-compose (the client IP Kestrel sees is a
docker-internal address). `IsPrivateOrLoopback` checked `ip.AddressFamily != InterNetwork` before
doing a byte-range check — but Kestrel's `RemoteIpAddress` on a dual-stack socket (the .NET/Docker
default) commonly hands back an **IPv4-mapped IPv6** address like `::ffff:172.18.0.25` for what is
really an IPv4 client. That address's `AddressFamily` is `InterNetworkV6`, not `InterNetwork` — so
the check bailed out early, treating every docker-internal click as a public address.

Found by profiling a live k6 run generating hundreds of thousands of clicks: `traffic-service`'s
consumer fell over a million messages behind (`GET /api/queues` on the RabbitMQ management API),
acking at only ~0.4-0.5 msg/s. Container logs showed real calls to
`http://ip-api.com/json/::ffff:172.18.0.25` — a genuine external HTTP request, 3-second timeout, on
every single click, serializing the whole consumer.

🐛 **Bug** — no unmapping before the address-family check:

```csharp
private static bool IsPrivateOrLoopback(IPAddress ip)
{
    if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
    {
        return true;
    }

    if (ip.AddressFamily != AddressFamily.InterNetwork)
    {
        return false; // an IPv4-mapped IPv6 address falls through here, unmapped
    }

    var bytes = ip.GetAddressBytes();
    return bytes[0] switch { /* 10.x, 172.16-31.x, 192.168.x, ... */ };
}
```

✅ **Fix** — unmap an IPv4-mapped IPv6 address before any of the existing checks run:

```csharp
private static bool IsPrivateOrLoopback(IPAddress ip)
{
    // Kestrel's RemoteIpAddress on a dual-stack socket commonly hands back an IPv4-mapped IPv6
    // address for what is really an IPv4 client - AddressFamily is InterNetworkV6, not
    // InterNetwork, so the byte-range check below was silently skipped and every docker-internal
    // click was treated as a public address. Verified live: this sent a real ip-api.com call (3s
    // timeout) for every single click, serializing the consumer to ~1 message every 2-3s
    // regardless of prefetch/consumer count - unmapping first is what actually fixes that, not
    // more workers.
    if (ip.IsIPv4MappedToIPv6)
    {
        ip = ip.MapToIPv4();
    }

    if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
    {
        return true;
    }

    if (ip.AddressFamily != AddressFamily.InterNetwork)
    {
        return false;
    }

    var bytes = ip.GetAddressBytes();
    return bytes[0] switch { /* 10.x, 172.16-31.x, 192.168.x, ... */ };
}
```

**Effect, measured on the same load: single-replica ack rate ~0.4/s → ~45/s (≈100x); scaled to 4
`traffic-service` replicas afterward → ~112/s**, with queue depth staying bounded under realistic
load instead of climbing unboundedly.

This bug was found by live profiling, not a code review — no review-report describes it as a
standalone finding (a later report reviewing the same commit only flagged the fix's missing test
coverage, see [the health check channel leak pitfall](pitfall:rabbitmq-healthcheck-loser-channel-leak)
for another instance of that pattern). Before/after above is the real diff from the fix commit.

(fixed in `86c760c` — see `.notes/PLAN.md`, the 2026-09-25 "10k RPS load-testing pass" entry, Bug
#2, for the full before/after throughput numbers; regression test added one commit later,
`Infrastructure.Tests/IpApiGeoIpResolverTests.cs`, per
review-reports/2026-09-25-2140-86c760c-click-count-atomic-update.md, F1)

## Relatives

### Nodes

- [TrafficService](node:traffic-service)

### Patterns

None yet.
