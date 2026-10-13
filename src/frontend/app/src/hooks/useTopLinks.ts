import { useEffect, useState } from 'react'
import { createTopLinksConnection } from '../api/topLinksApi'
import type { TopLinkEntry } from '../types'

export type TopLinksStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

// How long to wait before trying to (re)start a connection that failed, or that the client's own
// automatic reconnect gave up on. Without this, a single failed start left the panel "offline"
// until a full page reload.
const RETRY_DELAY_MS = 10_000

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

    // StrictMode mounts, unmounts and remounts effects, so a start() can still be pending when
    // stop() runs. The flag stops any late callback from reporting a status or scheduling a retry
    // for a connection that no longer belongs to this effect.
    let stopped = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined

    function scheduleRetry() {
      if (stopped) return
      setStatus('offline')
      retryTimer = setTimeout(start, RETRY_DELAY_MS)
    }

    function start() {
      connection
        .start()
        .then(() => {
          if (!stopped) setStatus('live')
        })
        .catch(scheduleRetry)
    }

    // Fires once the client's own automatic reconnect has given up. Restarting the same connection
    // is allowed once it's disconnected, so the retry reuses it rather than building a new one.
    connection.onclose(scheduleRetry)

    start()

    return () => {
      stopped = true
      clearTimeout(retryTimer)
      setStatus('connecting')
      connection.stop().catch(() => undefined)
    }
  }, [enabled])

  return { items, status }
}
