#!/bin/sh
# redis-master runs through here on every start, including a plain `docker start` after Sentinel has
# already failed over to a different node while this one was down. A bare `redis-server` always comes
# back up as an independent master - it has no memory of ever having lost that role - so without this
# check, restarting the original master after a real failover produces a second, unrelated master
# next to whichever node Sentinel actually promoted (a split brain), not a rejoin. This asks Sentinel
# first: if it already has an opinion on who's master and it isn't us, start as that node's replica
# instead.
#
# Bounded, not blocking, by design: on a genuine first-ever `docker compose up`, no Sentinel
# container exists yet at all (they depend on this one being healthy first) - `getent` is a DNS
# lookup, not a TCP connect, so an unknown compose service name fails fast rather than hanging (the
# same property the Sentinel containers' own entrypoint already relies on `getent`/`redis-cli -h` for
# - see sentinel.conf's header comment), and a cold start falls through to the bare-master branch
# within a second or two instead of stalling startup.

MASTER_ADDR=""
for sentinel in redis-sentinel-1 redis-sentinel-2 redis-sentinel-3; do
  if getent hosts "$sentinel" >/dev/null 2>&1; then
    MASTER_ADDR=$(redis-cli -h "$sentinel" -p 26379 SENTINEL get-master-addr-by-name mymaster 2>/dev/null)
    if [ -n "$MASTER_ADDR" ]; then
      break
    fi
  fi
done

# min-replicas-to-write/-max-lag: not real fencing (a master that still sees a replica connection
# keeps accepting writes even if it's the one that's actually isolated/stale), but it closes the
# common split-brain window - a master cut off from every replica (e.g. recreated while Sentinels
# were down, so nothing ever told it to step down) stops accepting writes instead of silently
# diverging. Baked into every startup path on every Redis node (not just this file's two branches,
# see the matching flags on redis-replica1/2's command in docker-compose.yml) because Sentinel
# promotes a replica to master via REPLICAOF NO ONE without setting server-level config like this -
# whichever node ends up master needs to have started with it already.
MIN_REPLICAS_FLAGS="--min-replicas-to-write 1 --min-replicas-max-lag 10"

if [ -z "$MASTER_ADDR" ]; then
  echo "master-entrypoint: no Sentinel has an opinion yet (cold start) - starting as master"
  exec redis-server $MIN_REPLICAS_FLAGS
fi

MASTER_IP=$(echo "$MASTER_ADDR" | head -1)
MASTER_PORT=$(echo "$MASTER_ADDR" | tail -1)
SELF_IP=$(getent hosts "$(hostname)" | awk '{print $1}' | head -1)

if [ "$MASTER_IP" = "$SELF_IP" ]; then
  echo "master-entrypoint: Sentinel already considers this node ($SELF_IP) the master - starting as master"
  exec redis-server $MIN_REPLICAS_FLAGS
fi

echo "master-entrypoint: Sentinel says $MASTER_IP:$MASTER_PORT is master (this node is $SELF_IP) - rejoining as its replica"
exec redis-server --replicaof "$MASTER_IP" "$MASTER_PORT" $MIN_REPLICAS_FLAGS
