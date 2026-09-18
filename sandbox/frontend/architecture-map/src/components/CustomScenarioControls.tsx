import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { CustomScenario } from '../types/controlApi'
import type { StagePoint } from './StageGraphEditor'

interface Props {
  disabled: boolean
  selectedEndpoints: string[]
  onEndpointsChange: (endpoints: string[]) => void
  points: StagePoint[]
  totalDurationSeconds: number
  onLoad: (scenario: CustomScenario) => void
  onReset: () => void
}

// Human-readable labels for DockerService.KnownEndpoints - the API only needs the keys.
const ENDPOINT_LABELS: Record<string, string> = {
  create: 'Create link (POST /Links)',
  redirect: 'Resolve link (GET redirect)',
  register: 'Register user (POST /register)',
}

// Endpoint checkboxes + save/load/delete for named custom scenarios - the load ramp itself stays
// in TrafficPanel's own StageGraphEditor (points/totalDurationSeconds are passed in just so Save
// can persist whatever's currently drawn).
export function CustomScenarioControls({
  disabled,
  selectedEndpoints,
  onEndpointsChange,
  points,
  totalDurationSeconds,
  onLoad,
  onReset,
}: Props) {
  const [endpointOptions, setEndpointOptions] = useState<string[]>([])
  const [scenarios, setScenarios] = useState<CustomScenario[]>([])
  const [selectedName, setSelectedName] = useState('')
  const [nameInput, setNameInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi.listEndpoints().then(setEndpointOptions).catch(() => {})
    refreshScenarios()
  }, [])

  function refreshScenarios() {
    controlApi.listScenarios().then(setScenarios).catch(() => {})
  }

  function toggleEndpoint(endpoint: string, checked: boolean) {
    onEndpointsChange(checked ? [...selectedEndpoints, endpoint] : selectedEndpoints.filter((e) => e !== endpoint))
  }

  function handleLoadChange(name: string) {
    setSelectedName(name)
    setError(null)
    if (!name) {
      setNameInput('')
      onReset()
      return
    }
    const found = scenarios.find((s) => s.name === name)
    if (found) {
      setNameInput(found.name)
      onLoad(found)
    }
  }

  async function handleSave() {
    setBusy(true)
    setError(null)
    try {
      const saved = await controlApi.saveScenario({
        name: nameInput.trim(),
        endpoints: selectedEndpoints,
        totalDurationSeconds,
        points,
      })
      refreshScenarios()
      setSelectedName(saved.name)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleDelete() {
    setBusy(true)
    setError(null)
    try {
      await controlApi.deleteScenario(selectedName)
      refreshScenarios()
      setSelectedName('')
      setNameInput('')
      onReset()
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="custom-scenario-controls">
      <div className="custom-scenario-row">
        <select value={selectedName} onChange={(e) => handleLoadChange(e.target.value)} disabled={disabled}>
          <option value="">+ New custom scenario</option>
          {scenarios.map((s) => (
            <option key={s.name} value={s.name}>
              {s.name}
            </option>
          ))}
        </select>
        {selectedName && (
          <button onClick={handleDelete} disabled={disabled || busy}>
            Delete
          </button>
        )}
      </div>

      <div className="custom-scenario-endpoints">
        {endpointOptions.map((endpoint) => (
          <label key={endpoint} className="custom-scenario-endpoint">
            <input
              type="checkbox"
              checked={selectedEndpoints.includes(endpoint)}
              disabled={disabled}
              onChange={(e) => toggleEndpoint(endpoint, e.target.checked)}
            />
            {ENDPOINT_LABELS[endpoint] ?? endpoint}
          </label>
        ))}
      </div>

      <div className="custom-scenario-row">
        <input
          type="text"
          value={nameInput}
          onChange={(e) => setNameInput(e.target.value)}
          placeholder="Scenario name"
          disabled={disabled}
        />
        <button onClick={handleSave} disabled={disabled || busy || !nameInput.trim() || selectedEndpoints.length === 0}>
          {busy ? 'Saving...' : selectedName ? 'Save changes' : 'Save as new'}
        </button>
      </div>

      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
