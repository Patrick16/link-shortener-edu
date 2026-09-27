import { useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'

interface Props {
  serviceId: string
  currentReplicas: number
}

// The backend allows up to 100, but each replica is a full container (Kestrel + Npgsql + Redis/
// RabbitMQ clients + an OTLP exporter, plus its own 5s-interval healthcheck) on what's meant to be
// a single developer machine via docker compose, not a real orchestrator with resource-aware
// scheduling. Past this many at once, a laptop-class Docker Desktop allocation can genuinely choke
// (container starts timing out, docker compose stalling, or Docker Desktop itself needing a
// restart) - a confirmation catches an accidental big jump before it happens, without lowering the
// cap itself for whoever actually wants to push it that far on beefier hardware.
const CONFIRM_ABOVE_REPLICAS = 20

export function ScaleControl({ serviceId, currentReplicas }: Props) {
  const [replicas, setReplicas] = useState(currentReplicas)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function apply() {
    if (
      replicas > CONFIRM_ABOVE_REPLICAS &&
      !window.confirm(
        `Scaling ${serviceId} to ${replicas} replicas starts that many containers at once on this machine's docker compose host - on a laptop-class Docker Desktop allocation this can exhaust its CPU/RAM. Continue?`,
      )
    ) {
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await controlApi.scale(serviceId, replicas)
      if (!result.success) {
        setError(result.output || 'Scaling failed')
      }
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="scale-control">
      <label>
        Replicas
        <input type="number" value={replicas} onChange={(e) => setReplicas(Number(e.target.value))} min={1} max={100} disabled={busy} />
      </label>
      <button onClick={apply} disabled={busy || replicas === currentReplicas}>
        {busy ? 'Scaling...' : `Scale to ${replicas}`}
      </button>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
