import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import { TrafficReportView } from './TrafficReportView'
import type { RunSnapshot, RunSummary } from '../types/controlApi'

interface Props {
  lastSavedRunId: string | null
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

// Right sidebar: every past run, newest first - each one a full snapshot of both the report AND
// the system configuration it ran under (infra toggles, replica counts, connection counts at the
// time), so "why did this run behave differently from that one" has an actual answer later instead
// of just a number with no context.
export function RunHistoryPanel({ lastSavedRunId }: Props) {
  const [runs, setRuns] = useState<RunSummary[]>([])
  const [selected, setSelected] = useState<RunSnapshot | null>(null)
  const [loadingId, setLoadingId] = useState<string | null>(null)
  const [clearing, setClearing] = useState(false)

  function refresh() {
    controlApi.listRuns().then(setRuns).catch(() => {})
  }

  useEffect(refresh, [])
  useEffect(() => {
    if (lastSavedRunId) refresh()
  }, [lastSavedRunId])

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
    setClearing(true)
    try {
      await controlApi.clearRuns()
      setRuns([])
    } finally {
      setClearing(false)
    }
  }

  if (selected) {
    return (
      <div className="run-history-panel">
        <div className="run-history-detail-header">
          <button onClick={() => setSelected(null)}>&larr; Back to history</button>
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

        <TrafficReportView report={selected.report} />
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
      {runs.length === 0 && <p className="run-history-empty">No runs yet - completed runs will show up here.</p>}
      <ul className="run-history-list">
        {runs.map((run) => (
          <li key={run.id}>
            <button className="run-history-item" onClick={() => openRun(run.id)} disabled={loadingId === run.id}>
              <span className="run-history-item-name">{run.scenario}</span>
              <span className="run-history-item-meta">
                {formatTimestamp(run.timestamp)} · {run.httpRequests} reqs
                {run.failedRequests > 0 && <span className="run-history-item-failed"> · {run.failedRequests} failed</span>}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}
