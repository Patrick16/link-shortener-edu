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
#
# That "no Sentinel has an opinion" signal used to mean two very different things treated identically:
# (a) a genuine cold start (no Sentinel container exists at all - getent fails for every one), and
# (b) redis-master being restarted at a moment when Sentinel containers *exist* but are all
# transiently unreachable (e.g. themselves mid-restart during the same chaos test exercising Sentinel
# resilience), even though a real failover already happened and blindly starting as master here would
# be exactly the split-brain-on-rejoin this file exists to prevent. The two are now told apart:
# any_sentinel_host_resolved distinguishes "no Sentinel container exists" (getent fails for all three
# - safe to start as master immediately, same as before) from "Sentinel container(s) exist but none
# answered" (getent succeeds for at least one, but SENTINEL get-master-addr-by-name still comes back
# empty from all of them - retried a few times before falling through, since this is most likely
# transient). If retries exhaust with still no answer, STATE_FILE (this node's last Sentinel-confirmed
# role, persisted in the redis-master-state volume so it survives a container recreate) breaks the
# remaining ambiguity: last confirmed role "replica" means this exact split-brain-on-rejoin scenario
# is plausibly happening right now, so it's logged as an explicit, loud AMBIGUOUS warning distinct
# from a normal cold start - not silently treated as one - before still starting as master (there is
# no safe alternative a lone shell script can take without human input or real distributed
# consensus; --min-replicas-to-write below is what keeps that master from actually diverging data in
# the meantime).
#
# Also: the original version trusted whichever Sentinel answered first, with no check for the other
# two disagreeing (a stale/minority Sentinel that hasn't heard about a fresh failover yet). pick_majority
# below requires at least two of the three to agree before an answer is trusted; a lone answer is
# still accepted (that's the same "only 1 of 3 Sentinels reachable" tolerance the whole point of
# running 3 Sentinels is meant to provide), but a straight disagreement with no majority is treated
# the same as "no answer" and goes through the same retry/ambiguity handling above.

SENTINELS="redis-sentinel-1 redis-sentinel-2 redis-sentinel-3"
STATE_FILE="/state/last-confirmed-role"
MIN_REPLICAS_FLAGS="--min-replicas-to-write 1 --min-replicas-max-lag 10"

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
  echo "master-entrypoint: Sentinel container(s) exist but gave no confident answer (attempt $attempt/$max_attempts) - retrying..."
  sleep 2
  query_sentinels_once
  MASTER_ADDR="$QUERY_RESULT"
  attempt=$((attempt + 1))
done

mkdir -p "$(dirname "$STATE_FILE")" 2>/dev/null
LAST_ROLE=""
[ -f "$STATE_FILE" ] && LAST_ROLE=$(cat "$STATE_FILE" 2>/dev/null)

if [ -z "$MASTER_ADDR" ]; then
  if [ "$ANY_SENTINEL_HOST_RESOLVED" = "0" ]; then
    echo "master-entrypoint: no Sentinel container exists yet (genuine cold start) - starting as master"
  elif [ "$LAST_ROLE" = "replica" ]; then
    echo "master-entrypoint: AMBIGUOUS - Sentinel container(s) exist but none gave a confident answer after $max_attempts attempts, and this node's last Sentinel-confirmed role was replica. This is NOT a normal cold start - it looks like the split-brain-on-rejoin scenario this script exists to prevent, just with Sentinel itself unreachable right now. Starting as master anyway (write-fencing via '$MIN_REPLICAS_FLAGS' keeps it from accepting writes until a replica connects), but the topology MUST be reconciled manually once Sentinel is reachable again."
  else
    echo "master-entrypoint: Sentinel container(s) exist but none gave a confident answer after $max_attempts attempts; this node has no prior confirmed replica role - starting as master"
  fi
  exec redis-server $MIN_REPLICAS_FLAGS
fi

MASTER_IP=$(echo "$MASTER_ADDR" | awk '{print $1}')
MASTER_PORT=$(echo "$MASTER_ADDR" | awk '{print $2}')
SELF_IP=$(getent hosts "$(hostname)" | awk '{print $1}' | head -1)

if [ "$MASTER_IP" = "$SELF_IP" ]; then
  echo "master-entrypoint: Sentinel already considers this node ($SELF_IP) the master - starting as master"
  echo "master" > "$STATE_FILE" 2>/dev/null
  exec redis-server $MIN_REPLICAS_FLAGS
fi

echo "master-entrypoint: Sentinel says $MASTER_IP:$MASTER_PORT is master (this node is $SELF_IP) - rejoining as its replica"
echo "replica" > "$STATE_FILE" 2>/dev/null
exec redis-server --replicaof "$MASTER_IP" "$MASTER_PORT" $MIN_REPLICAS_FLAGS
