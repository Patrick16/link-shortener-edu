import { useEffect, useState } from 'react'
import { controlApi, ControlApiError } from '../api/controlApi'
import { InfraToggleControl } from './InfraToggleControl'
import type { CapabilityControlProps } from '../utils/capabilityControlProps'

// Same self-contained, immediate-apply shape as L1CacheControl/NegativeCacheControl - a runtime
// knob, not an InfraStatus/Topology toggle (see IInfraToggleService's own comment on
// GetCircuitBreakersEnabled). Rendered on all 6 breaker-registering nodes (see architecture.json)
// rather than just one or two - toggling from any of them recreates all 6 together.
export function CircuitBreakerControl(_props: CapabilityControlProps) {
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    controlApi
      .getCircuitBreakersEnabled()
      .then((r) => setEnabled(r.enabled))
      .catch(() => {})
  }, [])

  async function apply(next: boolean) {
    setBusy(true)
    setError(null)
    try {
      const r = await controlApi.setCircuitBreakersEnabled(next)
      setEnabled(r.enabled)
    } catch (err) {
      setError(err instanceof ControlApiError ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  if (enabled === null) return null

  return (
    <>
      <InfraToggleControl
        label="Circuit breakers"
        description="On wraps every Redis/gRPC/RabbitMQ/ip-api.com call on this service in a Polly circuit breaker (opens after 10 failures in 30s, fails fast for 15s instead of paying the dependency's own timeout on every request). Off reverts to every call waiting out the real timeout, every time - the pre-backlog-item-1 baseline. Recreates all 6 breaker-registering services (auth-api, link-api, redirect-api, shortener-service, traffic-service, reporting-service)."
        enabled={enabled}
        busy={busy}
        onToggle={apply}
      />
      {error && <p className="service-card-error">{error}</p>}
    </>
  )
}
