import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { CustomScenario } from '../types/controlApi'

interface Props {
  disabled: boolean
  scenarioName: string
  onScenarioNameChange: (name: string) => void
  current: Omit<CustomScenario, 'name'>
  onLoad: (scenario: CustomScenario) => void
  onReset: () => void
}

// Save/load/delete of a named scenario - the endpoint sequence, ramp graph, and iteration settings
// all live elsewhere in TrafficPanel; this just persists whatever combination is currently set up
// (passed in as `current`), under a name, so it can be reloaded later.
export function CustomScenarioControls({ disabled, scenarioName, onScenarioNameChange, current, onLoad, onReset }: Props) {
  const [scenarios, setScenarios] = useState<CustomScenario[]>([])
  const [selectedName, setSelectedName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    refreshScenarios()
  }, [])

  function refreshScenarios() {
    controlApi.listScenarios().then(setScenarios).catch(() => {})
  }

  function handleLoadChange(name: string) {
    setSelectedName(name)
    setError(null)
    if (!name) {
      onScenarioNameChange('')
      onReset()
      return
    }
    const found = scenarios.find((s) => s.name === name)
    if (found) {
      onScenarioNameChange(found.name)
      onLoad(found)
    }
  }

  async function handleSave() {
    setBusy(true)
    setError(null)
    try {
      const saved = await controlApi.saveScenario({ name: scenarioName.trim(), ...current })
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
      onScenarioNameChange('')
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
          <option value="">+ New scenario</option>
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

      <div className="custom-scenario-row">
        <input
          type="text"
          value={scenarioName}
          onChange={(e) => onScenarioNameChange(e.target.value)}
          placeholder="Scenario name"
          disabled={disabled}
        />
        <button onClick={handleSave} disabled={disabled || busy || !scenarioName.trim() || current.steps.length === 0}>
          {busy ? 'Saving...' : selectedName ? 'Save changes' : 'Save as new'}
        </button>
      </div>

      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
