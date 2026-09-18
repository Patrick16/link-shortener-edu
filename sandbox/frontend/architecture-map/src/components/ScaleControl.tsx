import { useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'

interface Props {
  serviceId: string
  currentReplicas: number
}

export function ScaleControl({ serviceId, currentReplicas }: Props) {
  const [replicas, setReplicas] = useState(currentReplicas)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function apply() {
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
        <input type="number" value={replicas} onChange={(e) => setReplicas(Number(e.target.value))} min={1} max={10} disabled={busy} />
      </label>
      <button onClick={apply} disabled={busy || replicas === currentReplicas}>
        {busy ? 'Scaling...' : `Scale to ${replicas}`}
      </button>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
