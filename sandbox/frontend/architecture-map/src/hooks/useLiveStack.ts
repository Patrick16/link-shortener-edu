import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { controlApi } from '../api/controlApi'
import type { ManagedContainer, ResourceSample } from '../types/controlApi'

const RESOURCE_HISTORY_LIMIT = 30

export interface LiveStackState {
  // One entry per serviceId, holding every replica (usually just one) - sorted by
  // containerNumber so a scaled service's instance list renders in a stable order.
  containers: Record<string, ManagedContainer[]>
  // One entry per containerId (not serviceId) - a scaled service's replicas each get their own
  // history instead of sharing/overwriting one array, so per-instance charts are possible.
  resourceHistory: Record<string, ResourceSample[]>
  connected: boolean
  loading: boolean
  error: string | null
}

function groupByService(list: ManagedContainer[]): Record<string, ManagedContainer[]> {
  const byId: Record<string, ManagedContainer[]> = {}
  for (const container of list) {
    ;(byId[container.serviceId] ??= []).push(container)
  }
  for (const group of Object.values(byId)) {
    group.sort((a, b) => a.containerNumber - b.containerNumber)
  }
  return byId
}

// Container ids churn continuously here - every scale up/down and every container replacement
// mints a new id and abandons the old one. Without this, resourceHistory (keyed by containerId)
// keeps one permanently-retained ~30-sample array per retired container for as long as the tab
// stays open, which is the app's actual intended usage pattern (a long-running infra console), not
// an edge case. Mirrors the equivalent server-side prune added to ResourceStatsStore.
function pruneStaleHistory(
  history: Record<string, ResourceSample[]>,
  liveContainerIds: ReadonlySet<string>,
): Record<string, ResourceSample[]> {
  const staleIds = Object.keys(history).filter((id) => !liveContainerIds.has(id))
  if (staleIds.length === 0) {
    return history
  }

  const next = { ...history }
  for (const id of staleIds) {
    delete next[id]
  }
  return next
}

// One SignalR connection shared by both container status and resource stats - both are pushed
// over the same /hub/status hub (see StatusPollerService / ResourceStatsPollerService), so there's
// no reason to open two sockets for them.
export function useLiveStack(): LiveStackState {
  const [containers, setContainers] = useState<Record<string, ManagedContainer[]>>({})
  const [resourceHistory, setResourceHistory] = useState<Record<string, ResourceSample[]>>({})
  const [connected, setConnected] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const connectionRef = useRef<signalR.HubConnection | null>(null)

  useEffect(() => {
    let cancelled = false

    // Always the full current set (see StatusPollerService), so this is also the one place that
    // knows which containerIds are still live - the natural point to prune resourceHistory too.
    function applyContainers(list: ManagedContainer[]) {
      setContainers(groupByService(list))
      const liveContainerIds = new Set(list.map((c) => c.containerId))
      setResourceHistory((prev) => pruneStaleHistory(prev, liveContainerIds))
    }

    controlApi
      .listContainers()
      .then((list) => !cancelled && applyContainers(list))
      .catch((err) => !cancelled && setError(String(err)))
      .finally(() => !cancelled && setLoading(false))

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(controlApi.hubUrl)
      .withAutomaticReconnect()
      .build()

    connection.on('containersUpdated', (list: ManagedContainer[]) => {
      if (cancelled) return
      applyContainers(list)
    })

    connection.on('resourceStatsUpdated', (samples: ResourceSample[]) => {
      if (cancelled) return
      setResourceHistory((prev) => {
        const next = { ...prev }
        for (const sample of samples) {
          const existing = next[sample.containerId] ?? []
          next[sample.containerId] = [...existing, sample].slice(-RESOURCE_HISTORY_LIMIT)
        }
        return next
      })
    })

    connection.onreconnecting(() => !cancelled && setConnected(false))
    connection.onreconnected(() => !cancelled && setConnected(true))

    connection
      .start()
      .then(() => !cancelled && setConnected(true))
      .catch((err) => !cancelled && setError(String(err)))

    connectionRef.current = connection

    return () => {
      cancelled = true
      connection.stop()
    }
  }, [])

  return { containers, resourceHistory, connected, loading, error }
}
