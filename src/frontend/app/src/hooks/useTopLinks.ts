import { useEffect, useState } from 'react'
import { createTopLinksConnection } from '../api/topLinksApi'
import type { TopLinkEntry } from '../types'

export type TopLinksStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

// Subscribes to the ReportingApi top-links hub while `enabled` is true. The server sends the current
// list on connect and again whenever it changes, so this only ever stores the latest list - no
// merging, no polling.
export function useTopLinks(enabled: boolean): { items: TopLinkEntry[]; status: TopLinksStatus } {
  const [items, setItems] = useState<TopLinkEntry[]>([])
  const [status, setStatus] = useState<TopLinksStatus>('connecting')

  useEffect(() => {
    if (!enabled) return

    const connection = createTopLinksConnection()
    connection.on('topLinks', (latest: TopLinkEntry[]) => setItems(latest))
    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(() => setStatus('live'))
    connection.onclose(() => setStatus('offline'))

    // StrictMode mounts, unmounts and remounts effects, so the first start() can still be pending
    // when stop() runs. The flag stops a late start() from reporting a status for a connection that
    // no longer belongs to this effect.
    let stopped = false
    connection
      .start()
      .then(() => {
        if (!stopped) setStatus('live')
      })
      .catch(() => {
        if (!stopped) setStatus('offline')
      })

    return () => {
      stopped = true
      setStatus('connecting')
      connection.stop().catch(() => undefined)
    }
  }, [enabled])

  return { items, status }
}
