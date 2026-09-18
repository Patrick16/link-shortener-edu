import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { controlApi } from '../api/controlApi'
import type { ManagedContainer } from '../types/controlApi'

export interface ContainerStatusState {
  containers: Record<string, ManagedContainer>
  connected: boolean
  loading: boolean
  error: string | null
}

// Initial snapshot comes from a plain GET (fast, works even if the hub takes a moment to connect);
// every state change after that comes over the SignalR push from StatusPollerService.
export function useContainerStatus(): ContainerStatusState {
  const [containers, setContainers] = useState<Record<string, ManagedContainer>>({})
  const [connected, setConnected] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const connectionRef = useRef<signalR.HubConnection | null>(null)

  useEffect(() => {
    let cancelled = false

    function applyList(list: ManagedContainer[]) {
      const byId: Record<string, ManagedContainer> = {}
      for (const container of list) {
        byId[container.serviceId] = container
      }
      if (!cancelled) {
        setContainers(byId)
      }
    }

    controlApi
      .listContainers()
      .then(applyList)
      .catch((err) => !cancelled && setError(String(err)))
      .finally(() => !cancelled && setLoading(false))

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(controlApi.hubUrl)
      .withAutomaticReconnect()
      .build()

    connection.on('containersUpdated', (list: ManagedContainer[]) => applyList(list))
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

  return { containers, connected, loading, error }
}
