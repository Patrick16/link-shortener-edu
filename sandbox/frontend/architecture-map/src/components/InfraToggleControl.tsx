interface Props {
  label: string
  description: string
  enabled: boolean
  busy: boolean
  onToggle: (next: boolean) => void
  // For a toggle that's only meaningful given another toggle's current state (e.g. the HAProxy
  // sub-toggle under Connection pooling) - greys the control out and skips onToggle instead of
  // hiding it, so the dependency stays visible rather than the control just disappearing.
  disabled?: boolean
  // Renders as a dependent of the row above (indented, no separator) instead of a sibling -
  // same HAProxy-under-pgcat case as `disabled`.
  nested?: boolean
}

// One reusable on/off switch, used for all three standing infra toggles (nginx/pgcat/redis node
// panels) - same shape, different label/description/handler per node. Kept generic rather than
// three near-identical components.
export function InfraToggleControl({ label, description, enabled, busy, onToggle, disabled, nested }: Props) {
  const className = ['infra-toggle-control', nested && 'infra-toggle-control-nested', disabled && 'infra-toggle-control-disabled']
    .filter(Boolean)
    .join(' ')

  return (
    <div className={className}>
      <div className="infra-toggle-row">
        <span className="infra-toggle-label">{label}</span>
        <button
          onClick={() => onToggle(!enabled)}
          disabled={busy || disabled}
          className={enabled ? 'infra-toggle-btn infra-toggle-btn-on' : 'infra-toggle-btn infra-toggle-btn-off'}
        >
          {busy ? 'Applying...' : enabled ? 'ON' : 'OFF'}
        </button>
      </div>
      <p className="infra-toggle-description">{description}</p>
    </div>
  )
}
