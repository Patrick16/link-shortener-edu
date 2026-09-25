import { useEffect, useState } from 'react'
import type { AccessInfo } from '../utils/accessInfo'

interface Props {
  title: string
  info: AccessInfo
  onClose: () => void
}

// Kept out of NodePanel's own body on purpose (see quickLink.ts's comment on avoiding clutter) -
// this is only ever open on demand, one node at a time.
export function AccessInfoModal({ title, info, onClose }: Props) {
  const [copiedLabel, setCopiedLabel] = useState<string | null>(null)

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  useEffect(() => {
    if (!copiedLabel) return
    const timeout = setTimeout(() => setCopiedLabel(null), 1500)
    return () => clearTimeout(timeout)
  }, [copiedLabel])

  async function copy(label: string, value: string) {
    try {
      await navigator.clipboard.writeText(value)
      setCopiedLabel(label)
    } catch {
      // Clipboard API can be unavailable (no HTTPS/localhost, permissions) - the value is still
      // right there in the row to select by hand, so silently no-op rather than showing an error.
    }
  }

  return (
    <div className="access-modal-overlay" onClick={onClose}>
      <div className="access-modal" onClick={(e) => e.stopPropagation()}>
        <div className="access-modal-header">
          <h3>{title} - access info</h3>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        {info.note && <p className="access-modal-note">{info.note}</p>}

        <dl className="access-modal-entries">
          {info.entries.map((entry) => (
            <div className="access-modal-row" key={entry.label}>
              <dt>{entry.label}</dt>
              <dd>
                <code>{entry.value}</code>
                <button className="access-modal-copy-btn" onClick={() => copy(entry.label, entry.value)}>
                  {copiedLabel === entry.label ? 'Copied' : 'Copy'}
                </button>
              </dd>
            </div>
          ))}
        </dl>
      </div>
    </div>
  )
}
