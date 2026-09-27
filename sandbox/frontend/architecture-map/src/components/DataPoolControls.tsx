import type { DataPoolMode, DataSourceDefinition } from '../types/controlApi'

// A number input's value can be a syntactically incomplete string while the user is still typing
// (a lone "-", clearing the field...) - Number(...) of that is NaN, and Math.max/Math.min propagate
// NaN through unchanged (no clamping actually applied), so this would otherwise be sent as JSON
// `null` in the traffic request instead of the friendlier `dataPool.count must be between 1 and
// 20000` validation message. Returns null (meaning "ignore this keystroke, don't call
// onCountChange yet") instead of falling back to a default, so a still-in-progress edit doesn't get
// silently overwritten. Exported as a pure function for direct testing - a native
// <input type="number">'s own value-sanitization strips most invalid interim strings down to ""
// before a DOM-driven test's onChange ever sees them (Number("") is 0, not NaN), masking this exact
// bug in a jsdom/RTL test.
export function clampDataPoolCount(rawValue: string): number | null {
  const parsed = Number(rawValue)
  return Number.isFinite(parsed) ? Math.max(1, Math.min(20_000, parsed)) : null
}

interface Props {
  disabled: boolean
  sources: DataSourceDefinition[]
  enabled: boolean
  onEnabledChange: (enabled: boolean) => void
  sourceId: string
  onSourceIdChange: (id: string) => void
  count: number
  onCountChange: (count: number) => void
  mode: DataPoolMode
  onModeChange: (mode: DataPoolMode) => void
}

// A sequence with no Create step of its own (e.g. testing "Resolve link" alone) has nothing real to
// resolve, so every run falls back to one fixture link created at setup - meaning every iteration
// hits the exact same record. This preloads Count real values from the app itself (see
// DataSourceDefinition on the backend) before the run starts, so those steps get varied real data
// instead. Off by default - it's an opt-in fix for that one gap, not something every run needs.
export function DataPoolControls({ disabled, sources, enabled, onEnabledChange, sourceId, onSourceIdChange, count, onCountChange, mode, onModeChange }: Props) {
  const selectedSource = sources.find((s) => s.id === sourceId)

  return (
    <div className="data-pool-controls">
      <label className="data-pool-toggle">
        <input type="checkbox" checked={enabled} onChange={(e) => onEnabledChange(e.target.checked)} disabled={disabled || sources.length === 0} />
        Preload real data for steps that need it
      </label>

      {enabled && (
        <div className="data-pool-fields">
          <label>
            Source
            <select value={sourceId} onChange={(e) => onSourceIdChange(e.target.value)} disabled={disabled}>
              {sources.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.id}
                </option>
              ))}
            </select>
          </label>
          <label>
            Count
            <input
              type="number"
              min={1}
              max={20_000}
              value={count}
              onChange={(e) => {
                const next = clampDataPoolCount(e.target.value)
                if (next !== null) {
                  onCountChange(next)
                }
              }}
              disabled={disabled}
            />
          </label>
          <label>
            Order
            <select value={mode} onChange={(e) => onModeChange(e.target.value as DataPoolMode)} disabled={disabled}>
              <option value="sequential">Sequential</option>
              <option value="random">Random</option>
            </select>
          </label>
          {selectedSource && <p className="scenario-description-text">{selectedSource.description}</p>}
        </div>
      )}
    </div>
  )
}
