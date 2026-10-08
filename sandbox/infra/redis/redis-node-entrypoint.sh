#!/bin/sh
# Shared entrypoint for all three Redis data-plane containers (redis-master, redis-replica1,
# redis-replica2) - every one of them runs through here on every start, including a plain
# `docker start` after Sentinel has already failed over to a different node while this one was
# down. Generalized from an earlier redis-master-only version (master-entrypoint.sh) that left
# redis-replica1/redis-replica2 with a hardcoded `--replicaof redis-master` in their compose
# `command:` and no equivalent check - confirmed live: stop all three data nodes, let Sentinel
# promote (say) redis-replica1 while they're down, then restart all three - redis-master correctly
# asks Sentinel and rejoins as redis-replica1's replica, but redis-replica1 itself just reapplies
# its hardcoded `--replicaof redis-master`, blindly demoting itself back to a replica of the node
# that's supposed to be replicating from IT. The two fight over who's the replica until Sentinel's
# own failover churn eventually resolves it by luck of the restart timing, not by design. Every
# node now asks Sentinel first: if it already has an opinion on who's master, obey it (become that
# node's replica, or start as master if the opinion names this container) - only when Sentinel
# can't give a confident answer does NODE_DEFAULT_ROLE (this container's compose-declared default:
# "master" for redis-master, "replica" for redis-replica1/2) come into play, and even then this
# node's own last Sentinel-confirmed role (STATE_FILE) is trusted ahead of that default - a node
# whose last confirmed role was master never demotes itself just because Sentinel is transiently
# unreachable, regardless of what its compose default says.
#
# Bounded, not blocking, by design: on a genuine first-ever `docker compose up`, no Sentinel
# container exists yet at all (they depend on the data nodes being healthy first) - `getent` is a
# DNS lookup, not a TCP connect, so an unknown compose service name fails fast rather than hanging,
# and a cold start falls through to NODE_DEFAULT_ROLE within a second or two instead of stalling
# startup.
#
# "No Sentinel has an opinion" used to mean two very different things treated identically: (a) a
# genuine cold start (no Sentinel container exists at all - getent fails for every one), and (b)
# this node being restarted at a moment when Sentinel containers *exist* but are all transiently
# unreachable (e.g. themselves mid-restart during the same chaos test exercising Sentinel
# resilience), even though a real failover already happened and blindly falling back to the
# compose default here could be exactly the split-brain-on-rejoin scenario above. The two are told
# apart via ANY_SENTINEL_HOST_RESOLVED: "no Sentinel container exists" (getent fails for all three
# - safe to use NODE_DEFAULT_ROLE immediately, same as a normal cold start) vs "Sentinel
# container(s) exist but none answered" (getent succeeds for at least one, but SENTINEL
# get-master-addr-by-name still comes back empty from all of them - retried a few times before
# falling through, since this is most likely transient). If retries exhaust with still no answer,
# STATE_FILE (this node's last Sentinel-confirmed role, persisted per-node so it survives a
# container recreate) breaks the remaining ambiguity, per the asymmetric rule above.
#
# Also: an earlier version trusted whichever Sentinel answered first, with no check for the other
# two disagreeing (a stale/minority Sentinel that hasn't heard about a fresh failover yet).
# pick_majority below requires at least two of the three to agree before an answer is trusted; a
# lone answer is still accepted (that's the same "only 1 of 3 Sentinels reachable" tolerance the
# whole point of running 3 Sentinels is meant to provide), but a straight disagreement with no
# majority is treated the same as "no answer" and goes through the same retry/ambiguity handling
# above.

SENTINELS="redis-sentinel-1 redis-sentinel-2 redis-sentinel-3"
STATE_FILE="/state/last-confirmed-role"
MIN_REPLICAS_FLAGS="--min-replicas-to-write 1 --min-replicas-max-lag 10"

# This container's role per docker-compose.yml when Sentinel has no opinion at all (cold start) or
# can't be reached confidently and this node's own last confirmed role doesn't say otherwise.
# NODE_DEFAULT_REPLICAOF_HOST/_PORT are only read when NODE_DEFAULT_ROLE=replica.
NODE_DEFAULT_ROLE="${NODE_DEFAULT_ROLE:-replica}"
NODE_DEFAULT_REPLICAOF_HOST="${NODE_DEFAULT_REPLICAOF_HOST:-redis-master}"
NODE_DEFAULT_REPLICAOF_PORT="${NODE_DEFAULT_REPLICAOF_PORT:-6379}"

# Majority-vote across up to 3 answers (each "ip port" or empty if that Sentinel didn't respond),
# instead of trusting whichever one happens to answer first. A value shared by at least 2 of the 3
# wins; a single non-empty answer with the other two silent still wins (tolerating 2 unreachable
# Sentinels is the point of running 3); anything else (a 3-way disagreement, or no answers at all) is
# treated as "no confident answer".
pick_majority() {
  a="$1"; b="$2"; c="$3"
  if [ -n "$a" ] && { [ "$a" = "$b" ] || [ "$a" = "$c" ]; }; then
    echo "$a"
  elif [ -n "$b" ] && [ "$b" = "$c" ]; then
    echo "$b"
  elif [ -n "$a" ] && [ -z "$b" ] && [ -z "$c" ]; then
    echo "$a"
  elif [ -z "$a" ] && [ -n "$b" ] && [ -z "$c" ]; then
    echo "$b"
  elif [ -z "$a" ] && [ -z "$b" ] && [ -n "$c" ]; then
    echo "$c"
  else
    echo ""
  fi
}

# Queries every Sentinel once. Sets the globals ANY_SENTINEL_HOST_RESOLVED (1 if getent succeeded
# for at least one, regardless of whether redis-cli got an answer from it) and QUERY_RESULT (the
# majority-agreed "ip port", or empty if there's no confident answer yet). Deliberately NOT called
# as `x=$(query_sentinels_once)` anywhere - that would run it in a subshell (command substitution
# always forks one in POSIX sh), so ANY_SENTINEL_HOST_RESOLVED's assignment would never make it back
# to the calling shell and the retry loop below would never see a real cold start as one.
query_sentinels_once() {
  ANY_SENTINEL_HOST_RESOLVED=0
  answers=""
  for sentinel in $SENTINELS; do
    addr=""
    if getent hosts "$sentinel" >/dev/null 2>&1; then
      ANY_SENTINEL_HOST_RESOLVED=1
      addr=$(redis-cli -h "$sentinel" -p 26379 SENTINEL get-master-addr-by-name mymaster 2>/dev/null | tr '\n' ' ' | sed 's/ *$//')
    fi
    answers="$answers|$addr"
  done
  # answers is now "|A1|A2|A3" - split back out positionally for pick_majority.
  a=$(echo "$answers" | cut -d'|' -f2)
  b=$(echo "$answers" | cut -d'|' -f3)
  c=$(echo "$answers" | cut -d'|' -f4)
  QUERY_RESULT=$(pick_majority "$a" "$b" "$c")
}

query_sentinels_once
MASTER_ADDR="$QUERY_RESULT"

max_attempts=5
attempt=1
while [ -z "$MASTER_ADDR" ] && [ "$ANY_SENTINEL_HOST_RESOLVED" = "1" ] && [ "$attempt" -lt "$max_attempts" ]; do
  echo "redis-node-entrypoint: Sentinel container(s) exist but gave no confident answer (attempt $attempt/$max_attempts) - retrying..."
  sleep 2
  query_sentinels_once
  MASTER_ADDR="$QUERY_RESULT"
  attempt=$((attempt + 1))
done

mkdir -p "$(dirname "$STATE_FILE")" 2>/dev/null
LAST_ROLE=""
[ -f "$STATE_FILE" ] && LAST_ROLE=$(cat "$STATE_FILE" 2>/dev/null)

DEVIATES=0
[ -n "$LAST_ROLE" ] && [ "$LAST_ROLE" != "$NODE_DEFAULT_ROLE" ] && DEVIATES=1

if [ -z "$MASTER_ADDR" ]; then
  if [ "$ANY_SENTINEL_HOST_RESOLVED" = "0" ]; then
    echo "redis-node-entrypoint: no Sentinel container exists yet (genuine cold start) - using this node's default role ($NODE_DEFAULT_ROLE)"
  elif [ "$LAST_ROLE" = "master" ]; then
    if [ "$DEVIATES" = "1" ]; then
      echo "redis-node-entrypoint: AMBIGUOUS - Sentinel container(s) exist but none gave a confident answer after $max_attempts attempts, and this node's last Sentinel-confirmed role was master even though its compose-declared default is '$NODE_DEFAULT_ROLE'. This looks like a real promotion Sentinel can't currently confirm, not a normal cold start - trusting the last confirmed role and starting as master anyway (write-fencing via '$MIN_REPLICAS_FLAGS' keeps it from accepting writes until a replica connects), but the topology MUST be reconciled manually once Sentinel is reachable again."
    else
      echo "redis-node-entrypoint: Sentinel container(s) exist but none gave a confident answer after $max_attempts attempts; this node's last Sentinel-confirmed role was master - starting as master"
    fi
    echo "master" > "$STATE_FILE" 2>/dev/null
    exec redis-server $MIN_REPLICAS_FLAGS
  elif [ "$DEVIATES" = "1" ]; then
    echo "redis-node-entrypoint: AMBIGUOUS - Sentinel container(s) exist but none gave a confident answer after $max_attempts attempts, and this node's last Sentinel-confirmed role was replica even though its compose-declared default is '$NODE_DEFAULT_ROLE'. Falling back to the compose default anyway - unlike last-confirmed-master above, this isn't the dangerous direction to be wrong in - but the topology should still be reconciled once Sentinel is reachable again."
  else
    echo "redis-node-entrypoint: Sentinel container(s) exist but none gave a confident answer after $max_attempts attempts; this node has no prior confirmed role contradicting its default - falling back to this node's default role ($NODE_DEFAULT_ROLE)"
  fi

  if [ "$NODE_DEFAULT_ROLE" = "master" ]; then
    echo "master" > "$STATE_FILE" 2>/dev/null
    exec redis-server $MIN_REPLICAS_FLAGS
  fi
  echo "replica" > "$STATE_FILE" 2>/dev/null
  exec redis-server --replicaof "$NODE_DEFAULT_REPLICAOF_HOST" "$NODE_DEFAULT_REPLICAOF_PORT" $MIN_REPLICAS_FLAGS
fi

MASTER_IP=$(echo "$MASTER_ADDR" | awk '{print $1}')
MASTER_PORT=$(echo "$MASTER_ADDR" | awk '{print $2}')
SELF_IP=$(getent hosts "$(hostname)" | awk '{print $1}' | head -1)

if [ "$MASTER_IP" = "$SELF_IP" ]; then
  echo "redis-node-entrypoint: Sentinel already considers this node ($SELF_IP) the master - starting as master"
  echo "master" > "$STATE_FILE" 2>/dev/null
  exec redis-server $MIN_REPLICAS_FLAGS
fi

echo "redis-node-entrypoint: Sentinel says $MASTER_IP:$MASTER_PORT is master (this node is $SELF_IP) - rejoining as its replica"
echo "replica" > "$STATE_FILE" 2>/dev/null
exec redis-server --replicaof "$MASTER_IP" "$MASTER_PORT" $MIN_REPLICAS_FLAGS
