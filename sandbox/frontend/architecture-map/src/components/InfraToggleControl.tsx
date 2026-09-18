interface Props {
  label: string
  description: string
  enabled: boolean
  busy: boolean
  onToggle: (next: boolean) => void
}

// One reusable on/off switch, used for all three standing infra toggles (nginx/pgcat/redis node
// panels) - same shape, different label/description/handler per node. Kept generic rather than
// three near-identical components.
export function InfraToggleControl({ label, description, enabled, busy, onToggle }: Props) {
  return (
    <div className="infra-toggle-control">
      <div className="infra-toggle-row">
        <span className="infra-toggle-label">{label}</span>
        <button
          onClick={() => onToggle(!enabled)}
          disabled={busy}
          className={enabled ? 'infra-toggle-btn infra-toggle-btn-on' : 'infra-toggle-btn infra-toggle-btn-off'}
        >
          {busy ? 'Applying...' : enabled ? 'ON' : 'OFF'}
        </button>
      </div>
      <p className="infra-toggle-description">{description}</p>
    </div>
  )
}
