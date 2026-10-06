import { useCallback, useEffect, useState } from 'react'
import { controlApi } from '../api/controlApi'
import type { InfraStatus } from '../types/controlApi'

// Single shared fetch of the 4 topology-affecting infra settings (nginx bypass, pgcat enabled,
// cache enabled, messaging transport) - both the Topology panel's initial checkbox state and
// Diagram's "only show active participants" filter read from this one hook, instead of each
// control fetching its own copy the way the now-retired per-node toggles did. No polling, no
// SignalR push: nothing except this frontend's own Topology Save / Presets apply / reuse-a-run
// flow ever changes these settings, so each of those calls refresh() explicitly instead.
export function useInfraStatus() {
  const [status, setStatus] = useState<InfraStatus | null>(null)

  const refresh = useCallback(() => controlApi.getInfraStatus().then(setStatus).catch(() => {}), [])

  useEffect(() => {
    refresh()
  }, [refresh])

  return { status, refresh }
}
