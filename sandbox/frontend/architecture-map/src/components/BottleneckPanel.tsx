import type { BottleneckVerdict, TraceHopStats } from '../types/controlApi'

interface Props {
  verdict?: BottleneckVerdict | null
  traceHops?: TraceHopStats[] | null
}

// Two views over the same run data, deliberately kept side by side rather than one replacing the
// other: a guided checklist (teaches what to look at and in what order, same as you'd do it by
// hand) always shown open, and the rule-based verdict (the short answer) collapsed behind
// <details> so it doesn't short-circuit the "figure it out yourself" step for someone using this to
// learn, but is one click away for someone who just wants the answer. Both are built from the exact
// same BottleneckAdvisor computation on the backend, so they can't disagree with each other.
export function BottleneckPanel({ verdict, traceHops }: Props) {
  if (!verdict) {
    return null // Older run snapshots (saved before this feature existed) simply have no verdict.
  }

  const topHops = [...(traceHops ?? [])].sort((a, b) => b.p95Ms - a.p95Ms).slice(0, 6)

  return (
    <div className="bottleneck-panel">
      <div className="bottleneck-checklist">
        <h3>How to find the bottleneck</h3>
        <ol>
          {verdict.checklist.map((step) => (
            <li key={step.title} className="bottleneck-checklist-step">
              <div className="bottleneck-checklist-title">{step.title}</div>
              <div className="bottleneck-checklist-explanation">{step.explanation}</div>
              {step.finding && <div className="bottleneck-checklist-finding">{step.finding}</div>}
            </li>
          ))}
        </ol>
      </div>

      {topHops.length > 0 && (
        <div className="bottleneck-hops">
          <h3>Max processing time (traces)</h3>
          <table className="bottleneck-hops-table">
            <thead>
              <tr>
                <th>Node</th>
                <th>Hop</th>
                <th>p95</th>
                <th>max</th>
                <th>calls</th>
              </tr>
            </thead>
            <tbody>
              {topHops.map((hop) => (
                <tr key={`${hop.serviceId}-${hop.spanName}`}>
                  <td>{hop.serviceId}</td>
                  <td>{hop.spanName}</td>
                  <td>{hop.p95Ms.toFixed(0)} ms</td>
                  <td>{hop.maxMs.toFixed(0)} ms</td>
                  <td>{hop.count}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <details className="bottleneck-verdict">
        <summary>Show automatic verdict</summary>
        {verdict.suspects.length === 0 ? (
          <p className="bottleneck-verdict-clean">No clear bottleneck detected - resources, pools, and traces look normal for this run.</p>
        ) : (
          <ul className="bottleneck-suspect-list">
            {verdict.suspects.map((suspect, i) => (
              <li key={`${suspect.serviceId}-${i}`} className={i === 0 ? 'bottleneck-suspect bottleneck-suspect-primary' : 'bottleneck-suspect'}>
                <div className="bottleneck-suspect-node">
                  {suspect.serviceId} <span className="bottleneck-suspect-type">({suspect.nodeType})</span>
                </div>
                <div className="bottleneck-suspect-evidence">{suspect.evidence}</div>
                <div className="bottleneck-suspect-recommendation">{suspect.recommendation}</div>
              </li>
            ))}
          </ul>
        )}
      </details>
    </div>
  )
}
