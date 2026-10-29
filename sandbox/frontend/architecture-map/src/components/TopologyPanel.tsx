import { useEffect, useState } from 'react'
import { InfraToggleControl } from './InfraToggleControl'
import { EnumToggleControl } from './EnumToggleControl'
import { applyInfraConfig, type ApplySystemConfigResult } from '../utils/infraConfig'
import type { InfraStatus, MessagingMode } from '../types/controlApi'

interface Props {
  status: InfraStatus | null
  onClose: () => void
  onApplied: () => void
  onShowAllLabels: () => void
  onHideAllLabels: () => void
}

const MESSAGING_MODE_OPTIONS: { value: MessagingMode; label: string }[] = [
  { value: 'rabbitmq', label: 'RabbitMQ (async)' },
  { value: 'grpc', label: 'gRPC (sync)' },
]

function sameStatus(a: InfraStatus, b: InfraStatus): boolean {
  return (
    a.nginxBypassed === b.nginxBypassed &&
    a.pgcatEnabled === b.pgcatEnabled &&
    a.cacheEnabled === b.cacheEnabled &&
    a.messagingMode === b.messagingMode &&
    a.haproxyEnabled === b.haproxyEnabled &&
    a.postgresReplicasEnabled === b.postgresReplicasEnabled &&
    a.redisEnabled === b.redisEnabled &&
    a.redisSentinelEnabled === b.redisSentinelEnabled &&
    a.mongoReplicasEnabled === b.mongoReplicasEnabled
  )
}

// Global (not node-scoped) modal, same reason PresetsModal is one - this reconfigures several
// nodes at once, so it needs to be reachable regardless of what's selected on the graph. Replaces
// the 4 former per-node toggle controls (NginxToggleControl/PgcatToggleControl/CacheToggleControl/
// MessagingModeControl, all retired) with one panel that stages changes locally and only applies
// them on Save - unlike those immediate-apply controls, so a half-finished edit never mid-air
// recreates containers. Each row below is just a thin controlled usage of the same generic,
// state-less InfraToggleControl/EnumToggleControl those retired wrappers already built on - no
// reason to split this into 4 files when the fetch/busy/error state they used to own individually
// is lifted to this one panel instead.
export function TopologyPanel({ status, onClose, onApplied, onShowAllLabels, onHideAllLabels }: Props) {
  const [draft, setDraft] = useState<InfraStatus | null>(status)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ApplySystemConfigResult | null>(null)

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  // `status` can still be null on first mount if useInfraStatus's initial fetch hasn't resolved
  // yet - seed the draft as soon as it does, but only once (never overwrite an in-progress edit
  // just because the parent's status happened to update, e.g. from a Preset applied elsewhere).
  useEffect(() => {
    if (!draft && status) setDraft(status)
  }, [draft, status])

  if (!draft) {
    return (
      <div className="presets-modal-overlay" onClick={onClose}>
        <div className="presets-modal" onClick={(e) => e.stopPropagation()}>
          <div className="presets-modal-header">
            <h3>Topology</h3>
            <button onClick={onClose} aria-label="Close">
              &times;
            </button>
          </div>
          <p className="presets-modal-note">Reading current configuration...</p>
        </div>
      </div>
    )
  }

  const dirty = status ? !sameStatus(draft, status) : false

  async function handleSave() {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      const applyResult = await applyInfraConfig({ infra: draft, replicas: [] })
      setResult(applyResult)
      onApplied()
    } catch (err) {
      // applyInfraConfig catches each setting's own error internally (see its step() helper) and
      // reports them via `result.failed` instead of throwing - this branch only fires for something
      // genuinely unexpected outside that per-step handling, so it's worth surfacing the real detail
      // rather than a generic message, same as the per-node toggle controls this panel replaced used to.
      console.error('TopologyPanel: applyInfraConfig failed unexpectedly', err)
      setError(`Failed to apply: ${err instanceof Error ? err.message : String(err)}`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="presets-modal-overlay" onClick={onClose}>
      <div className="presets-modal" onClick={(e) => e.stopPropagation()}>
        <div className="presets-modal-header">
          <h3>Topology</h3>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        <p className="presets-modal-note">
          Pick which infra components are in the active path, then Save to apply - the graph only draws participants that are actually in play right now.
        </p>

        <div className="topology-panel-labels">
          <span className="topology-panel-labels-label">Edge labels</span>
          <span className="infra-toggle-description">
            Labels default to hidden (click an edge to reveal just that one, click again to hide it) - these two apply to every edge at once, right away,
            independent of Save below.
          </span>
          <div className="topology-panel-labels-actions">
            <button type="button" onClick={onShowAllLabels}>
              Show all labels
            </button>
            <button type="button" onClick={onHideAllLabels}>
              Hide all labels
            </button>
          </div>
        </div>

        <InfraToggleControl
          label="Load balancing (nginx)"
          description="Off reroutes the load generator (k6) straight to a single link-api/redirect-api container, bypassing nginx - nginx itself keeps running, so the real app UI is unaffected. The diagram's direct frontend-app edges illustrate the broader 'entry point removed' topology this toggle implies, not a literal trace of today's backend behavior - see architecture.json's own note on those edges."
          enabled={!draft.nginxBypassed}
          busy={busy}
          onToggle={(enabled) => setDraft({ ...draft, nginxBypassed: !enabled })}
        />
        <InfraToggleControl
          label="Connection pooling (pgcat)"
          description="Off reconnects every DB-touching service straight to Postgres, bypassing pgcat - no pooling, no read/write split across replicas."
          enabled={draft.pgcatEnabled}
          busy={busy}
          // One-directional, mirroring the backend (SetPgcatEnabledAsync only ever forces haproxy
          // OFF, never back on): off means the whole pooler layer is off, so haproxy's draft value
          // is forced false too, not left showing a stale "ON" it's no longer safe to apply. Turning
          // pgcat back on does NOT restore haproxy's draft value - bringing a node up is always an
          // explicit action (the sub-toggle below + Save), never an automatic side effect of this one.
          onToggle={(enabled) => setDraft({ ...draft, pgcatEnabled: enabled, haproxyEnabled: enabled ? draft.haproxyEnabled : false })}
        />
        <InfraToggleControl
          label="HAProxy (in front of pgcat)"
          description={
            draft.pgcatEnabled
              ? 'Off stops the haproxy container itself - DB-touching services keep dialing it anyway (connection pooling is still on), so this is a real single-point-of-failure demo for the pooler layer, not a graceful bypass.'
              : 'Only meaningful while connection pooling is on - nothing routes through haproxy while pgcat is bypassed.'
          }
          enabled={draft.haproxyEnabled}
          busy={busy}
          disabled={!draft.pgcatEnabled}
          nested
          onToggle={(enabled) => setDraft({ ...draft, haproxyEnabled: enabled })}
        />
        <InfraToggleControl
          label="Read replicas (Postgres)"
          description="Off stops postgres-replica1/2 (volumes kept) and drops them from pgcat's server list - reads fall back to the primary. On starts them back up and re-adds them; streaming resumes from the existing replication slot, no fresh pg_basebackup."
          enabled={draft.postgresReplicasEnabled}
          busy={busy}
          onToggle={(enabled) => setDraft({ ...draft, postgresReplicasEnabled: enabled })}
        />
        <InfraToggleControl
          label="Redis"
          description="On starts redis-master and lets LinkApi/RedirectApi cache against it - a single node, no failover. Off stops redis-master and goes back to always reading Postgres - also forces Sentinel off below (nothing left to fail over to). There's no 'node up but unusable' state here: a redis-master nobody's allowed to read from would have no reason to exist - stop it without losing it via the node's own generic Stop control instead."
          enabled={draft.redisEnabled}
          busy={busy}
          // Unlike every other cascade in this panel (pgcat->haproxy, replicas->pgcat.toml),
          // cacheEnabled moves WITH redisEnabled in both directions, not just forced off on OFF -
          // see InfraStatus's own comment on RedisEnabled for why. Sentinel is the one real
          // one-directional cascade here: forced off on OFF, never restored on ON (bringing the HA
          // layer up is still its own explicit action).
          onToggle={(enabled) =>
            setDraft({
              ...draft,
              redisEnabled: enabled,
              redisSentinelEnabled: enabled ? draft.redisSentinelEnabled : false,
              cacheEnabled: enabled,
            })
          }
        />
        <InfraToggleControl
          label="Redis Sentinel (HA)"
          description={
            draft.redisEnabled
              ? 'Off stops redis-sentinel-1/2/3 and redis-replica1/2 (volumes kept) and recreates LinkApi/RedirectApi/ShortenerService/TrafficService/ReportingService/ReportingApi onto a direct redis-master:6379 connection string - no failover once a sentinel/replica stop.'
              : 'Only meaningful while Redis is on - nothing to monitor or fail over to while Redis is off.'
          }
          enabled={draft.redisSentinelEnabled}
          busy={busy}
          disabled={!draft.redisEnabled}
          nested
          onToggle={(enabled) => setDraft({ ...draft, redisSentinelEnabled: enabled })}
        />
        <InfraToggleControl
          label="Replicas (MongoDB)"
          description="Off reconfigures rs0 down to mongo1 alone (mongo2/mongo3 dropped from the voting set first, then stopped - volumes kept) and points TrafficService at mongo1 only. On starts mongo2/mongo3 back up and rs.add()s them back into rs0 - a freshly rejoined member does a real initial sync, not an instant rejoin, so expect a delay proportional to clicks_meta_db's size before it's fully caught up."
          enabled={draft.mongoReplicasEnabled}
          busy={busy}
          onToggle={(enabled) => setDraft({ ...draft, mongoReplicasEnabled: enabled })}
        />
        <div className="pgcat-pool-control">
          <EnumToggleControl
            label="Messaging transport"
            options={MESSAGING_MODE_OPTIONS}
            value={draft.messagingMode}
            busy={busy}
            onChange={(mode) => setDraft({ ...draft, messagingMode: mode })}
          />
          <p className="infra-toggle-description">
            rabbitmq: LinkApi/RedirectApi enqueue and return immediately. grpc: the request waits for the downstream
            worker to persist the row - no fallback queue if it's unreachable, by design.
          </p>
          {draft.messagingMode === 'grpc' && (
            <p className="messaging-mode-warning">
              ⚠ Demo-only mode, not a production option - it exists to show sync coupling's latency cost. In gRPC
              mode RedirectApi dispatches click.tracked straight to TrafficService instead of publishing to
              RabbitMQ, so the other two fan-out consumers of that event - ShortenerService's click-count bump and
              ReportingService's ClickHouse write - never receive it and silently go stale while this is active.
            </p>
          )}
        </div>

        {error && <p className="service-card-error">{error}</p>}

        {result ? (
          <div className="presets-apply-result">
            <p className="presets-modal-note">
              Applied {result.applied.length} setting{result.applied.length === 1 ? '' : 's'}
              {result.failed.length > 0 && `, ${result.failed.length} failed (${result.failed.join(', ')})`}.
            </p>
            <button onClick={onClose}>Done</button>
          </div>
        ) : (
          <div className="presets-form-actions">
            <button onClick={onClose} disabled={busy}>
              Cancel
            </button>
            <button onClick={handleSave} disabled={busy || !dirty}>
              {busy ? 'Applying...' : 'Save'}
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
