import type { TopLinkEntry } from '../types'
import type { TopLinksStatus } from '../hooks/useTopLinks'

const STATUS_LABEL: Record<TopLinksStatus, string> = {
  connecting: 'Connecting…',
  live: 'Live',
  reconnecting: 'Reconnecting…',
  offline: 'Offline',
}

type Props = {
  items: TopLinkEntry[]
  status: TopLinksStatus
}

export function TopLinksPanel({ items, status }: Props) {
  return (
    <section className="top-links" aria-labelledby="top-links-heading">
      <header className="top-links-header">
        <h3 id="top-links-heading">Top links, last 7 days</h3>
        <span className={`top-links-status top-links-status-${status}`}>{STATUS_LABEL[status]}</span>
      </header>

      {items.length === 0 ? (
        // Until the first list arrives, "no clicks" would be a lie - it is just not loaded yet.
        status === 'connecting' ? (
          <p className="hint">Loading…</p>
        ) : (
          <p className="hint">No clicks in the last 7 days yet.</p>
        )
      ) : (
        <ol className="top-links-list">
          {items.map((item) => (
            <li key={item.hash} className="top-links-item">
              <span className="top-links-hash">{item.hash}</span>
              <span className="top-links-clicks">
                {item.clicks} {item.clicks === 1 ? 'click' : 'clicks'}
              </span>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
