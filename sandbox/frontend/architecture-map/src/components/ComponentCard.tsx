import type { ArchComponent } from '../types/architecture'

interface Props {
  component: ArchComponent
}

// Read-only details for a node - purpose, stack, config, links to code. No live status or actions
// here; NodePanel composes this with ServiceControls + Sparkline for the pieces that need those.
export function ComponentCard({ component }: Props) {
  const { details } = component

  return (
    <div className="component-card">
      <p className="component-card-purpose">{details.purpose}</p>

      <div className="component-card-tags">
        {details.technologies.map((tech) => (
          <span key={tech} className="tag">
            {tech}
          </span>
        ))}
      </div>

      <dl className="component-card-facts">
        {details.port !== undefined && (
          <>
            <dt>Port</dt>
            <dd>{details.port}</dd>
          </>
        )}
        {details.managementPort !== undefined && (
          <>
            <dt>Management port</dt>
            <dd>{details.managementPort}</dd>
          </>
        )}
        {details.keyFormat && (
          <>
            <dt>Key format</dt>
            <dd>
              <code>{details.keyFormat}</code>
            </dd>
          </>
        )}
      </dl>

      {details.topology && <p className="component-card-note">{details.topology}</p>}

      {details.links && (
        <ul className="component-card-links">
          {Object.entries(details.links).map(([label, path]) => (
            <li key={label}>
              <span className="component-card-link-label">{label}:</span> <code>{path}</code>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
