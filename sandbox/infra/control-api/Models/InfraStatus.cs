namespace ControlApi.Models;

// The three standing infra toggles the graph exposes on nginx/pgcat/redis's own node panels -
// unlike a chaos run or a traffic run, these persist until switched back, because the whole point
// is to show how the *system* behaves without that piece, not just one synthetic test.
//
// NginxBypassed lives purely in control-api's memory (it only changes which URL a k6 container is
// given, nginx itself keeps running so the frontend app's own manual use of it is unaffected).
// PgcatEnabled/CacheEnabled instead reflect real container state - toggling them recreates the
// affected services with a different connection string / cache flag via docker compose - so on a
// control-api restart these two fields could in principle drift from the containers' actual last
// setting; a real restart during an experiment is rare enough for a local sandbox tool that this is
// an accepted, documented limitation rather than something worth reconciling on startup.
// MessagingMode ("rabbitmq" or "grpc") is the fourth real-container-state toggle, same category as
// PgcatEnabled/CacheEnabled above (same drift-on-restart caveat applies) - see
// MessagingToggleCapability/InfraToggleService.SetMessagingModeAsync.
//
// HaproxyEnabled is a different kind of real-container-state toggle from the three above: those
// recreate the *consumer* services with a different connection string/flag, while this one starts/
// stops the haproxy container itself - DB_HOST stays "haproxy" the whole time (see
// InfraToggleService.CurrentStandingEnv), so turning it off while PgcatEnabled is still true
// deliberately demonstrates the pooler layer's single entry point going down, not a graceful
// bypass. Only meaningful while PgcatEnabled is true - see HaproxyToggleCapability/
// InfraToggleService.SetHaproxyEnabledAsync.
//
// Unlike PgcatEnabled/CacheEnabled/MessagingMode above, this field is NOT cached in control-api's
// memory - it's read live from the haproxy container's actual Docker state on every
// GetInfraStatusAsync call (see InfraToggleService.IsHaproxyRunningAsync). A cached bool would go
// stale the moment anyone used the generic per-container Start/Stop controls every node gets for
// free (ContainerEndpoints.cs) on haproxy directly - those bypass SetHaproxyEnabledAsync entirely
// and have no reason not to, so the only way to never lie about this one is to not cache it.
//
// PostgresReplicasEnabled is the same "read live, don't cache" shape as HaproxyEnabled and for the
// identical reason - postgres-replica1/2 also get the generic per-container Start/Stop controls for
// free, which would silently desync a cached flag. True only when BOTH replica containers are
// running (see InfraToggleService.AreAllRunningAsync).
//
// This field itself can never lie (same live-read guarantee as HaproxyEnabled above), but
// pgcat.toml's servers list is a SEPARATE piece of derived state (PgcatService._replicasEnabled)
// that this field says nothing about - toggling replicas through SetPostgresReplicasEnabledAsync
// keeps the two in lockstep (pgcat never routes to a replica that's stopped, or ignores one that's
// back up), but stopping/starting a replica via the generic per-container controls instead bypasses
// PgcatService entirely, exactly like the PgcatEnabled/CacheEnabled generic-bypass caveat above -
// pgcat.toml would then keep listing a replica that's actually down (or omit one that's actually up)
// until something exercises this toggle for real.
//
// RedisEnabled/RedisSentinelEnabled are the Redis-side equivalent of HaproxyEnabled/
// PostgresReplicasEnabled above - both read live from Docker (same generic-per-container-controls
// desync risk), never cached. RedisEnabled is true iff redis-master is running.
// RedisSentinelEnabled is true iff all 5 HA containers (redis-sentinel-1/2/3, redis-replica1/2) are
// running. Sentinel can only be turned on while Redis is on (SetRedisSentinelEnabledAsync enforces
// this, same shape as HaproxyEnabled requiring PgcatEnabled); turning Redis off forces Sentinel off
// too, one-directionally, same shape as PgcatEnabled forcing HaproxyEnabled off.
//
// RedisEnabled also moves CacheEnabled with it, in BOTH directions, unlike every other cascade in
// this record - a redis-master nobody's allowed to read from would be a node on the graph with no
// reason to exist, so there's no "Redis on, Caching off" standing state reachable through this
// toggle at all (stopping the container without losing it is what the generic per-node Stop control
// is for, same as any other node). CacheEnabled keeps its own standalone endpoint
// (SetCacheEnabledAsync/"cache-toggle") for Presets/diffing to capture and reapply independently,
// but RedisEnabled is the only thing driving it through the Topology panel - see
// InfraToggleService.SetRedisEnabledAsync/SetRedisSentinelEnabledAsync.
//
// Neither field says anything about redis-master's own min-replicas-to-write - a third piece of
// state this toggle keeps in lockstep (0 while Sentinel is off, 1 while it's on) that's neither
// cached here nor read live, found by exercising this toggle live: without it, a standalone
// redis-master still refuses every write once its last connected replica disappears (the
// resilience pass's own split-brain mitigation, see redis-node-entrypoint.sh's MIN_REPLICAS_FLAGS),
// which would make "Redis on, Sentinel off" silently non-functional rather than a working plain
// cache - see InfraToggleService.SetRedisMinReplicasToWriteAsync.
//
// Nor does either field say anything about which physical container Sentinel currently believes is
// "mymaster", or which one redis-replica1/2 are actually replicating off - also found by exercising
// this toggle live: redis-sentinel-1/2/3's sentinel.conf survives a restart unchanged once it's ever
// learned a master address, so restarting them (this toggle's own ON path - the first thing in this
// stack's normal lifecycle to ever do that) can resurrect a stale pointer from long before this
// toggle existed and fail over onto a now-dead address, demoting the real redis-master to its
// replica. SetRedisSentinelEnabledAsync corrects both (ReclaimRedisMasterRoleAsync, then
// RedisInfraService.SelfHealSentinelAsync) every time it turns Sentinel on, rather than leaving this
// to the existing 30s self-heal poller - that poller can't recover a fully-stuck topology on its own
// in the first place, since it needs at least one data node already reachable and confirmed master to
// anchor on.
//
// Unlike Postgres's pgcat.toml, the Redis Sentinel toggle also flips which connection-string FORM
// (Sentinel-form vs a direct redis-master:6379) the 6 Redis-using services are recreated with - that
// choice is cached state (InfraToggleService._redisConnectionViaSentinel), the same
// recreate-with-a-different-env-var shape as PgcatEnabled/CacheEnabled/MessagingMode above, not a
// live read, and carries the identical generic-bypass caveat those three already document.
//
// MongoReplicasEnabled is the Mongo-side equivalent of PostgresReplicasEnabled - read live
// (true iff BOTH mongo2/mongo3 are running), single toggle, no nested sub-toggle the way Redis has
// (Mongo has no separate "cacheable but not HA" state to merge into it - mongo1 is always the single
// required node, same as redis-master, but there's no app-level flag analogous to CacheEnabled to
// move with it). Unlike Postgres's pgcat.toml, though, rs0's own replica-set CONFIGURATION (voting
// membership) is a genuinely different thing from "is the container running" - stopping mongo2/
// mongo3 without first removing them from rs0's config would leave mongo1 able to see only 1 of 3
// votes and step down to SECONDARY, refusing writes entirely, not just serve a stale list the way a
// forgotten pgcat.toml entry would. SetMongoReplicasEnabledAsync always reconfigures rs0
// (MongoTopologyService.SetReplicasEnabledAsync) before/after the container stop/start, never just
// one or the other - see its own comment. The connection-string host list (MONGO_HOSTS) that
// traffic-service gets recreated with is cached state
// (InfraToggleService._mongoConnectionIncludesReplicas), the same shape as REDIS_CONN above, with the
// identical generic-bypass caveat.
public record InfraStatus(bool NginxBypassed, bool PgcatEnabled, bool CacheEnabled, string MessagingMode, bool HaproxyEnabled, bool PostgresReplicasEnabled, bool RedisEnabled, bool RedisSentinelEnabled, bool MongoReplicasEnabled);

public record InfraToggleRequest(bool Enabled);

public record MessagingModeRequest(string Mode);
