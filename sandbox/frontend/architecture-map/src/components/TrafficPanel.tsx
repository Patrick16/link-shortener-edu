import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { TrafficResult } from '../types/controlApi'

export function TrafficPanel() {
  const [scenarios, setScenarios] = useState<string[]>([])
  const [scenario, setScenario] = useState('')
  const [vus, setVus] = useState(3)
  const [duration, setDuration] = useState(10)
  const [running, setRunning] = useState(false)
  const [result, setResult] = useState<TrafficResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .listTrafficScenarios()
      .then((list) => {
        setScenarios(list)
        if (list.length > 0) {
          setScenario(list[0])
        }
      })
      .catch((err) => setError(String(err)))
  }, [])

  async function run() {
    setRunning(true)
    setError(null)
    setResult(null)
    try {
      const res = await controlApi.runTraffic({ scenario, vus, durationSeconds: duration })
      setResult(res)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setRunning(false)
    }
  }

  return (
    <div className="traffic-panel">
      <div className="traffic-panel-controls">
        <select value={scenario} onChange={(e) => setScenario(e.target.value)} disabled={running || scenarios.length === 0}>
          {scenarios.length === 0 && <option>No scenarios available</option>}
          {scenarios.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <label>
          VUs
          <input type="number" value={vus} onChange={(e) => setVus(Number(e.target.value))} min={1} max={200} disabled={running} />
        </label>
        <label>
          Duration (s)
          <input type="number" value={duration} onChange={(e) => setDuration(Number(e.target.value))} min={1} max={120} disabled={running} />
        </label>
        <button onClick={run} disabled={running || !scenario}>
          {running ? `Running ${scenario}...` : 'Run traffic'}
        </button>
      </div>

      {error && <p className="service-card-error">{error}</p>}

      {result && (
        <details className="traffic-result" open>
          <summary>
            {result.scenario} finished (exit code {result.exitCode})
          </summary>
          <pre>{result.output}</pre>
        </details>
      )}
    </div>
  )
}
