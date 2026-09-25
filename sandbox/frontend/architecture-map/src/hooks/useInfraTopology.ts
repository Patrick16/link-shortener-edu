import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { controlApi } from '../api/controlApi'
import type { NodeRole } from '../types/controlApi'

function toMap(roles: NodeRole[]): Record<string, string> {
  const map: Record<string, string> = {}
  for (const r of roles) {
    map[r.serviceId] = r.role
  }
  return map
}

// Redis Sentinel and MongoDB's replica set can each re-elect their own leader with zero
// involvement from this app (see TopologyPollerService on the backend) - pushed over the same
// SignalR hub every other live status update already uses, not polled from the browser, so a
// failover shows up as soon as control-api itself notices it rather than up to a poll interval
// late, and the exec calls this needs happen once server-side no matter how many tabs are open.
// Own connection (like useTrafficRun's), not shared with useLiveStack's - keeps this hook fully
// self-contained since not every consumer of container/resource data cares about cluster roles.
export function useInfraTopology(): Record<string, string> {
  const [roles, setRoles] = useState<Record<string, string>>({})
  const connectionRef = useRef<signalR.HubConnection | null>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([controlApi.getRedisTopology(), controlApi.getMongoTopology()])
      .then(([redis, mongo]) => {
        if (cancelled) return
        setRoles(toMap([...redis.roles, ...mongo.roles]))
      })
      .catch(() => {})

    const connection = new signalR.HubConnectionBuilder().withUrl(controlApi.hubUrl).withAutomaticReconnect().build()

    connection.on('infraTopologyUpdated', (nodeRoles: NodeRole[]) => {
      if (cancelled) return
      // Always the full current set (see TopologyPollerService), so replacing rather than merging
      // is what correctly reflects a node that's dropped out of the topology entirely.
      setRoles(toMap(nodeRoles))
    })

    connection.start().catch(() => {})
    connectionRef.current = connection

    return () => {
      cancelled = true
      connection.stop()
    }
  }, [])

  return roles
}
