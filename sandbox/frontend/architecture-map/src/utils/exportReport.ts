import type { BottleneckSuspect, ChecklistStep, RunSnapshot, TraceHopStats, TrafficReport, BottleneckVerdict } from '../types/controlApi'
import { formatTimestamp } from './formatTimestamp'

export interface ReportExportInput {
  report: TrafficReport
  verdict?: BottleneckVerdict | null
  traceHops?: TraceHopStats[] | null
  // Present only when exporting from RunHistoryPanel's detail view - lets the report include the
  // system configuration the run happened under, not just the k6 result (see RunSnapshot).
  snapshot?: RunSnapshot | null
}

interface KeyValue {
  label: string
  value: string
}

interface StatusEndpointRows {
  endpoint: string
  rows: KeyValue[]
}

// One shared, presentation-agnostic view over the report - both the Markdown and HTML renderers
// below build off this so they can never drift into showing different numbers.
interface ReportData {
  scenario: string
  timestamp: string | null
  configRows: KeyValue[]
  statTiles: KeyValue[]
  latencyRows: KeyValue[]
  checkRows: KeyValue[]
  statusSections: StatusEndpointRows[]
  topHops: TraceHopStats[]
  checklist: ChecklistStep[]
  suspects: BottleneckSuspect[]
  rawOutput: string
}

function slugify(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-+|-+$)/g, '') || 'run'
}

export function reportExportFilename(input: ReportExportInput, extension: string): string {
  const scenario = slugify(input.snapshot?.request.scenario ?? input.report.scenario ?? 'run')
  const stamp = (input.snapshot?.timestamp ?? new Date().toISOString()).replace(/[:.]/g, '-')
  return `traffic-report-${scenario}-${stamp}.${extension}`
}

function formatReplicas(snapshot: RunSnapshot): string {
  const nonDefault = snapshot.replicas.filter((r) => r.count !== 1)
  return nonDefault.length > 0 ? nonDefault.map((r) => `${r.serviceId} x${r.count}`).join(', ') : 'defaults (x1)'
}

function buildConfigRows(snapshot: RunSnapshot): KeyValue[] {
  const rows: KeyValue[] = [
    { label: 'Steps', value: snapshot.request.steps.map((s) => s.endpointId).join(' -> ') || '-' },
    { label: 'Load balancing', value: snapshot.infra.nginxBypassed ? 'OFF (bypassed)' : 'ON' },
    { label: 'Connection pooling', value: snapshot.infra.pgcatEnabled ? 'ON' : 'OFF' },
    { label: 'Caching', value: snapshot.infra.cacheEnabled ? 'ON' : 'OFF' },
    { label: 'Replicas', value: formatReplicas(snapshot) },
  ]

  if (snapshot.pgcatPool) {
    rows.push({
      label: 'Pgcat pool',
      value: `${snapshot.pgcatPool.poolMode}, rw-split ${snapshot.pgcatPool.readWriteSplitting ? 'on' : 'off'}, size ${snapshot.pgcatPool.poolSize}`,
    })
  }
  if (snapshot.sentinel) {
    rows.push({
      label: 'Sentinel',
      value: `down-after ${snapshot.sentinel.downAfterMs}ms, quorum ${snapshot.sentinel.quorum}, failover ${snapshot.sentinel.failoverTimeoutMs}ms`,
    })
  }
  if (snapshot.replicationLags && snapshot.replicationLags.length > 0) {
    rows.push({ label: 'Replication lag', value: snapshot.replicationLags.map((l) => `${l.serviceId}: ${l.delayMs}ms`).join(', ') })
  }
  if (snapshot.rabbitMqPrefetchCount != null) {
    rows.push({ label: 'RabbitMQ prefetch', value: String(snapshot.rabbitMqPrefetchCount) })
  }
  if (snapshot.mongoReadPreference) {
    rows.push({ label: 'Mongo read preference', value: snapshot.mongoReadPreference })
  }
  if (snapshot.npgsqlPoolSize != null) {
    rows.push({ label: 'Npgsql pool size', value: String(snapshot.npgsqlPoolSize) })
  }

  return rows
}

function collectReportData(input: ReportExportInput): ReportData {
  const { report, verdict, traceHops, snapshot } = input

  const statTiles: KeyValue[] = [
    { label: 'requests', value: `${report.httpRequests} (${report.httpRequestRate.toFixed(1)}/s)` },
    { label: 'failed', value: `${report.failedRequests} (${(report.failedRequestRate * 100).toFixed(1)}%)` },
    { label: 'iterations', value: `${report.iterations} (${report.iterationRate.toFixed(1)}/s)` },
    { label: 'max VUs', value: String(report.vus) },
    { label: 'exit code', value: String(report.exitCode) },
  ]

  const latencyRows: KeyValue[] = report.httpReqDuration
    ? (['avg', 'med', 'p90', 'p95', 'max'] as const).map((key) => ({ label: key, value: `${report.httpReqDuration![key].toFixed(2)}ms` }))
    : []

  const checkRows: KeyValue[] = report.checks.map((c) => ({ label: c.name, value: `${c.passes}/${c.passes + c.fails}` }))

  const statusSections: StatusEndpointRows[] = report.statusBreakdownByEndpoint.map((e) => ({
    endpoint: e.endpointId,
    rows: e.statusCounts.map((s) => ({ label: s.label, value: String(s.count) })),
  }))

  const topHops = [...(traceHops ?? [])].sort((a, b) => b.p95Ms - a.p95Ms).slice(0, 6)

  return {
    scenario: snapshot?.request.scenario ?? report.scenario,
    timestamp: snapshot?.timestamp ?? null,
    configRows: snapshot ? buildConfigRows(snapshot) : [],
    statTiles,
    latencyRows,
    checkRows,
    statusSections,
    topHops,
    checklist: verdict?.checklist ?? [],
    suspects: verdict?.suspects ?? [],
    rawOutput: report.rawOutput,
  }
}

function mdTable(rows: KeyValue[]): string {
  if (rows.length === 0) return ''
  return ['| | |', '| --- | --- |', ...rows.map((r) => `| ${r.label} | ${r.value} |`)].join('\n')
}

export function buildReportMarkdown(input: ReportExportInput): string {
  const data = collectReportData(input)
  const lines: string[] = []

  lines.push(`# Traffic report - ${data.scenario}`)
  if (data.timestamp) lines.push(`_${formatTimestamp(data.timestamp)}_`)
  lines.push('')

  if (data.configRows.length > 0) {
    lines.push('## Configuration', '', mdTable(data.configRows), '')
  }

  lines.push('## Summary', '', mdTable(data.statTiles), '')

  if (data.latencyRows.length > 0) {
    lines.push('## http_req_duration', '', mdTable(data.latencyRows), '')
  }

  if (data.checkRows.length > 0) {
    lines.push('## Checks', '', mdTable(data.checkRows), '')
  }

  if (data.statusSections.length > 0) {
    lines.push('## Status codes by endpoint', '')
    for (const section of data.statusSections) {
      lines.push(`### ${section.endpoint}`, '', mdTable(section.rows), '')
    }
  }

  if (data.topHops.length > 0) {
    lines.push('## Max processing time (traces)', '', '| node | hop | p95 | max | calls |', '| --- | --- | --- | --- | --- |')
    for (const hop of data.topHops) {
      lines.push(`| ${hop.serviceId} | ${hop.spanName} | ${hop.p95Ms.toFixed(0)}ms | ${hop.maxMs.toFixed(0)}ms | ${hop.count} |`)
    }
    lines.push('')
  }

  if (data.checklist.length > 0) {
    lines.push('## How to find the bottleneck', '')
    for (const step of data.checklist) {
      lines.push(`1. **${step.title}** - ${step.explanation}`)
      if (step.finding) lines.push(`   - ${step.finding}`)
    }
    lines.push('')
  }

  lines.push('## Recommendations')
  lines.push('')
  if (data.suspects.length === 0) {
    lines.push('No clear bottleneck detected - resources, pools, and traces look normal for this run.')
  } else {
    data.suspects.forEach((s, i) => {
      lines.push(`${i + 1}. **${s.serviceId}** (${s.nodeType})`)
      lines.push(`   - Evidence: ${s.evidence}`)
      lines.push(`   - Recommendation: ${s.recommendation}`)
    })
  }
  lines.push('')

  lines.push('## Raw k6 output', '', '```', data.rawOutput, '```', '')

  return lines.join('\n')
}

function escapeHtml(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

function htmlTable(rows: KeyValue[]): string {
  if (rows.length === 0) return ''
  const body = rows.map((r) => `<tr><td>${escapeHtml(r.label)}</td><td>${escapeHtml(r.value)}</td></tr>`).join('')
  return `<table>${body}</table>`
}

export function buildReportHtml(input: ReportExportInput): string {
  const data = collectReportData(input)
  const parts: string[] = []

  parts.push(`<h1>Traffic report - ${escapeHtml(data.scenario)}</h1>`)
  if (data.timestamp) parts.push(`<p class="meta">${escapeHtml(formatTimestamp(data.timestamp))}</p>`)

  if (data.configRows.length > 0) {
    parts.push('<h2>Configuration</h2>', htmlTable(data.configRows))
  }

  parts.push('<h2>Summary</h2>', htmlTable(data.statTiles))

  if (data.latencyRows.length > 0) {
    parts.push('<h2>http_req_duration</h2>', htmlTable(data.latencyRows))
  }

  if (data.checkRows.length > 0) {
    parts.push('<h2>Checks</h2>', htmlTable(data.checkRows))
  }

  if (data.statusSections.length > 0) {
    parts.push('<h2>Status codes by endpoint</h2>')
    for (const section of data.statusSections) {
      parts.push(`<h3>${escapeHtml(section.endpoint)}</h3>`, htmlTable(section.rows))
    }
  }

  if (data.topHops.length > 0) {
    const rows = data.topHops
      .map(
        (h) =>
          `<tr><td>${escapeHtml(h.serviceId)}</td><td>${escapeHtml(h.spanName)}</td><td>${h.p95Ms.toFixed(0)}ms</td><td>${h.maxMs.toFixed(0)}ms</td><td>${h.count}</td></tr>`,
      )
      .join('')
    parts.push(
      '<h2>Max processing time (traces)</h2>',
      `<table><tr><th>node</th><th>hop</th><th>p95</th><th>max</th><th>calls</th></tr>${rows}</table>`,
    )
  }

  if (data.checklist.length > 0) {
    parts.push('<h2>How to find the bottleneck</h2>', '<ol>')
    for (const step of data.checklist) {
      parts.push(
        `<li><strong>${escapeHtml(step.title)}</strong> - ${escapeHtml(step.explanation)}${step.finding ? `<div class="finding">${escapeHtml(step.finding)}</div>` : ''}</li>`,
      )
    }
    parts.push('</ol>')
  }

  parts.push('<h2>Recommendations</h2>')
  if (data.suspects.length === 0) {
    parts.push('<p class="clean">No clear bottleneck detected - resources, pools, and traces look normal for this run.</p>')
  } else {
    parts.push('<ol>')
    for (const s of data.suspects) {
      parts.push(
        `<li><strong>${escapeHtml(s.serviceId)}</strong> <span class="node-type">(${escapeHtml(s.nodeType)})</span>` +
          `<div class="evidence">${escapeHtml(s.evidence)}</div>` +
          `<div class="recommendation">${escapeHtml(s.recommendation)}</div></li>`,
      )
    }
    parts.push('</ol>')
  }

  parts.push('<h2>Raw k6 output</h2>', `<pre>${escapeHtml(data.rawOutput)}</pre>`)

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Traffic report - ${escapeHtml(data.scenario)}</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: -apple-system, Segoe UI, sans-serif; max-width: 860px; margin: 32px auto; padding: 0 16px; line-height: 1.5; }
  h1 { margin-bottom: 4px; }
  .meta { color: #888; margin-top: 0; }
  h2 { margin-top: 32px; border-bottom: 1px solid #8884; padding-bottom: 4px; }
  table { border-collapse: collapse; width: 100%; margin: 8px 0 16px; }
  td, th { border: 1px solid #8884; padding: 6px 10px; text-align: left; font-size: 14px; }
  pre { background: #8881; padding: 12px; overflow: auto; border-radius: 6px; white-space: pre-wrap; word-break: break-word; }
  .finding { color: #888; font-size: 13px; margin: 2px 0 8px; }
  .recommendation { font-weight: 600; margin-bottom: 8px; }
  .evidence { color: #888; font-size: 13px; }
  .node-type { color: #888; font-weight: normal; }
  .clean { color: #888; }
</style>
</head>
<body>
${parts.join('\n')}
</body>
</html>
`
}

export function downloadTextFile(filename: string, content: string, mimeType: string): void {
  const blob = new Blob([content], { type: mimeType })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}
