import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { EnumToggleControl } from './EnumToggleControl'
import type { MessagingMode } from '../types/controlApi'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

const OPTIONS: { value: MessagingMode; label: string }[] = [
  { value: 'rabbitmq', label: 'RabbitMQ (async)' },
  { value: 'grpc', label: 'gRPC (sync)' },
]

// Only recreates link-api/redirect-api - shortener-service/traffic-service/reporting-service
// always host their gRPC endpoint regardless of this setting (see
// GrpcMessagingExtensions.AddMessagingGrpcServer's own comment), so switching is faster than the
// pgcat/cache toggles, which touch every DB-touching service.
export function MessagingModeControl(_props: CapabilityControlProps) {
  const [mode, setMode] = useState<MessagingMode | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getInfraStatus()
      .then((s) => setMode(s.messagingMode))
      .catch(() => {})
  }, [])

  async function apply(next: MessagingMode) {
    setBusy(true)
    setError(null)
    try {
      const s = await controlApi.setMessagingMode(next)
      setMode(s.messagingMode)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (!mode) {
    return null
  }

  return (
    <div className="pgcat-pool-control">
      <EnumToggleControl label="Messaging transport" options={OPTIONS} value={mode} busy={busy} onChange={apply} />
      <p className="infra-toggle-description">
        rabbitmq: LinkApi/RedirectApi enqueue and return immediately, the publish happens off the request's
        critical path. grpc: the request waits for the downstream worker to persist the row - no fallback
        queue if it's unreachable, by design. Recreates link-api/redirect-api, takes a few seconds.
      </p>
      {error && <p className="service-card-error">{error}</p>}
    </div>
  )
}
