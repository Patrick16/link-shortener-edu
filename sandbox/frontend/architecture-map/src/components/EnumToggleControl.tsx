interface Option<T extends string> {
  value: T
  label: string
}

interface Props<T extends string> {
  label: string
  options: Option<T>[]
  value: T
  busy: boolean
  onChange: (next: T) => void
}

// Generalizes InfraToggleControl's "label + one active button" pattern from boolean to any small
// enum - a row of buttons with the current value highlighted, instead of a dropdown, to match the
// rest of this panel's one-glance-status style.
export function EnumToggleControl<T extends string>({ label, options, value, busy, onChange }: Props<T>) {
  return (
    <div className="enum-toggle-control">
      <span className="enum-toggle-label">{label}</span>
      <div className="enum-toggle-options">
        {options.map((option) => (
          <button
            key={option.value}
            disabled={busy}
            onClick={() => onChange(option.value)}
            className={option.value === value ? 'enum-toggle-btn enum-toggle-btn-active' : 'enum-toggle-btn'}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  )
}
