import type { ArchScenario } from '../types/architecture'

interface Props {
  scenarios: ArchScenario[]
  selected: string
  onSelect: (id: string) => void
}

export function ScenarioSwitcher({ scenarios, selected, onSelect }: Props) {
  const current = scenarios.find((s) => s.id === selected)

  return (
    <div className="scenario-switcher">
      <div className="scenario-switcher-tabs">
        {scenarios.map((s) => (
          <button
            key={s.id}
            className={s.id === selected ? 'scenario-tab active' : 'scenario-tab'}
            onClick={() => onSelect(s.id)}
          >
            {s.id}. {s.name}
          </button>
        ))}
      </div>
      {current && <p className="scenario-description">{current.description}</p>}
    </div>
  )
}
