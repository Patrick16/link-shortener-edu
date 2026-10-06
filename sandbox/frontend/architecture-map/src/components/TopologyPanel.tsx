import { useEffect, useState } from 'react'
import { InfraToggleControl } from './InfraToggleControl'
import { EnumToggleControl } from './EnumToggleControl'
import { applyInfraConfig, type ApplySystemConfigResult } from '../utils/infraConfig'
import type { InfraStatus, MessagingMode } from '../types/controlApi'

interface Props {
  status: InfraStatus | null
  onClose: () => void
  onApplied: () => void
}

const MESSAGING_MODE_OPTIONS: { value: MessagingMode; label: string }[] = [
  { value: 'rabbitmq', label: 'RabbitMQ (async)' },
  { value: 'grpc', label: 'gRPC (sync)' },
]

function sameStatus(a: InfraStatus, b: InfraStatus): boolean {
  return a.nginxBypassed === b.nginxBypassed && a.pgcatEnabled === b.pgcatEnabled && a.cacheEnabled === b.cacheEnabled && a.messagingMode === b.messagingMode
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
export function TopologyPanel({ status, onClose, onApplied }: Props) {
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
          onToggle={(enabled) => setDraft({ ...draft, pgcatEnabled: enabled })}
        />
        <InfraToggleControl
          label="Caching (Redis)"
          description="Off makes LinkApi/RedirectApi skip Redis (and its replicas/Sentinels) entirely and always read Postgres."
          enabled={draft.cacheEnabled}
          busy={busy}
          onToggle={(enabled) => setDraft({ ...draft, cacheEnabled: enabled })}
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
