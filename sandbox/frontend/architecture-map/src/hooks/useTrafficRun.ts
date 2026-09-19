import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { controlApi, ControlApiError } from '../api/controlApi'
import type { TrafficProgress, TrafficReport, TrafficRequest } from '../types/controlApi'

export interface TrafficRunState {
  running: boolean
  progress: TrafficProgress | null
  progressHistory: TrafficProgress[]
  report: TrafficReport | null
  error: string | null
  start: (request: TrafficRequest) => Promise<void>
  // Bumped (to the new run's id) whenever control-api finishes persisting a run snapshot - a
  // simple change signal RunHistoryPanel watches to refetch its list, instead of a second SignalR
  // connection just for that one event.
  lastSavedRunId: string | null
}

// Own SignalR connection (separate from useLiveStack's) - keeps this hook fully self-contained
// since it's only ever used by TrafficPanel. The run itself happens server-side; this just listens
// for the three events control-api pushes for it (trafficProgress*, trafficCompleted,
// trafficFailed) and starts it via a fire-and-forget POST.
export function useTrafficRun(): TrafficRunState {
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState<TrafficProgress | null>(null)
  const [progressHistory, setProgressHistory] = useState<TrafficProgress[]>([])
  const [report, setReport] = useState<TrafficReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [lastSavedRunId, setLastSavedRunId] = useState<string | null>(null)
  const connectionRef = useRef<signalR.HubConnection | null>(null)

  useEffect(() => {
    // Guards every setState against the StrictMode dev double-mount: the first effect instance's
    // connection.start() can still be in flight (and later reject with "stopped during
    // negotiation") after cleanup has already run, and since StrictMode's remount reuses the same
    // Fiber, that stale rejection would otherwise leak an error into the *second*, real instance.
    let cancelled = false
    const connection = new signalR.HubConnectionBuilder().withUrl(controlApi.hubUrl).withAutomaticReconnect().build()

    connection.on('trafficProgress', (p: TrafficProgress) => {
      if (cancelled) return
      setProgress(p)
      setProgressHistory((prev) => [...prev, p])
    })
    connection.on('trafficCompleted', (r: TrafficReport) => {
      if (cancelled) return
      setReport(r)
      setRunning(false)
      setProgress(null)
    })
    connection.on('trafficFailed', (e: { error: string }) => {
      if (cancelled) return
      setError(e.error)
      setRunning(false)
      setProgress(null)
    })
    connection.on('runSaved', (e: { id: string }) => {
      if (cancelled) return
      setLastSavedRunId(e.id)
    })

    connection.start().catch((err) => !cancelled && setError(String(err)))
    connectionRef.current = connection

    return () => {
      cancelled = true
      connection.stop()
    }
  }, [])

  async function start(request: TrafficRequest) {
    setError(null)
    setReport(null)
    setProgress(null)
    setProgressHistory([])
    setRunning(true)
    try {
      await controlApi.startTraffic(request)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
      setRunning(false)
    }
  }

  return { running, progress, progressHistory, report, error, start, lastSavedRunId }
}
