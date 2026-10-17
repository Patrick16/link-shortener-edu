import { useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { ChaosType } from '../types/controlApi'

interface Props {
  serviceId: string
  state: string
}

// What each netem mode actually does to this one container's network namespace (see
// ChaosService.cs's own netemArgs switch) - not a backend/app-level fault, so the same three modes
// apply uniformly to every node regardless of what it is.
const CHAOS_TYPE_HELP: Record<ChaosType, string> = {
  Delay: 'Adds latency to every packet in and out of this container - simulates a slow dependency (a distant service, a congested link), without dropping anything.',
  Loss: 'Randomly drops the given percentage of packets in and out of this container - simulates a flaky, unreliable network link. Requests still get through, just not all of them.',
  Partition: 'Cuts all network traffic in and out of this container (100% loss, amount is ignored) - simulates a full outage, same as unplugging its network cable.',
}

const AMOUNT_LABEL: Record<ChaosType, string> = {
  Delay: 'Delay (ms)',
  Loss: 'Loss (%)',
  Partition: '',
}

// Delay's range is 1-10000 (ms), Loss's is 1-100 (%) - a value left over from one type is often
// out of range for the other (500 is a fine delay, not a percentage), so switching type reseeds
// amount with a sensible default for the one now showing, instead of silently carrying over a
// number that may no longer be valid.
const AMOUNT_DEFAULT: Record<ChaosType, number> = {
  Delay: 500,
  Loss: 50,
  Partition: 0,
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
          <label className="service-card-chaos-field">
            <span>Chaos type</span>
            <select
              value={chaosType}
              onChange={(e) => {
                const nextType = e.target.value as ChaosType
                setChaosType(nextType)
                setAmount(AMOUNT_DEFAULT[nextType])
              }}
            >
              <option value="Delay">Delay</option>
              <option value="Loss">Loss</option>
              <option value="Partition">Partition</option>
            </select>
          </label>

          <p className="infra-toggle-description">{CHAOS_TYPE_HELP[chaosType]}</p>

          <div className="service-card-chaos-row">
            {chaosType !== 'Partition' && (
              <label className="service-card-chaos-field">
                <span>{AMOUNT_LABEL[chaosType]}</span>
                <input
                  type="number"
                  value={amount}
                  onChange={(e) => setAmount(Number(e.target.value))}
                  min={1}
                  max={chaosType === 'Delay' ? 10000 : 100}
                />
              </label>
            )}
            <label className="service-card-chaos-field">
              <span>Duration (s)</span>
              <input
                type="number"
                value={duration}
                onChange={(e) => setDuration(Number(e.target.value))}
                min={1}
                max={300}
              />
            </label>
          </div>

          <p className="infra-toggle-description">
            Runs for the duration above, then reverts on its own - or click Heal (above) to stop it early.
          </p>

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
