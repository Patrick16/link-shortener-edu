import { useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { ChaosType } from '../types/controlApi'

interface Props {
  serviceId: string
  state: string
}

// Just the action buttons (Stop/Start/Restart/Heal/Degrade) - NodePanel composes this with
// ComponentCard (details) and Sparkline (resource history) for the full per-node panel.
export function ServiceControls({ serviceId, state }: Props) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [chaosOpen, setChaosOpen] = useState(false)
  const [chaosType, setChaosType] = useState<ChaosType>('Delay')
  const [amount, setAmount] = useState(500)
  const [duration, setDuration] = useState(20)

  const isRunning = state === 'running'

  async function runAction(action: () => Promise<unknown>) {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="service-controls">
      <div className="service-card-actions">
        <button
          disabled={busy}
          onClick={() => runAction(() => (isRunning ? controlApi.stop(serviceId) : controlApi.start(serviceId)))}
        >
          {isRunning ? 'Stop' : 'Start'}
        </button>
        <button disabled={busy} onClick={() => runAction(() => controlApi.restart(serviceId))}>
          Restart
        </button>
        <button disabled={busy} onClick={() => runAction(() => controlApi.heal(serviceId))}>
          Heal
        </button>
        <button disabled={busy} onClick={() => setChaosOpen((open) => !open)}>
          Degrade {chaosOpen ? '▴' : '▾'}
        </button>
      </div>

      {chaosOpen && (
        <div className="service-card-chaos">
          <select value={chaosType} onChange={(e) => setChaosType(e.target.value as ChaosType)}>
            <option value="Delay">Delay (ms)</option>
            <option value="Loss">Loss (%)</option>
            <option value="Partition">Partition (100% loss)</option>
          </select>
          {chaosType !== 'Partition' && (
            <input
              type="number"
              value={amount}
              onChange={(e) => setAmount(Number(e.target.value))}
              min={1}
              max={chaosType === 'Delay' ? 10000 : 100}
              aria-label="amount"
            />
          )}
          <input
            type="number"
            value={duration}
            onChange={(e) => setDuration(Number(e.target.value))}
            min={1}
            max={300}
            aria-label="duration seconds"
          />
          <button
            disabled={busy}
            onClick={() => runAction(() => controlApi.degrade(serviceId, { type: chaosType, amount, durationSeconds: duration }))}
          >
            Start
          </button>
        </div>
      )}

      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
