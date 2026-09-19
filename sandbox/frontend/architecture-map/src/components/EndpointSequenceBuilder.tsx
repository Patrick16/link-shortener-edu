import { useEffect, useState } from 'react'
import type { EndpointDefinition, FlowStep } from '../types/controlApi'

interface Props {
  disabled: boolean
  endpoints: EndpointDefinition[]
  sequence: FlowStep[]
  onChange: (sequence: FlowStep[]) => void
}

// What a load test actually does, made explicit and orderable: pick a service, pick one of its
// real endpoints, add it as the next step. Order matters - a "Resolve link" step consumes the
// "hash" an earlier "Create link" step in the same sequence produced (see each endpoint's
// consumes/produces hint), so reordering or removing a step changes what actually happens, the
// same way it would if a real user did things in a different order. Each step's own pause (seconds
// to sleep after that call, before the next one) is what lets a Create→Resolve pair be given time
// for ShortenerService's async persist to land, or deliberately left at 0 to race it on purpose.
export function EndpointSequenceBuilder({ disabled, endpoints, sequence, onChange }: Props) {
  const services = [...new Set(endpoints.map((e) => e.serviceId))]
  const [service, setService] = useState(services[0] ?? '')
  const [endpointId, setEndpointId] = useState('')
  const endpointsForService = endpoints.filter((e) => e.serviceId === service)
  const byId = new Map(endpoints.map((e) => [e.id, e]))

  useEffect(() => {
    if (services.length > 0 && !service) setService(services[0])
  }, [services, service])

  useEffect(() => {
    if (endpointsForService.length > 0 && !endpointsForService.some((e) => e.id === endpointId)) {
      setEndpointId(endpointsForService[0].id)
    }
  }, [service, endpoints, endpointsForService, endpointId])

  function addStep() {
    if (!endpointId) return
    onChange([...sequence, { endpointId, pauseAfterSeconds: 0 }])
  }

  function removeStep(index: number) {
    onChange(sequence.filter((_, i) => i !== index))
  }

  function moveStep(index: number, direction: -1 | 1) {
    const target = index + direction
    if (target < 0 || target >= sequence.length) return
    const next = [...sequence]
    ;[next[index], next[target]] = [next[target], next[index]]
    onChange(next)
  }

  function setPause(index: number, pauseAfterSeconds: number) {
    const next = [...sequence]
    next[index] = { ...next[index], pauseAfterSeconds }
    onChange(next)
  }

  function selectAll() {
    onChange(endpoints.map((e) => ({ endpointId: e.id, pauseAfterSeconds: 0 })))
  }

  return (
    <div className="endpoint-sequence-builder">
      <div className="endpoint-sequence-add-row">
        <select value={service} onChange={(e) => setService(e.target.value)} disabled={disabled}>
          {services.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <select value={endpointId} onChange={(e) => setEndpointId(e.target.value)} disabled={disabled}>
          {endpointsForService.map((e) => (
            <option key={e.id} value={e.id}>
              {e.method} {e.pathTemplate}
            </option>
          ))}
        </select>
        <button onClick={addStep} disabled={disabled || !endpointId}>
          + Add step
        </button>
        <button onClick={selectAll} disabled={disabled || endpoints.length === 0}>
          Select all
        </button>
      </div>

      {sequence.length === 0 && <p className="endpoint-sequence-hint">No steps yet - add at least one endpoint above.</p>}

      <ol className="endpoint-sequence-list">
        {sequence.map((step, index) => {
          const ep = byId.get(step.endpointId)
          const hints = ep
            ? [ep.consumes.length > 0 && `needs: ${ep.consumes.join(', ')}`, Object.keys(ep.produces).length > 0 && `produces: ${Object.keys(ep.produces).join(', ')}`].filter(Boolean)
            : []
          return (
            <li key={`${step.endpointId}-${index}`} className="endpoint-sequence-step">
              <span className="endpoint-sequence-step-index">{index + 1}</span>
              <span className="endpoint-sequence-step-label">{ep ? `${ep.serviceId}: ${ep.method} ${ep.pathTemplate}` : step.endpointId}</span>
              {hints.length > 0 && <span className="endpoint-sequence-step-hint">{hints.join(' · ')}</span>}
              <label className="endpoint-sequence-step-pause">
                pause
                <input
                  type="number"
                  min={0}
                  max={30}
                  step={0.1}
                  value={step.pauseAfterSeconds}
                  disabled={disabled}
                  onChange={(e) => setPause(index, Math.max(0, Math.min(30, Number(e.target.value))))}
                />
                s
              </label>
              <div className="endpoint-sequence-step-actions">
                <button onClick={() => moveStep(index, -1)} disabled={disabled || index === 0} aria-label="Move up">
                  ↑
                </button>
                <button onClick={() => moveStep(index, 1)} disabled={disabled || index === sequence.length - 1} aria-label="Move down">
                  ↓
                </button>
                <button onClick={() => removeStep(index)} disabled={disabled} aria-label="Remove step">
                  ×
                </button>
              </div>
            </li>
          )
        })}
      </ol>
    </div>
  )
}
