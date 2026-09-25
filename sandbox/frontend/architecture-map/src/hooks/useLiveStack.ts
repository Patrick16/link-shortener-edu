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

    controlApi
      .listContainers()
      .then((list) => !cancelled && setContainers(groupByService(list)))
      .catch((err) => !cancelled && setError(String(err)))
      .finally(() => !cancelled && setLoading(false))

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(controlApi.hubUrl)
      .withAutomaticReconnect()
      .build()

    connection.on('containersUpdated', (list: ManagedContainer[]) => {
      if (cancelled) return
      // Always the full current set (see StatusPollerService), so replacing rather than merging
      // is what correctly reflects a replica actually being removed after a scale-down.
      setContainers(groupByService(list))
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
