import type { ArchConnection } from '../types/architecture'

interface Props {
  connection: ArchConnection
  onClose: () => void
}

export function ConnectionDetail({ connection, onClose }: Props) {
  return (
    <div className="side-panel">
      <div className="side-panel-header">
        <h2>
          {connection.from} &rarr; {connection.to}
        </h2>
        <button onClick={onClose} aria-label="Close">
          &times;
        </button>
      </div>

      <p className="component-card-purpose">{connection.label}</p>

      <dl className="component-card-facts">
        <dt>Protocol</dt>
        <dd>{connection.protocol}</dd>
        {connection.format && (
          <>
            <dt>Format</dt>
            <dd>
              <code>{connection.format}</code>
            </dd>
          </>
        )}
        <dt>Mode</dt>
        <dd>{connection.synchronous ? 'Synchronous' : 'Asynchronous'}</dd>
      </dl>

      {connection.notes && <p className="component-card-note">{connection.notes}</p>}
    </div>
  )
}
