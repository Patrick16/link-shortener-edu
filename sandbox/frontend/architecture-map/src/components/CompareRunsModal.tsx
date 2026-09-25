import { useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import { formatTimestamp } from '../utils/formatTimestamp'
import type { PgcatPoolSettings, ReplicaCount, ReplicationLagEntry, RunSnapshot, SentinelConfig, TrafficRequest } from '../types/controlApi'

interface Props {
  runIds: string[]
  onClose: () => void
}

interface Row {
  label: string
  values: string[]
}

function summarizeLoad(request: TrafficRequest): string {
  if (request.iterations != null) {
    return `${request.vus} VUs × ${request.iterations} iterations`
  }
  const stages = request.stages ?? []
  const maxVus = Math.max(request.vus, ...stages.map((s) => s.targetVus))
  return `${request.durationSeconds}s ramp, up to ${maxVus} VUs`
}

function formatReplicas(replicas: ReplicaCount[]): string {
  const nonDefault = replicas.filter((r) => r.count !== 1)
  return nonDefault.length > 0 ? nonDefault.map((r) => `${r.serviceId} ×${r.count}`).join(', ') : 'defaults (×1)'
}

function formatPgcatPool(pool: PgcatPoolSettings | null | undefined): string {
  return pool ? `${pool.poolMode}, rw-split ${pool.readWriteSplitting ? 'on' : 'off'}, size ${pool.poolSize}` : '—'
}

function formatSentinel(sentinel: SentinelConfig | null | undefined): string {
  return sentinel ? `down-after ${sentinel.downAfterMs}ms, quorum ${sentinel.quorum}, failover ${sentinel.failoverTimeoutMs}ms` : '—'
}

function formatReplicationLags(lags: ReplicationLagEntry[] | null | undefined): string {
  return lags && lags.length > 0 ? lags.map((l) => `${l.serviceId}: ${l.delayMs}ms`).join(', ') : 'none'
}

function buildConfigRows(runs: RunSnapshot[]): Row[] {
  return [
    { label: 'Scenario', values: runs.map((r) => r.request.scenario) },
    { label: 'Load shape', values: runs.map((r) => summarizeLoad(r.request)) },
    { label: 'Steps', values: runs.map((r) => r.request.steps.map((s) => s.endpointId).join(' → ') || '—') },
    {
      label: 'Data pool',
      values: runs.map((r) => (r.request.dataPool ? `${r.request.dataPool.sourceId} ×${r.request.dataPool.count} (${r.request.dataPool.mode})` : 'off')),
    },
    { label: 'Load balancing', values: runs.map((r) => (r.infra.nginxBypassed ? 'OFF (bypassed)' : 'ON')) },
    { label: 'Connection pooling', values: runs.map((r) => (r.infra.pgcatEnabled ? 'ON' : 'OFF')) },
    { label: 'Caching', values: runs.map((r) => (r.infra.cacheEnabled ? 'ON' : 'OFF')) },
    { label: 'Replicas', values: runs.map((r) => formatReplicas(r.replicas)) },
    { label: 'Pgcat pool', values: runs.map((r) => formatPgcatPool(r.pgcatPool)) },
    { label: 'Sentinel', values: runs.map((r) => formatSentinel(r.sentinel)) },
    { label: 'Replication lag', values: runs.map((r) => formatReplicationLags(r.replicationLags)) },
    { label: 'RabbitMQ prefetch', values: runs.map((r) => (r.rabbitMqPrefetchCount != null ? String(r.rabbitMqPrefetchCount) : '—')) },
    { label: 'Mongo read pref.', values: runs.map((r) => r.mongoReadPreference ?? '—') },
    { label: 'Npgsql pool size', values: runs.map((r) => (r.npgsqlPoolSize != null ? String(r.npgsqlPoolSize) : '—')) },
  ]
}

function buildResultRows(runs: RunSnapshot[]): Row[] {
  return [
    { label: 'Requests', values: runs.map((r) => `${r.report.httpRequests} (${r.report.httpRequestRate.toFixed(1)}/s)`) },
    { label: 'Failed', values: runs.map((r) => `${r.report.failedRequests} (${(r.report.failedRequestRate * 100).toFixed(1)}%)`) },
    { label: 'Iterations', values: runs.map((r) => `${r.report.iterations} (${r.report.iterationRate.toFixed(1)}/s)`) },
    { label: 'Max VUs', values: runs.map((r) => String(r.report.vus)) },
    { label: 'Latency avg', values: runs.map((r) => (r.report.httpReqDuration ? `${r.report.httpReqDuration.avg.toFixed(2)}ms` : '—')) },
    { label: 'Latency p95', values: runs.map((r) => (r.report.httpReqDuration ? `${r.report.httpReqDuration.p95.toFixed(2)}ms` : '—')) },
    { label: 'Exit code', values: runs.map((r) => String(r.report.exitCode)) },
  ]
}

function isDiff(values: string[]): boolean {
  return values.some((v) => v !== values[0])
}

// Fetches the full snapshot for every selected run id (the history list only holds the lightweight
// RunSummary) and lays configuration + results side by side, oldest first - a row is highlighted
// the moment any two runs disagree on it, so "what actually changed between these" reads at a
// glance instead of requiring a field-by-field diff by eye.
export function CompareRunsModal({ runIds, onClose }: Props) {
  const [runs, setRuns] = useState<RunSnapshot[] | null>(null)
  const [missing, setMissing] = useState(0)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  // Keyed on the ids joined into one string, not the `runIds` array itself - the caller passes a
  // fresh `[...checked]` literal on every render, and depending on the array reference directly
  // would refetch (and flash back to "Loading...") on every unrelated re-render of this tree.
  const runIdsKey = runIds.join(',')

  useEffect(() => {
    let cancelled = false
    setRuns(null)
    setError(null)
    Promise.all(runIds.map((id) => controlApi.getRun(id).catch(() => null))).then((results) => {
      if (cancelled) return
      const found = results.filter((r): r is RunSnapshot => r != null).sort((a, b) => a.timestamp.localeCompare(b.timestamp))
      setMissing(results.length - found.length)
      if (found.length === 0) {
        setError('Could not load any of the selected runs.')
      }
      setRuns(found)
    })
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runIdsKey])

  const configRows = runs && runs.length > 0 ? buildConfigRows(runs) : []
  const resultRows = runs && runs.length > 0 ? buildResultRows(runs) : []

  return (
    <div className="compare-modal-overlay" onClick={onClose}>
      <div className="compare-modal" onClick={(e) => e.stopPropagation()}>
        <div className="compare-modal-header">
          <h3>Compare {runIds.length} runs</h3>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        {missing > 0 && <p className="compare-modal-note">{missing} of the selected runs could no longer be loaded.</p>}
        {error && <p className="service-card-error">{error}</p>}
        {!runs && !error && <p className="compare-modal-note">Loading...</p>}

        {runs && runs.length > 0 && (
          <div className="compare-modal-scroll">
            <table className="compare-table">
              <thead>
                <tr>
                  <th></th>
                  {runs.map((r) => (
                    <th key={r.id}>
                      <div className="compare-col-title">{r.request.scenario}</div>
                      <div className="compare-col-timestamp">{formatTimestamp(r.timestamp)}</div>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                <tr className="compare-section-row">
                  <td colSpan={runs.length + 1}>Configuration</td>
                </tr>
                {configRows.map((row) => (
                  <tr key={row.label} className={isDiff(row.values) ? 'compare-row-diff' : undefined}>
                    <td className="compare-row-label">{row.label}</td>
                    {row.values.map((v, i) => (
                      <td key={i}>{v}</td>
                    ))}
                  </tr>
                ))}
                <tr className="compare-section-row">
                  <td colSpan={runs.length + 1}>Results</td>
                </tr>
                {resultRows.map((row) => (
                  <tr key={row.label} className={isDiff(row.values) ? 'compare-row-diff' : undefined}>
                    <td className="compare-row-label">{row.label}</td>
                    {row.values.map((v, i) => (
                      <td key={i}>{v}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  )
}
