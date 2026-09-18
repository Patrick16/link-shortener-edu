import { useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { ManagedContainer } from '../types/controlApi'
import type { ArchComponent } from '../types/architecture'
import type { ChaosType } from '../types/controlApi'

interface Props {
  container: ManagedContainer
  meta?: ArchComponent
}

const STATE_COLOR: Record<string, string> = {
  running: '#22c55e',
  exited: '#ef4444',
  paused: '#f59e0b',
  restarting: '#f59e0b',
}

export function ServiceCard({ container, meta }: Props) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [chaosOpen, setChaosOpen] = useState(false)
  const [chaosType, setChaosType] = useState<ChaosType>('Delay')
  const [amount, setAmount] = useState(500)
  const [duration, setDuration] = useState(20)

  const isRunning = container.state === 'running'
  const dotColor = STATE_COLOR[container.state] ?? '#9ca3af'

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
    <div className="service-card">
      <div className="service-card-header">
        <span className="service-card-icon">{meta?.icon ?? '\u{1F4E6}'}</span>
        <div>
          <div className="service-card-name">{meta?.name ?? container.serviceId}</div>
          <div className="service-card-status">
            <span className="status-dot" style={{ background: dotColor }} />
            {container.status}
          </div>
        </div>
      </div>

      {meta?.description && <p className="service-card-description">{meta.description}</p>}

      <div className="service-card-actions">
        <button disabled={busy} onClick={() => runAction(() => (isRunning ? controlApi.stop(container.serviceId) : controlApi.start(container.serviceId)))}>
          {isRunning ? 'Stop' : 'Start'}
        </button>
        <button disabled={busy} onClick={() => runAction(() => controlApi.restart(container.serviceId))}>
          Restart
        </button>
        <button disabled={busy} onClick={() => runAction(() => controlApi.heal(container.serviceId))}>
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
            onClick={() => runAction(() => controlApi.degrade(container.serviceId, { type: chaosType, amount, durationSeconds: duration }))}
          >
            Start
          </button>
        </div>
      )}

      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
