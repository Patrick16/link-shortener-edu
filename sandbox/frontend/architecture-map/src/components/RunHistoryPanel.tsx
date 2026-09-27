import { useEffect, useRef, useState } from 'react'
import { controlApi } from '../api/controlApi'
import { TrafficReportView } from './TrafficReportView'
import { CompareRunsModal } from './CompareRunsModal'
import { formatTimestamp } from '../utils/formatTimestamp'
import type { RunSnapshot, RunSummary } from '../types/controlApi'

interface Props {
  lastSavedRunId: string | null
  onReuseRun: (snapshot: RunSnapshot) => void
}

// Right sidebar: every past run, newest first - each one a full snapshot of both the report AND
// the system configuration it ran under (infra toggles, replica counts, connection counts at the
// time), so "why did this run behave differently from that one" has an actual answer later instead
// of just a number with no context.
export function RunHistoryPanel({ lastSavedRunId, onReuseRun }: Props) {
  const [runs, setRuns] = useState<RunSummary[]>([])
  const [selected, setSelected] = useState<RunSnapshot | null>(null)
  const [loadingId, setLoadingId] = useState<string | null>(null)
  const [clearing, setClearing] = useState(false)
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [deleting, setDeleting] = useState(false)
  const [comparing, setComparing] = useState(false)
  const [deleteFailedCount, setDeleteFailedCount] = useState(0)
  const [clearError, setClearError] = useState<string | null>(null)

  // Bumped every time a clear actually completes, so a refresh() started before it (e.g. the
  // lastSavedRunId effect firing right as the user clicks "Clear history") can tell it's now stale
  // instead of applying its response after the fact - see clearHistory's own comment below.
  const clearGenerationRef = useRef(0)

  function refresh() {
    const generation = clearGenerationRef.current
    controlApi
      .listRuns()
      .then((fetched) => {
        if (generation === clearGenerationRef.current) {
          setRuns(fetched)
        }
      })
      .catch(() => {})
  }

  useEffect(refresh, [])
  useEffect(() => {
    if (lastSavedRunId) refresh()
  }, [lastSavedRunId])

  // A run disappearing from the list (cleared/deleted elsewhere) shouldn't leave its id lingering
  // in the selection - the toolbar's counts and the compare/delete actions both key off `checked`.
  useEffect(() => {
    setChecked((prev) => {
      const known = new Set(runs.map((r) => r.id))
      const next = new Set([...prev].filter((id) => known.has(id)))
      return next.size === prev.size ? prev : next
    })
  }, [runs])

  async function openRun(id: string) {
    setLoadingId(id)
    try {
      setSelected(await controlApi.getRun(id))
    } catch {
      setSelected(null)
    } finally {
      setLoadingId(null)
    }
  }

  async function clearHistory() {
    // "Clear" wipes every saved run in one click with no undo, unlike the single-/multi-select
    // delete actions below it - a misclick here has a much larger blast radius than those.
    if (!window.confirm(`Delete all ${runs.length} saved run${runs.length === 1 ? '' : 's'}? This cannot be undone.`)) {
      return
    }

    setClearing(true)
    setClearError(null)
    try {
      const { failedCount } = await controlApi.clearRuns()
      // A clear that just completed always wins over whatever refresh() call was already in
      // flight when the user clicked it - bumping this makes any such call's own response arrive
      // as stale (see refresh()'s check) instead of silently repopulating runs with the pre-clear
      // list a moment after this function already emptied it.
      clearGenerationRef.current += 1
      if (failedCount > 0) {
        // Some files failed to delete server-side (see RunHistoryStore.ClearAsync) - re-fetch what
        // actually survived instead of assuming everything was cleared.
        setClearError(`Failed to delete ${failedCount} run${failedCount === 1 ? '' : 's'} - still shown below.`)
        refresh()
      } else {
        setRuns([])
      }
      setChecked(new Set())
    } catch {
      setClearError('Failed to clear run history - see the console for details.')
    } finally {
      setClearing(false)
    }
  }

  function toggleChecked(id: string) {
    setChecked((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  async function deleteChecked() {
    const ids = [...checked]
    if (ids.length === 0) return
    setDeleting(true)
    setDeleteFailedCount(0)
    try {
      // Per-id success/failure, not just "did the batch throw" - a network blip or control-api
      // restarting mid-delete on just one of several selected runs used to still remove every
      // checked id from the visible list (Promise.all(...).catch(() => {}) swallowed the failure),
      // making a run whose file was never actually deleted disappear as if it had been - it only
      // reappeared on the next refresh, reading as the delete having "undone itself".
      const results = await Promise.allSettled(ids.map((id) => controlApi.deleteRun(id)))
      const succeeded = new Set(ids.filter((_, i) => results[i].status === 'fulfilled'))
      const failedCount = ids.length - succeeded.size

      setRuns((prev) => prev.filter((r) => !succeeded.has(r.id)))
      setChecked((prev) => new Set([...prev].filter((id) => !succeeded.has(id))))
      setDeleteFailedCount(failedCount)
    } finally {
      setDeleting(false)
    }
  }

  if (comparing) {
    return <CompareRunsModal runIds={[...checked]} onClose={() => setComparing(false)} />
  }

  if (selected) {
    return (
      <div className="run-history-panel">
        <div className="run-history-detail-header">
          <button onClick={() => setSelected(null)}>&larr; Back to history</button>
          <button className="run-history-reuse" onClick={() => onReuseRun(selected)} title="Load this run's test sequence/ramp and re-apply its system configuration">
            Reuse config
          </button>
        </div>
        <h3 className="run-history-detail-title">{selected.request.scenario}</h3>
        <p className="run-history-detail-timestamp">{formatTimestamp(selected.timestamp)}</p>

        <div className="run-history-detail-section">
          <h4>Steps</h4>
          <ol className="run-history-steps">
            {selected.request.steps.map((step, i) => (
              <li key={i}>
                {step.endpointId}
                {step.pauseAfterSeconds > 0 && ` (pause ${step.pauseAfterSeconds}s)`}
              </li>
            ))}
          </ol>
        </div>

        <div className="run-history-detail-section">
          <h4>Infra at the time</h4>
          <ul className="run-history-infra">
            <li>load balancing: {selected.infra.nginxBypassed ? 'OFF (bypassed)' : 'ON'}</li>
            <li>connection pooling: {selected.infra.pgcatEnabled ? 'ON' : 'OFF'}</li>
            <li>caching: {selected.infra.cacheEnabled ? 'ON' : 'OFF'}</li>
          </ul>
          <ul className="run-history-infra">
            {selected.replicas
              .filter((r) => r.count !== 1)
              .map((r) => (
                <li key={r.serviceId}>
                  {r.serviceId}: &times;{r.count}
                </li>
              ))}
          </ul>
        </div>

        {selected.postgresConnections && (
          <div className="run-history-detail-section">
            <h4>Postgres connections at the time</h4>
            <ul className="run-history-infra">
              {Object.entries(selected.postgresConnections.connectionsByDatabase).map(([db, count]) => (
                <li key={db}>
                  {db}: {count}
                </li>
              ))}
            </ul>
          </div>
        )}

        <TrafficReportView report={selected.report} verdict={selected.verdict} traceHops={selected.traceHops} />
      </div>
    )
  }

  return (
    <div className="run-history-panel">
      <div className="run-history-header">
        <h3 className="traffic-panel-subheading">Past runs</h3>
        {runs.length > 0 && (
          <button className="run-history-clear" onClick={clearHistory} disabled={clearing}>
            {clearing ? 'Clearing...' : 'Clear history'}
          </button>
        )}
      </div>
      {clearError && <p className="service-card-error">{clearError}</p>}

      {checked.size > 0 && (
        <div className="run-history-selection-toolbar">
          <span className="run-history-selection-count">{checked.size} selected</span>
          <button onClick={() => setComparing(true)} disabled={checked.size < 2} title={checked.size < 2 ? 'Select at least 2 runs to compare' : undefined}>
            Compare
          </button>
          <button className="run-history-selection-delete" onClick={deleteChecked} disabled={deleting}>
            {deleting ? 'Deleting...' : 'Delete'}
          </button>
          <button className="run-history-selection-clear" onClick={() => setChecked(new Set())}>
            Clear selection
          </button>
          {deleteFailedCount > 0 && (
            <p className="service-card-error">
              Failed to delete {deleteFailedCount} of the selected run{deleteFailedCount === 1 ? '' : 's'} - still shown below.
            </p>
          )}
        </div>
      )}

      {runs.length === 0 && <p className="run-history-empty">No runs yet - completed runs will show up here.</p>}
      <ul className="run-history-list">
        {runs.map((run) => (
          <li key={run.id} className="run-history-row">
            <input
              type="checkbox"
              className="run-history-checkbox"
              checked={checked.has(run.id)}
              onChange={() => toggleChecked(run.id)}
              aria-label={`Select run ${run.scenario}`}
            />
            <button className="run-history-item" onClick={() => openRun(run.id)} disabled={loadingId === run.id}>
              <span className="run-history-item-name">{run.scenario}</span>
              <span className="run-history-item-meta">
                {formatTimestamp(run.timestamp)} · {run.httpRequests} reqs ·{' '}
                <span className="run-history-item-rps">{run.httpRequestRate.toFixed(1)} rps</span>
                {run.failedRequests > 0 && <span className="run-history-item-failed"> · {run.failedRequests} failed</span>}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}
