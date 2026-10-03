import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { applyInfraConfig, captureCurrentInfraConfig, diffInfraConfig, type ApplySystemConfigResult, type InfraConfigDiffRow } from '../utils/infraConfig'
import type { CustomScenario, InfraConfigSnapshot, Preset } from '../types/controlApi'

interface Props {
  onClose: () => void
  onLoadScenario: (scenario: CustomScenario) => void
}

// Which preset (if any) the "preview & apply" flow is currently working on - distinct from the
// plain list view and the "save current config" form, same one-of-three-views shape
// RunHistoryPanel already uses for list/detail/compare.
type View = { kind: 'list' } | { kind: 'save' } | { kind: 'preview'; preset: Preset }

// Global (not node-scoped) modal - a preset reconfigures many nodes at once, so unlike every
// other control here it needs to be reachable regardless of what's selected on the graph. Opened
// from a header button in App.tsx rather than from inside NodePanel/RunHistoryPanel.
export function PresetsModal({ onClose, onLoadScenario }: Props) {
  const [presets, setPresets] = useState<Preset[] | null>(null)
  const [scenarios, setScenarios] = useState<CustomScenario[]>([])
  const [error, setError] = useState<string | null>(null)
  const [view, setView] = useState<View>({ kind: 'list' })

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  function refresh() {
    Promise.all([controlApi.listPresets(), controlApi.listScenarios()])
      .then(([p, s]) => {
        setPresets(p)
        setScenarios(s)
      })
      .catch(() => setError('Failed to load presets - see the console for details.'))
  }

  useEffect(refresh, [])

  async function deletePreset(name: string) {
    if (!window.confirm(`Delete preset "${name}"? This cannot be undone.`)) return
    try {
      await controlApi.deletePreset(name)
      setPresets((prev) => prev?.filter((p) => p.name !== name) ?? null)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    }
  }

  return (
    <div className="presets-modal-overlay" onClick={onClose}>
      <div className="presets-modal" onClick={(e) => e.stopPropagation()}>
        <div className="presets-modal-header">
          <h3>Presets</h3>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        {error && <p className="service-card-error">{error}</p>}

        {view.kind === 'list' && (
          <PresetList
            presets={presets}
            scenarios={scenarios}
            onSaveNew={() => setView({ kind: 'save' })}
            onPreview={(preset) => setView({ kind: 'preview', preset })}
            onDelete={deletePreset}
          />
        )}

        {view.kind === 'save' && (
          <SavePresetForm
            scenarios={scenarios}
            onSaved={() => {
              refresh()
              setView({ kind: 'list' })
            }}
            onCancel={() => setView({ kind: 'list' })}
          />
        )}

        {view.kind === 'preview' && (
          <PreviewAndApply
            preset={view.preset}
            onLoadScenario={(scenario) => {
              onLoadScenario(scenario)
              onClose()
            }}
            onApplied={refresh}
            onBack={() => setView({ kind: 'list' })}
          />
        )}
      </div>
    </div>
  )
}

interface PresetListProps {
  presets: Preset[] | null
  scenarios: CustomScenario[]
  onSaveNew: () => void
  onPreview: (preset: Preset) => void
  onDelete: (name: string) => void
}

function PresetList({ presets, scenarios, onSaveNew, onPreview, onDelete }: PresetListProps) {
  const scenarioExists = (name: string) => scenarios.some((s) => s.name === name)

  return (
    <>
      <div className="presets-list-header">
        <p className="presets-modal-note">A preset bundles the stand's infra settings (and, optionally, a load-test profile) under a name you can reload later.</p>
        <button onClick={onSaveNew}>Save current config as new preset</button>
      </div>

      {presets === null && <p className="presets-modal-note">Loading...</p>}
      {presets !== null && presets.length === 0 && <p className="presets-empty">No presets yet - configure the stand the way you want, then save it.</p>}

      <ul className="presets-list">
        {presets?.map((preset) => (
          <li key={preset.name} className="presets-row">
            <div className="presets-row-main">
              <span className="presets-row-name">{preset.name}</span>
              {preset.scenarioName && (
                <span className="presets-row-scenario">
                  load profile: {preset.scenarioName}
                  {!scenarioExists(preset.scenarioName) && ' (deleted)'}
                </span>
              )}
            </div>
            <div className="presets-row-actions">
              <button onClick={() => onPreview(preset)}>Preview &amp; apply</button>
              <button className="presets-row-delete" onClick={() => onDelete(preset.name)}>
                Delete
              </button>
            </div>
          </li>
        ))}
      </ul>
    </>
  )
}

interface SavePresetFormProps {
  scenarios: CustomScenario[]
  onSaved: () => void
  onCancel: () => void
}

function SavePresetForm({ scenarios, onSaved, onCancel }: SavePresetFormProps) {
  const [name, setName] = useState('')
  const [scenarioName, setScenarioName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSave() {
    setBusy(true)
    setError(null)
    try {
      const config = await captureCurrentInfraConfig()
      await controlApi.savePreset({ name: name.trim(), config, scenarioName: scenarioName || null })
      onSaved()
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="presets-form">
      <label className="presets-form-row">
        Name
        <input type="text" value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Read-scaled" disabled={busy} autoFocus />
      </label>
      <label className="presets-form-row">
        Linked load profile (optional)
        <select value={scenarioName} onChange={(e) => setScenarioName(e.target.value)} disabled={busy}>
          <option value="">None</option>
          {scenarios.map((s) => (
            <option key={s.name} value={s.name}>
              {s.name}
            </option>
          ))}
        </select>
      </label>
      {error && <p className="service-card-error">{error}</p>}
      <div className="presets-form-actions">
        <button onClick={onCancel} disabled={busy}>
          Cancel
        </button>
        <button onClick={handleSave} disabled={busy || !name.trim()}>
          {busy ? 'Capturing current config...' : 'Save'}
        </button>
      </div>
    </div>
  )
}

interface PreviewAndApplyProps {
  preset: Preset
  onLoadScenario: (scenario: CustomScenario) => void
  onApplied: () => void
  onBack: () => void
}

function PreviewAndApply({ preset, onLoadScenario, onApplied, onBack }: PreviewAndApplyProps) {
  const [current, setCurrent] = useState<InfraConfigSnapshot | null>(null)
  const [diff, setDiff] = useState<InfraConfigDiffRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [applying, setApplying] = useState(false)
  const [result, setResult] = useState<ApplySystemConfigResult | null>(null)

  useEffect(() => {
    let cancelled = false
    Promise.all([captureCurrentInfraConfig(), controlApi.listScalableServices().catch(() => [])])
      .then(([liveConfig, scalableIds]) => {
        if (cancelled) return
        setCurrent(liveConfig)
        setDiff(diffInfraConfig(liveConfig, preset.config, scalableIds))
      })
      .catch(() => {
        if (!cancelled) setError('Failed to read the current stand configuration - see the console for details.')
      })
    return () => {
      cancelled = true
    }
  }, [preset])

  // Resolves preset.scenarioName against the live scenario list (it's a reference by name, not
  // an embedded copy - see Preset on the backend) and loads it into the k6 node's form if still
  // there; silently does nothing if the linked scenario was since renamed/deleted.
  async function loadLinkedScenario() {
    const name = preset.scenarioName
    if (!name) return
    const found = await controlApi
      .listScenarios()
      .then((all) => all.find((s) => s.name === name))
      .catch(() => undefined)
    if (found) onLoadScenario(found)
  }

  async function handleApply() {
    setApplying(true)
    setError(null)
    try {
      const applyResult = await applyInfraConfig(preset.config)
      setResult(applyResult)
      onApplied()
      // A preset is a snapshot of the whole stand, k6's own node included - so a cleanly applied
      // preset with a linked scenario loads it automatically rather than waiting for a second
      // click, the same way every other node's settings in the preset took effect without one.
      // Only on a clean apply, though: if something failed, stay put so the failures are visible
      // instead of navigating away from them.
      if (applyResult.failed.length === 0 && preset.scenarioName) {
        await loadLinkedScenario()
      }
    } catch {
      setError('Failed to apply this preset - see the console for details.')
    } finally {
      setApplying(false)
    }
  }

  return (
    <div className="presets-preview">
      <button className="presets-preview-back" onClick={onBack}>
        &larr; Back to presets
      </button>
      <h4 className="presets-preview-title">{preset.name}</h4>

      {error && <p className="service-card-error">{error}</p>}
      {!current && !error && <p className="presets-modal-note">Reading current configuration...</p>}

      {current && diff && !result && (
        <>
          {diff.length === 0 ? (
            <p className="presets-modal-note">The stand already matches this preset - nothing to change.</p>
          ) : (
            <div className="compare-modal-scroll">
              <table className="compare-table">
                <thead>
                  <tr>
                    <th></th>
                    <th>Current</th>
                    <th>This preset</th>
                  </tr>
                </thead>
                <tbody>
                  {diff.map((row) => (
                    <tr key={row.label} className="compare-row-diff">
                      <td className="compare-row-label">{row.label}</td>
                      <td>{row.current}</td>
                      <td>{row.desired}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {diff.length > 0 && (
            <div className="presets-form-actions">
              <button onClick={handleApply} disabled={applying}>
                {applying ? 'Applying...' : 'Apply'}
              </button>
            </div>
          )}
        </>
      )}

      {result && (
        <div className="presets-apply-result">
          <p className="presets-modal-note">
            Applied {result.applied.length} setting{result.applied.length === 1 ? '' : 's'}
            {result.failed.length > 0 && `, ${result.failed.length} failed (${result.failed.join(', ')})`}.
          </p>
          {/* Only shown when apply didn't already auto-load it (see handleApply) - i.e. some
              setting failed, so we stayed on this screen instead of navigating away. */}
          {result.failed.length > 0 && preset.scenarioName && (
            <button onClick={loadLinkedScenario}>Load &quot;{preset.scenarioName}&quot; into the k6 panel</button>
          )}
          <button
            onClick={() => {
              onApplied()
              onBack()
            }}
          >
            Done
          </button>
        </div>
      )}
    </div>
  )
}
